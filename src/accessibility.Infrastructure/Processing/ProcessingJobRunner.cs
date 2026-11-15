using System.Security.Cryptography;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Processing;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Processing;

public sealed class ProcessingJobRunner(
    AccessLearnDbContext database,
    IObjectStorage objectStorage,
    IMediaProcessingProvider mediaProcessor,
    ITranscriptionProvider transcriptionProvider,
    ITranslationProvider translationProvider,
    IBackgroundJobScheduler scheduler,
    ProcessingCancellationRegistry cancellationRegistry,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ProcessingJobRunner> logger)
{
    [Queue("processing")]
    [AutomaticRetry(Attempts = 0)]
    public async Task ExecuteAsync(Guid processingJobId)
    {
        var claim = await TryClaimAsync(processingJobId);
        if (claim is null) return;
        var (job, attempt, leaseId) = claim.Value;

        var workspace = Path.Combine(Path.GetTempPath(), "AccessLearn", "processing", job.Id.ToString("N"));
        ProcessingCancellationRegistry.Registration? cancellation = null;
        CancellationTokenSource? executionCancellation = null;
        Task? heartbeat = null;
        var heartbeatState = new HeartbeatState();
        var cancellationToken = CancellationToken.None;
        try
        {
            Directory.CreateDirectory(workspace);
            RestrictWorkspacePermissions(workspace);
            cancellation = cancellationRegistry.Register(job.Id);
            executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            cancellationToken = executionCancellation.Token;
            heartbeat = RunLeaseHeartbeatAsync(job.Id, leaseId, executionCancellation, heartbeatState);
            if (await CancelIfRequestedAsync(job, attempt, leaseId)) return;
            var payload = JsonSerializer.Deserialize<PrepareAccessibilityPayload>(job.PayloadJson ?? string.Empty)
                ?? throw new FileContentInvalidException("Processing job payload is invalid.");
            var source = await (
                    from asset in database.MediaAssets
                    join file in database.StoredFiles on asset.StoredFileId equals file.Id
                    join lesson in database.Lessons on asset.LessonId equals lesson.Id
                    where asset.Id == payload.SourceAssetId && asset.IsActive && asset.Status == ResourceStatus.Ready &&
                          file.Status == StoredFileStatus.Ready
                    select new { Asset = asset, File = file, Lesson = lesson })
                .SingleAsync();
            if (await CancelIfRequestedAsync(job, attempt, leaseId)) return;

            var sourcePath = Path.Combine(workspace, "source" + Path.GetExtension(source.File.OriginalFileName));
            await using (var stored = await objectStorage.OpenReadAsync(source.File.ObjectKey, cancellationToken))
            await using (var output = new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await stored.Content.CopyToAsync(output, cancellationToken);
            }
            await ReportProgressAsync(job, leaseId, 15);

            var prepared = await mediaProcessor.PrepareAsync(
                sourcePath,
                workspace,
                source.Asset.Role == MediaAssetRole.PrimaryVideo,
                cancellationToken);
            if (await CancelIfRequestedAsync(job, attempt, leaseId)) return;
            await PersistOfflineAudioAsync(job, source.Asset, source.Lesson, prepared, cancellationToken);
            if (prepared.PosterPath is not null)
            {
                await PersistPosterAsync(job, source.Asset, source.Lesson, prepared.PosterPath, cancellationToken);
            }
            await ReportProgressAsync(job, leaseId, 35);

            var sourceLanguage = source.Lesson.SourceLanguage;
            Transcript? sourceTranscript;
            ProviderTranscript providerTranscript;
            if (payload.SourceTranscriptId.HasValue)
            {
                sourceTranscript = await database.Transcripts.SingleOrDefaultAsync(transcript =>
                    transcript.Id == payload.SourceTranscriptId.Value &&
                    transcript.ReviewStatus == ReviewStatus.Approved &&
                    transcript.Version == payload.SourceTranscriptVersion &&
                    transcript.SourceMediaAssetId == source.Asset.Id &&
                    transcript.SourceMediaVersion == source.Asset.Version,
                    cancellationToken)
                    ?? throw new StateConflictException("The reviewed source transcript changed before translation.");
                providerTranscript = await LoadProviderTranscriptAsync(sourceTranscript, cancellationToken);
                if (Fingerprint(providerTranscript.Segments) != payload.SourceTranscriptFingerprint)
                {
                    throw new StateConflictException("The reviewed source transcript content changed before translation.");
                }
            }
            else
            {
                sourceTranscript = await database.Transcripts.SingleOrDefaultAsync(transcript =>
                    transcript.ProcessingJobId == job.Id && transcript.Language == sourceLanguage,
                    cancellationToken);
                if (sourceTranscript is null)
                {
                    providerTranscript = await transcriptionProvider.TranscribeAsync(
                        prepared.SpeechWavPath,
                        sourceLanguage,
                        cancellationToken);
                    sourceTranscript = await PersistGeneratedTranscriptAsync(
                        job,
                        source.Asset,
                        source.Lesson,
                        providerTranscript.Language,
                        providerTranscript.Segments,
                        sourceTranscriptId: null,
                        transcriptionProvider.Name,
                        transcriptionProvider.Version,
                        cancellationToken);
                }
                else
                {
                    providerTranscript = await LoadProviderTranscriptAsync(sourceTranscript, cancellationToken);
                }
            }
            await ReportProgressAsync(job, leaseId, 70);
            if (await CancelIfRequestedAsync(job, attempt, leaseId)) return;

            foreach (var targetLanguage in payload.TargetLanguages.Where(language => language != sourceLanguage))
            {
                if (await database.Transcripts.AnyAsync(transcript =>
                        transcript.ProcessingJobId == job.Id && transcript.Language == targetLanguage))
                {
                    continue;
                }

                var translated = await translationProvider.TranslateAsync(
                    providerTranscript.Segments.Select(segment => segment.Text).ToArray(),
                    sourceLanguage,
                    targetLanguage,
                    cancellationToken);
                var translatedSegments = providerTranscript.Segments.Select((segment, index) =>
                    new ProviderTranscriptSegment(
                        index,
                        segment.StartMilliseconds,
                        segment.EndMilliseconds,
                        translated[index],
                        segment.SpeakerLabel)).ToArray();
                await PersistGeneratedTranscriptAsync(
                    job,
                    source.Asset,
                    source.Lesson,
                    targetLanguage,
                    translatedSegments,
                    sourceTranscript.Id,
                    translationProvider.Name,
                    translationProvider.Version,
                    cancellationToken);
            }

            await ReportProgressAsync(job, leaseId, 95);
            if (await CancelIfRequestedAsync(job, attempt, leaseId)) return;
            await EnsureLeaseAsync(job, leaseId);
            job.Succeed(timeProvider.GetUtcNow());
            attempt.Succeed(timeProvider.GetUtcNow());
            await database.SaveChangesAsync();
        }
        catch (OperationCanceledException)
        {
            if (heartbeatState.Failed)
            {
                await HandleFailureAsync(
                    job,
                    attempt,
                    leaseId,
                    new ProcessorUnavailableException("The processing lease heartbeat failed.", heartbeatState.Exception));
                return;
            }
            await database.Entry(job).ReloadAsync();
            if (job.ExecutionLeaseId != leaseId) return;
            if (job.Status is ProcessingJobStatus.Queued or ProcessingJobStatus.Running)
            {
                job.Cancel(timeProvider.GetUtcNow());
                attempt.Cancel(timeProvider.GetUtcNow());
                await database.SaveChangesAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            if (exception is ProcessingLeaseLostException) return;
            await HandleFailureAsync(job, attempt, leaseId, exception);
        }
        finally
        {
            executionCancellation?.Cancel();
            if (heartbeat is not null)
            {
                try { await heartbeat; }
                catch (OperationCanceledException) { }
            }
            executionCancellation?.Dispose();
            cancellation?.Dispose();
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not clean processing workspace {Workspace}", workspace);
            }
        }
    }

    private async Task<(ProcessingJob Job, ProcessingJobAttempt Attempt, Guid LeaseId)?> TryClaimAsync(Guid jobId)
    {
        await using var transaction = await database.Database.BeginTransactionAsync();
        var job = await database.ProcessingJobs
            .FromSqlInterpolated($"SELECT * FROM processing_jobs WHERE id = {jobId} FOR UPDATE")
            .SingleOrDefaultAsync();
        if (job is null) throw new ResourceNotFoundException("Processing job", jobId);
        if (job.Status == ProcessingJobStatus.Cancelled || job.IsCancellationRequested ||
            job.Status == ProcessingJobStatus.Succeeded || job.Attempts >= 4 ||
            job.Status == ProcessingJobStatus.Running && job.LeaseExpiresAt > timeProvider.GetUtcNow())
        {
            await transaction.CommitAsync();
            return null;
        }
        if (job.Status == ProcessingJobStatus.Running)
        {
            job.Fail("LEASE_EXPIRED", "The previous processing lease expired.", timeProvider.GetUtcNow());
            var expiredAttempt = await database.ProcessingJobAttempts.SingleOrDefaultAsync(
                item => item.ProcessingJobId == job.Id && item.AttemptNumber == job.Attempts);
            if (expiredAttempt?.Status == ProcessingJobStatus.Running)
            {
                expiredAttempt.Fail(
                    "LEASE_EXPIRED",
                    "The previous processing lease expired.",
                    timeProvider.GetUtcNow());
            }
            job.Requeue();
        }

        job.Start(timeProvider.GetUtcNow(), transcriptionProvider.Name, job.ExternalJobId);
        var leaseId = Guid.CreateVersion7();
        job.AcquireLease(leaseId, timeProvider.GetUtcNow().AddMinutes(45));
        job.ReportProgress(5);
        var attempt = new ProcessingJobAttempt(job.Id, job.Attempts, transcriptionProvider.Name, timeProvider.GetUtcNow());
        database.ProcessingJobAttempts.Add(attempt);
        await database.SaveChangesAsync();
        await transaction.CommitAsync();
        return (job, attempt, leaseId);
    }

    private async Task ReportProgressAsync(ProcessingJob job, Guid leaseId, int progress)
    {
        await EnsureLeaseAsync(job, leaseId);
        job.ReportProgress(progress);
        job.RenewLease(leaseId, timeProvider.GetUtcNow().AddMinutes(45));
        await database.SaveChangesAsync();
    }

    private async Task EnsureLeaseAsync(ProcessingJob job, Guid leaseId)
    {
        await database.Entry(job).ReloadAsync();
        if (job.Status != ProcessingJobStatus.Running || job.ExecutionLeaseId != leaseId)
        {
            throw new ProcessingLeaseLostException();
        }
    }

    private async Task RunLeaseHeartbeatAsync(
        Guid jobId,
        Guid leaseId,
        CancellationTokenSource executionCancellation,
        HeartbeatState state)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            while (await timer.WaitForNextTickAsync(executionCancellation.Token))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var heartbeatDatabase = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
                var expiresAt = timeProvider.GetUtcNow().AddMinutes(45);
                var updated = await heartbeatDatabase.ProcessingJobs
                    .Where(item => item.Id == jobId && item.Status == ProcessingJobStatus.Running &&
                                   item.ExecutionLeaseId == leaseId)
                    .ExecuteUpdateAsync(
                        updates => updates.SetProperty(item => item.LeaseExpiresAt, expiresAt),
                        executionCancellation.Token);
                if (updated == 0)
                {
                    executionCancellation.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (executionCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Lease heartbeat failed for processing job {ProcessingJobId}", jobId);
            state.Failed = true;
            state.Exception = exception;
            executionCancellation.Cancel();
        }
    }

    private async Task HandleFailureAsync(
        ProcessingJob job,
        ProcessingJobAttempt attempt,
        Guid leaseId,
        Exception exception)
    {
        logger.LogError(exception, "Processing job {ProcessingJobId} failed", job.Id);
        await database.Entry(job).ReloadAsync();
        if (job.ExecutionLeaseId != leaseId) return;
        if (job.Status == ProcessingJobStatus.Running)
        {
            var code = exception is AccessLearnException accessLearn ? accessLearn.Code : "PROCESSING_FAILED";
            var message = exception is AccessLearnException ? exception.Message : "The processing workflow failed.";
            job.Fail(code, message, timeProvider.GetUtcNow());
            attempt.Fail(code, message, timeProvider.GetUtcNow());
            await database.SaveChangesAsync(CancellationToken.None);
        }
        if (IsTransient(exception) && job.Attempts < 4)
        {
            job.Requeue();
            await database.SaveChangesAsync(CancellationToken.None);
            try
            {
                var delays = new[] { 10, 60, 300 };
                job.SetExternalJobId(scheduler.Schedule(
                    job.Id,
                    TimeSpan.FromSeconds(delays[Math.Min(job.Attempts - 1, delays.Length - 1)])));
                await database.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception scheduleException)
            {
                logger.LogWarning(
                    scheduleException,
                    "Processing job {ProcessingJobId} remains queued for reconciliation",
                    job.Id);
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is ProcessorUnavailableException or StorageUnavailableException or TimeoutException;

    private sealed class ProcessingLeaseLostException : Exception
    {
    }

    private sealed class HeartbeatState
    {
        public bool Failed { get; set; }
        public Exception? Exception { get; set; }
    }

    private async Task<bool> CancelIfRequestedAsync(
        ProcessingJob job,
        ProcessingJobAttempt attempt,
        Guid leaseId)
    {
        await database.Entry(job).ReloadAsync();
        if (job.ExecutionLeaseId != leaseId) return true;
        if (!job.IsCancellationRequested && job.Status != ProcessingJobStatus.Cancelled) return false;
        if (job.Status != ProcessingJobStatus.Cancelled) job.Cancel(timeProvider.GetUtcNow());
        attempt.Cancel(timeProvider.GetUtcNow());
        await database.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    private async Task PersistOfflineAudioAsync(
        ProcessingJob job,
        MediaAsset source,
        Lesson lesson,
        PreparedMediaFiles prepared,
        CancellationToken cancellationToken)
    {
        if (await database.MediaAssets.AnyAsync(asset =>
                asset.LessonId == lesson.Id && asset.Role == MediaAssetRole.OfflineAudio && asset.IsActive &&
                asset.Status == ResourceStatus.Ready && asset.SourceMediaAssetId == source.Id &&
                asset.SourceMediaVersion == source.Version,
                cancellationToken))
        {
            return;
        }
        var (sha256, byteLength) = await HashFileAsync(prepared.OfflineAudioPath, cancellationToken);
        var objectKey = $"lessons/{lesson.Id:N}/processing/{job.Id:N}/offline{prepared.OfflineAudioExtension}";
        var existing = await (
                from fileCandidate in database.StoredFiles
                join assetCandidate in database.MediaAssets on fileCandidate.Id equals assetCandidate.StoredFileId
                where fileCandidate.ObjectKey == objectKey
                select new { File = fileCandidate, Asset = assetCandidate })
            .SingleOrDefaultAsync(cancellationToken);
        StoredFile file;
        MediaAsset asset;
        if (existing is null)
        {
            file = new StoredFile(
                objectKey,
                $"offline{prepared.OfflineAudioExtension}",
                prepared.OfflineAudioContentType,
                byteLength,
                sha256);
            asset = new MediaAsset(
                lesson.Id,
                file.Id,
                MediaAssetRole.OfflineAudio,
                lesson.SourceLanguage,
                sourceMediaAssetId: source.Id,
                sourceMediaVersion: source.Version,
                processingJobId: job.Id);
            database.StoredFiles.Add(file);
            database.MediaAssets.Add(asset);
            await database.SaveChangesAsync(cancellationToken);
        }
        else
        {
            file = existing.File;
            asset = existing.Asset;
            if (file.Status is StoredFileStatus.Deleting or StoredFileStatus.Deleted)
            {
                throw new StateConflictException("Offline audio cleanup already claimed this output; create a new job.");
            }
            if (file.Sha256 != sha256 || file.ByteLength != byteLength)
            {
                if (file.Status == StoredFileStatus.Ready)
                {
                    throw new StateConflictException("Ready offline audio does not match regenerated output.");
                }
                file.RefreshQuarantineMetadata(prepared.OfflineAudioContentType, byteLength, sha256);
            }
            if (file.Status == StoredFileStatus.Ready && asset.Status == ResourceStatus.Ready) return;
            if (file.Status == StoredFileStatus.Rejected) file.RetryQuarantine();
            if (asset.Status == ResourceStatus.Rejected) asset.Retry();
        }
        try
        {
            await using var content = new FileStream(
                prepared.OfflineAudioPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var write = await objectStorage.PutAsync(
                objectKey,
                content,
                prepared.OfflineAudioContentType,
                byteLength,
                new Dictionary<string, string> { ["sha256"] = sha256, ["processing-job-id"] = job.Id.ToString("N") },
                cancellationToken);
            file.MarkReady(write.ETag);
            asset.MarkReady(source.DurationMilliseconds, null, null, "opus");
            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            asset.Reject();
            file.Reject("Offline audio generation did not complete.");
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<Transcript> PersistGeneratedTranscriptAsync(
        ProcessingJob job,
        MediaAsset source,
        Lesson lesson,
        string language,
        IReadOnlyList<ProviderTranscriptSegment> segments,
        Guid? sourceTranscriptId,
        string provider,
        string providerVersion,
        CancellationToken cancellationToken)
    {
        var existing = await database.Transcripts.SingleOrDefaultAsync(transcript =>
            transcript.ProcessingJobId == job.Id && transcript.Language == language,
            cancellationToken);
        if (existing is not null) return existing;
        if (source.DurationMilliseconds is null || segments.Count == 0)
        {
            throw new FileContentInvalidException("Generated transcript timing cannot be validated.");
        }
        long previousEnd = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (segment.Sequence != index || segment.StartMilliseconds < previousEnd ||
                segment.EndMilliseconds <= segment.StartMilliseconds ||
                segment.EndMilliseconds > source.DurationMilliseconds.Value ||
                string.IsNullOrWhiteSpace(segment.Text))
            {
                throw new FileContentInvalidException("Generated transcript segment timing is invalid.");
            }
            previousEnd = segment.EndMilliseconds;
        }
        var revision = (await database.Transcripts
            .Where(transcript => transcript.LessonId == lesson.Id && transcript.Language == language)
            .Select(transcript => (int?)transcript.Revision)
            .MaxAsync(cancellationToken) ?? 0) + 1;
        var transcript = new Transcript(
            lesson.Id,
            language,
            revision,
            TranscriptOrigin.Generated,
            lesson.DraftRevision,
            sourceTranscriptId,
            source.Id,
            source.Version,
            job.Id,
            provider,
            providerVersion);
        database.Transcripts.Add(transcript);
        database.TranscriptSegments.AddRange(segments.Select(segment => new TranscriptSegment(
            transcript.Id,
            segment.Sequence,
            segment.StartMilliseconds,
            segment.EndMilliseconds,
            segment.Text,
            segment.SpeakerLabel)));
        await database.SaveChangesAsync(cancellationToken);
        return transcript;
    }

    private async Task PersistPosterAsync(
        ProcessingJob job,
        MediaAsset source,
        Lesson lesson,
        string posterPath,
        CancellationToken cancellationToken)
    {
        if (await database.MediaAssets.AnyAsync(asset =>
                asset.LessonId == lesson.Id && asset.Role == MediaAssetRole.Poster && asset.IsActive &&
                asset.Status == ResourceStatus.Ready && asset.SourceMediaAssetId == source.Id &&
                asset.SourceMediaVersion == source.Version,
                cancellationToken))
        {
            return;
        }
        var (sha256, byteLength) = await HashFileAsync(posterPath, cancellationToken);
        var objectKey = $"lessons/{lesson.Id:N}/processing/{job.Id:N}/poster.jpg";
        var existing = await (
                from fileCandidate in database.StoredFiles
                join assetCandidate in database.MediaAssets on fileCandidate.Id equals assetCandidate.StoredFileId
                where fileCandidate.ObjectKey == objectKey
                select new { File = fileCandidate, Asset = assetCandidate })
            .SingleOrDefaultAsync(cancellationToken);
        StoredFile file;
        MediaAsset asset;
        if (existing is null)
        {
            file = new StoredFile(objectKey, "poster.jpg", "image/jpeg", byteLength, sha256);
            asset = new MediaAsset(
                lesson.Id,
                file.Id,
                MediaAssetRole.Poster,
                lesson.SourceLanguage,
                sourceMediaAssetId: source.Id,
                sourceMediaVersion: source.Version,
                processingJobId: job.Id);
            database.StoredFiles.Add(file);
            database.MediaAssets.Add(asset);
            await database.SaveChangesAsync(cancellationToken);
        }
        else
        {
            file = existing.File;
            asset = existing.Asset;
            if (file.Status is StoredFileStatus.Deleting or StoredFileStatus.Deleted)
            {
                throw new StateConflictException("Poster cleanup already claimed this output; create a new job.");
            }
            if (file.Sha256 != sha256 || file.ByteLength != byteLength)
            {
                if (file.Status == StoredFileStatus.Ready)
                {
                    throw new StateConflictException("Ready poster does not match regenerated output.");
                }
                file.RefreshQuarantineMetadata("image/jpeg", byteLength, sha256);
            }
            if (file.Status == StoredFileStatus.Ready && asset.Status == ResourceStatus.Ready) return;
            if (file.Status == StoredFileStatus.Rejected) file.RetryQuarantine();
            if (asset.Status == ResourceStatus.Rejected) asset.Retry();
        }
        try
        {
            await using var content = new FileStream(
                posterPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var write = await objectStorage.PutAsync(
                objectKey,
                content,
                "image/jpeg",
                byteLength,
                new Dictionary<string, string> { ["sha256"] = sha256, ["processing-job-id"] = job.Id.ToString("N") },
                cancellationToken);
            file.MarkReady(write.ETag);
            asset.MarkReady(source.DurationMilliseconds, 640, null, "mjpeg");
            await database.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            asset.Reject();
            file.Reject("Poster generation did not complete.");
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ProviderTranscript> LoadProviderTranscriptAsync(
        Transcript transcript,
        CancellationToken cancellationToken)
    {
        var segments = await database.TranscriptSegments
            .Where(segment => segment.TranscriptId == transcript.Id)
            .OrderBy(segment => segment.Sequence)
            .Select(segment => new ProviderTranscriptSegment(
                segment.Sequence,
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                segment.Text,
                segment.SpeakerLabel))
            .ToListAsync(cancellationToken);
        return new ProviderTranscript(transcript.Language, segments);
    }

    private static async Task<(string Sha256, long ByteLength)> HashFileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long length = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
            length += read;
        }
        return (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), length);
    }

    private static string Fingerprint(IReadOnlyList<ProviderTranscriptSegment> segments)
    {
        var content = string.Join('\n', segments.Select(segment =>
            $"{segment.Sequence}|{segment.StartMilliseconds}|{segment.EndMilliseconds}|{segment.Text}|{segment.SpeakerLabel}"));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
    }

    private static void RestrictWorkspacePermissions(string workspace)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                workspace,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
