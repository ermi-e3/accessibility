using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Processing;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Processing;

public sealed class ProcessingService(
    AccessLearnDbContext database,
    IUserContext userContext,
    IBackgroundJobScheduler scheduler,
    ITranscriptionProvider transcription,
    ITranslationProvider translation,
    ITranscriptFormatService formats,
    ProcessingCancellationRegistry cancellationRegistry,
    TimeProvider timeProvider,
    Microsoft.Extensions.Logging.ILogger<ProcessingService> logger) : IProcessingService
{
    private const string WorkflowVersion = "prepare-accessibility-v1";

    public async Task EnsureLessonAccessAsync(Guid lessonId, CancellationToken cancellationToken)
    {
        if (!await VisibleLessons().AnyAsync(lesson => lesson.Id == lessonId, cancellationToken))
        {
            throw new ResourceNotFoundException("Lesson", lessonId);
        }
    }

    public async Task<ProcessingJobModel> StartAsync(
        Guid lessonId,
        StartProcessingCommand command,
        string? requestIdempotencyKey,
        CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        var source = await (
                from asset in database.MediaAssets
                join file in database.StoredFiles on asset.StoredFileId equals file.Id
                where asset.Id == command.SourceAssetId && asset.LessonId == lessonId && asset.IsActive &&
                      asset.Status == ResourceStatus.Ready && file.Status == StoredFileStatus.Ready &&
                      (asset.Role == MediaAssetRole.PrimaryVideo || asset.Role == MediaAssetRole.PrimaryAudio)
                select new { Asset = asset, File = file })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Ready source media", command.SourceAssetId);
        var targets = NormalizeLanguages(command.TargetLanguages ?? [], lesson.SourceLanguage);
        var providerVersion = $"{WorkflowVersion}:{transcription.Version}:{translation.Version}";
        var latestApprovedSource = await database.Transcripts
            .Where(transcript => transcript.LessonId == lesson.Id &&
                                 transcript.Language == lesson.SourceLanguage &&
                                 transcript.ReviewStatus == ReviewStatus.Approved &&
                                 transcript.SourceMediaAssetId == source.Asset.Id &&
                                 transcript.SourceMediaVersion == source.Asset.Version)
            .OrderByDescending(transcript => transcript.Revision)
            .Select(transcript => new { transcript.Id, transcript.Version })
            .FirstOrDefaultAsync(cancellationToken);
        var approvedContentFingerprint = latestApprovedSource is null
            ? string.Empty
            : await ComputeTranscriptFingerprintAsync(latestApprovedSource.Id, cancellationToken);
        var fingerprintSource = string.Join('|',
            lessonId,
            source.Asset.Id,
            source.Asset.Version,
            source.File.Sha256,
            string.Join(',', targets),
            providerVersion,
            latestApprovedSource?.Id,
            latestApprovedSource?.Version,
            approvedContentFingerprint);
        var idempotencyKey = $"prepare:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant()}";
        var ownerId = RequireOwner();
        var clientKey = string.IsNullOrWhiteSpace(requestIdempotencyKey) ? null : requestIdempotencyKey.Trim();
        if (clientKey?.Length > 200)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["Idempotency-Key"] = ["Idempotency-Key cannot exceed 200 characters."]
            });
        }
        if (clientKey is not null)
        {
            var replay = await (
                    from request in database.ProcessingIdempotencyRequests
                    join replayJob in database.ProcessingJobs on request.ProcessingJobId equals replayJob.Id
                    where request.OwnerUserId == ownerId && request.ClientKey == clientKey
                    select new { Request = request, Job = replayJob })
                .SingleOrDefaultAsync(cancellationToken);
            if (replay is not null)
            {
                if (replay.Request.RequestFingerprint != idempotencyKey)
                {
                    throw new StateConflictException("Idempotency-Key was already used for different processing input.");
                }
                return ToModel(replay.Job);
            }
        }
        var existing = await database.ProcessingJobs.SingleOrDefaultAsync(
            job => job.IdempotencyKey == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            if (clientKey is not null)
            {
                await EnsureClientMappingAsync(ownerId, clientKey, idempotencyKey, existing.Id, cancellationToken);
            }
            return ToModel(existing);
        }

        var payload = JsonSerializer.Serialize(new PrepareAccessibilityPayload(
            source.Asset.Id,
            source.Asset.Version,
            source.File.Sha256,
            targets,
            providerVersion,
            latestApprovedSource?.Id,
            latestApprovedSource?.Version,
            string.IsNullOrEmpty(approvedContentFingerprint) ? null : approvedContentFingerprint));
        var job = new ProcessingJob(
            ProcessingJobType.PrepareMedia,
            idempotencyKey,
            lessonId: lesson.Id,
            mediaAssetId: source.Asset.Id,
            requestedByUserId: ownerId,
            payloadJson: payload);
        database.ProcessingJobs.Add(job);
        if (clientKey is not null)
        {
            database.ProcessingIdempotencyRequests.Add(new ProcessingIdempotencyRequest(
                ownerId,
                clientKey,
                idempotencyKey,
                job.Id,
                timeProvider.GetUtcNow()));
        }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.Entry(job).State = EntityState.Detached;
            database.ChangeTracker.Clear();
            if (clientKey is not null)
            {
                var replay = await database.ProcessingIdempotencyRequests.SingleOrDefaultAsync(
                    request => request.OwnerUserId == ownerId && request.ClientKey == clientKey,
                    cancellationToken);
                if (replay is not null)
                {
                    if (replay.RequestFingerprint != idempotencyKey)
                    {
                        throw new StateConflictException("Idempotency-Key was already used for different processing input.");
                    }
                    var replayJob = await database.ProcessingJobs.SingleAsync(
                        item => item.Id == replay.ProcessingJobId,
                        cancellationToken);
                    return ToModel(replayJob);
                }
            }
            var concurrent = await database.ProcessingJobs.SingleOrDefaultAsync(
                item => item.IdempotencyKey == idempotencyKey,
                cancellationToken);
            if (concurrent is not null)
            {
                if (clientKey is not null)
                {
                    await EnsureClientMappingAsync(ownerId, clientKey, idempotencyKey, concurrent.Id, cancellationToken);
                }
                return ToModel(concurrent);
            }
            throw;
        }
        try
        {
            job.SetExternalJobId(scheduler.Enqueue(job.Id));
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Processing job {ProcessingJobId} remains queued for reconciliation", job.Id);
        }

        return ToModel(job);
    }

    public async Task<ProcessingJobModel> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            ?? throw new ResourceNotFoundException("Processing job", jobId);
        var attempts = await database.ProcessingJobAttempts
            .Where(attempt => attempt.ProcessingJobId == job.Id)
            .OrderBy(attempt => attempt.AttemptNumber)
            .Select(attempt => new ProcessingAttemptModel(
                attempt.AttemptNumber,
                Contract(attempt.Status),
                attempt.Provider,
                attempt.ErrorCode,
                attempt.ErrorMessage,
                attempt.StartedAt,
                attempt.CompletedAt))
            .ToListAsync(cancellationToken);
        return ToModel(job) with { AttemptHistory = attempts };
    }

    public async Task<ProcessingJobModel> RetryAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            ?? throw new ResourceNotFoundException("Processing job", jobId);
        if (job.Attempts >= 4)
        {
            throw new StateConflictException("The processing job reached its maximum attempt count.");
        }
        var retryableCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "PROCESSOR_UNAVAILABLE",
            "STORAGE_UNAVAILABLE",
            "LEASE_EXPIRED"
        };
        if (job.Status != ProcessingJobStatus.Cancelled &&
            (job.ErrorCode is null || !retryableCodes.Contains(job.ErrorCode)))
        {
            throw new StateConflictException("The processing failure is not classified as retryable.");
        }

        if (job.Type is ProcessingJobType.NormalizeDocument or ProcessingJobType.GenerateDocumentOutputs)
        {
            await EnsureDocumentRetryInputIsCurrentAsync(job, cancellationToken);
        }
        else
        {
            await EnsureMediaRetryInputIsCurrentAsync(job, cancellationToken);
        }
        try
        {
            job.Requeue();
        }
        catch (InvalidOperationException exception)
        {
            throw new StateConflictException(exception.Message);
        }

        await database.SaveChangesAsync(cancellationToken);
        try
        {
            job.SetExternalJobId(scheduler.Enqueue(job.Id));
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Retried processing job {ProcessingJobId} remains queued for reconciliation", job.Id);
        }
        return ToModel(job);
    }

    private async Task EnsureMediaRetryInputIsCurrentAsync(
        ProcessingJob job,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<PrepareAccessibilityPayload>(job.PayloadJson ?? string.Empty)
            ?? throw new StateConflictException("The processing payload is invalid.");
        var currentConfiguration = $"{WorkflowVersion}:{transcription.Version}:{translation.Version}";
        if (payload.ConfigurationVersion != currentConfiguration)
        {
            throw new StateConflictException("Provider configuration changed and a new processing job is required.");
        }
        var inputIsCurrent = await (
                from asset in database.MediaAssets
                join file in database.StoredFiles on asset.StoredFileId equals file.Id
                where asset.Id == payload.SourceAssetId && asset.IsActive && asset.Version == payload.SourceAssetVersion &&
                      file.Sha256 == payload.SourceFileSha256
                select asset.Id)
            .AnyAsync(cancellationToken);
        if (!inputIsCurrent)
        {
            throw new StateConflictException("The processing input changed and a new job is required.");
        }
        if (!payload.SourceTranscriptId.HasValue)
        {
            return;
        }

        var sourceTranscript = await database.Transcripts.SingleOrDefaultAsync(
            transcript => transcript.Id == payload.SourceTranscriptId.Value &&
                          transcript.ReviewStatus == ReviewStatus.Approved,
            cancellationToken);
        if (sourceTranscript is null || sourceTranscript.Version != payload.SourceTranscriptVersion ||
            await ComputeTranscriptFingerprintAsync(sourceTranscript.Id, cancellationToken) !=
            payload.SourceTranscriptFingerprint)
        {
            throw new StateConflictException("The reviewed source transcript changed and a new job is required.");
        }
    }

    private async Task EnsureDocumentRetryInputIsCurrentAsync(
        ProcessingJob job,
        CancellationToken cancellationToken)
    {
        if (job.Type == ProcessingJobType.NormalizeDocument)
        {
            var payload = JsonSerializer.Deserialize<Accessibility.Application.Features.Documents.NormalizeDocumentPayload>(
                job.PayloadJson ?? string.Empty)
                ?? throw new StateConflictException("The document normalization payload is invalid.");
            var current = await (
                    from document in database.Documents
                    join file in database.StoredFiles on document.SourceFileId equals file.Id
                    where document.Id == payload.DocumentId && file.Id == payload.SourceFileId &&
                          file.Status == StoredFileStatus.Ready && file.Sha256 == payload.SourceSha256
                    select document.Id)
                .AnyAsync(cancellationToken);
            if (!current)
            {
                throw new StateConflictException("The document source changed and a new normalization job is required.");
            }
            return;
        }

        var outputPayload = JsonSerializer.Deserialize<Accessibility.Application.Features.Documents.GenerateDocumentOutputsPayload>(
            job.PayloadJson ?? string.Empty)
            ?? throw new StateConflictException("The document output payload is invalid.");
        var outputIsCurrent = await database.Documents.AnyAsync(document =>
            document.Id == outputPayload.DocumentId && document.Status == DocumentStatus.Ready &&
            document.Version == outputPayload.SourceRevision &&
            document.NormalizedModelFileId == outputPayload.NormalizedModelFileId,
            cancellationToken);
        if (!outputIsCurrent)
        {
            throw new StateConflictException("The reviewed document changed and a new output job is required.");
        }
    }

    public async Task<ProcessingJobModel> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await VisibleJobs().SingleOrDefaultAsync(item => item.Id == jobId, cancellationToken)
            ?? throw new ResourceNotFoundException("Processing job", jobId);
        try
        {
            job.RequestCancellation(timeProvider.GetUtcNow());
        }
        catch (InvalidOperationException exception)
        {
            throw new StateConflictException(exception.Message);
        }

        await database.SaveChangesAsync(cancellationToken);
        cancellationRegistry.Cancel(job.Id);
        if (job.Status == ProcessingJobStatus.Cancelled && job.ExternalJobId is not null)
        {
            scheduler.Delete(job.ExternalJobId);
        }
        return ToModel(job);
    }

    public async Task<TranscriptEditorModel> GetTranscriptAsync(Guid transcriptId, CancellationToken cancellationToken)
    {
        var transcript = await VisibleTranscripts().SingleOrDefaultAsync(item => item.Id == transcriptId, cancellationToken)
            ?? throw new ResourceNotFoundException("Transcript", transcriptId);
        return await ToEditorModelAsync(transcript, cancellationToken);
    }

    public async Task<IReadOnlyList<TranscriptSummaryModel>> ListTranscriptsAsync(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        if (!await VisibleLessons().AnyAsync(lesson => lesson.Id == lessonId, cancellationToken))
        {
            throw new ResourceNotFoundException("Lesson", lessonId);
        }
        return await database.Transcripts
            .Where(transcript => transcript.LessonId == lessonId)
            .OrderBy(transcript => transcript.Language)
            .ThenByDescending(transcript => transcript.Revision)
            .Select(transcript => new TranscriptSummaryModel(
                transcript.Id,
                transcript.Language,
                transcript.Revision,
                Contract(transcript.Origin),
                Contract(transcript.ReviewStatus),
                transcript.Provider,
                transcript.ProviderVersion,
                transcript.ProcessingJobId,
                transcript.Version,
                transcript.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<TranscriptEditorModel> UpdateTranscriptAsync(
        Guid transcriptId,
        long expectedVersion,
        UpdateTranscriptCommand command,
        CancellationToken cancellationToken)
    {
        var transcript = await VisibleTranscripts().SingleOrDefaultAsync(item => item.Id == transcriptId, cancellationToken)
            ?? throw new ResourceNotFoundException("Transcript", transcriptId);
        if (transcript.Version != expectedVersion) throw new VersionConflictException();
        if (transcript.ReviewStatus != ReviewStatus.Draft)
        {
            throw new StateConflictException("Only a draft transcript can be edited.");
        }

        var duration = await CurrentMediaDurationAsync(transcript.LessonId, cancellationToken);
        var segments = ValidateSegments(command.Segments, duration);
        var existing = await database.TranscriptSegments
            .Where(segment => segment.TranscriptId == transcript.Id)
            .ToListAsync(cancellationToken);
        database.TranscriptSegments.RemoveRange(existing);
        database.TranscriptSegments.AddRange(segments.Select(segment => new TranscriptSegment(
            transcript.Id,
            segment.Sequence,
            segment.StartMilliseconds,
            segment.EndMilliseconds,
            segment.Text,
            segment.SpeakerLabel)));
        await MarkDerivedResourcesStaleAsync(
            transcript.SourceTranscriptId ?? transcript.Id,
            transcript.Id,
            cancellationToken);
        var lesson = await database.Lessons.SingleAsync(item => item.Id == transcript.LessonId, cancellationToken);
        lesson.AdvanceDraftRevision();
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new VersionConflictException();
        }

        return await ToEditorModelAsync(transcript, cancellationToken);
    }

    public async Task<TranscriptEditorModel> CreateDraftRevisionAsync(
        Guid transcriptId,
        CancellationToken cancellationToken)
    {
        var source = await VisibleTranscripts().SingleOrDefaultAsync(
            item => item.Id == transcriptId,
            cancellationToken)
            ?? throw new ResourceNotFoundException("Transcript", transcriptId);
        if (source.ReviewStatus != ReviewStatus.Approved)
        {
            throw new StateConflictException("Only an approved transcript can start a correction revision.");
        }

        var revision = await NextRevisionAsync(source.LessonId, source.Language, cancellationToken);
        var draft = new Transcript(
            source.LessonId,
            source.Language,
            revision,
            TranscriptOrigin.Manual,
            source.SourceLessonRevision,
            source.SourceTranscriptId,
            source.SourceMediaAssetId,
            source.SourceMediaVersion,
            provider: "educator-revision",
            providerVersion: "v1",
            revisionOfTranscriptId: source.Id);
        var segments = await database.TranscriptSegments
            .Where(segment => segment.TranscriptId == source.Id)
            .OrderBy(segment => segment.Sequence)
            .ToListAsync(cancellationToken);
        database.Transcripts.Add(draft);
        database.TranscriptSegments.AddRange(segments.Select(segment => new TranscriptSegment(
            draft.Id,
            segment.Sequence,
            segment.StartMilliseconds,
            segment.EndMilliseconds,
            segment.Text,
            segment.SpeakerLabel)));
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new VersionConflictException();
        }
        return await ToEditorModelAsync(draft, cancellationToken);
    }

    public async Task<TranscriptEditorModel> ImportTranscriptAsync(
        Guid lessonId,
        ImportTranscriptCommand command,
        CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        var source = await CurrentMediaAsync(lessonId, cancellationToken);
        var parsed = formats.Parse(command.Content, command.Format, source.DurationMilliseconds);
        var normalizedLanguage = command.Language.Trim().ToLowerInvariant();
        var revision = await NextRevisionAsync(lessonId, normalizedLanguage, cancellationToken);
        Transcript transcript;
        try
        {
            transcript = new Transcript(
                lessonId,
                normalizedLanguage,
                revision,
                TranscriptOrigin.Imported,
                lesson.DraftRevision,
                sourceMediaAssetId: source.Id,
                sourceMediaVersion: source.Version,
                provider: "educator-import",
                providerVersion: command.Format.ToString());
        }
        catch (ArgumentException exception)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                [exception.ParamName ?? "language"] = [exception.Message]
            });
        }
        database.Transcripts.Add(transcript);
        database.TranscriptSegments.AddRange(parsed.Select(segment => new TranscriptSegment(
            transcript.Id,
            segment.Sequence,
            segment.StartMilliseconds,
            segment.EndMilliseconds,
            segment.Text,
            segment.SpeakerLabel)));
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new VersionConflictException();
        }
        return await ToEditorModelAsync(transcript, cancellationToken);
    }

    public async Task<TranscriptExportModel> ExportTranscriptAsync(
        Guid transcriptId,
        TranscriptExchangeFormat format,
        CancellationToken cancellationToken)
    {
        var transcript = await VisibleTranscripts().SingleOrDefaultAsync(item => item.Id == transcriptId, cancellationToken)
            ?? throw new ResourceNotFoundException("Transcript", transcriptId);
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
        var extension = format switch
        {
            TranscriptExchangeFormat.WebVtt => "vtt",
            TranscriptExchangeFormat.Srt => "srt",
            _ => "txt"
        };
        var contentType = format switch
        {
            TranscriptExchangeFormat.WebVtt => "text/vtt; charset=utf-8",
            TranscriptExchangeFormat.Srt => "application/x-subrip; charset=utf-8",
            _ => "text/plain; charset=utf-8"
        };
        return new TranscriptExportModel(
            $"{transcript.Language}-r{transcript.Revision}.{extension}",
            contentType,
            formats.Serialize(segments, format));
    }

    private IQueryable<Lesson> VisibleLessons() => userContext.IsInRole(AccessLearnRoles.Administrator)
        ? database.Lessons
        : database.Lessons.Where(lesson => lesson.OwnerUserId == RequireOwner());

    private IQueryable<ProcessingJob> VisibleJobs()
    {
        if (userContext.IsInRole(AccessLearnRoles.Administrator))
        {
            return database.ProcessingJobs;
        }

        var ownerId = RequireOwner();
        return database.ProcessingJobs.Where(job =>
            job.RequestedByUserId == ownerId ||
            job.DocumentId.HasValue &&
            database.Documents.Any(document =>
                document.Id == job.DocumentId.Value &&
                database.Lessons.Any(lesson =>
                    lesson.Id == document.LessonId && lesson.OwnerUserId == ownerId)));
    }

    private IQueryable<Transcript> VisibleTranscripts() =>
        from transcript in database.Transcripts
        join lesson in VisibleLessons() on transcript.LessonId equals lesson.Id
        select transcript;

    private Guid RequireOwner() =>
        userContext.UserId ?? throw new UnauthorizedAccessException("An authenticated owner is required.");

    private async Task<MediaAsset> CurrentMediaAsync(Guid lessonId, CancellationToken cancellationToken) =>
        await database.MediaAssets.SingleAsync(
            asset => asset.LessonId == lessonId && asset.IsActive && asset.Status == ResourceStatus.Ready &&
                     (asset.Role == MediaAssetRole.PrimaryVideo || asset.Role == MediaAssetRole.PrimaryAudio),
            cancellationToken);

    private async Task<long> CurrentMediaDurationAsync(Guid lessonId, CancellationToken cancellationToken) =>
        (await CurrentMediaAsync(lessonId, cancellationToken)).DurationMilliseconds
        ?? throw new StateConflictException("Primary media duration is unavailable.");

    private async Task<int> NextRevisionAsync(Guid lessonId, string language, CancellationToken cancellationToken) =>
        (await database.Transcripts
            .Where(transcript => transcript.LessonId == lessonId && transcript.Language == language)
            .Select(transcript => (int?)transcript.Revision)
            .MaxAsync(cancellationToken) ?? 0) + 1;

    private async Task MarkDerivedResourcesStaleAsync(
        Guid sourceTranscriptId,
        Guid excludedTranscriptId,
        CancellationToken cancellationToken)
    {
        var derived = new List<Transcript>();
        var frontier = new[] { sourceTranscriptId };
        while (frontier.Length > 0)
        {
            var level = await database.Transcripts
                .Where(transcript => transcript.SourceTranscriptId.HasValue &&
                                     frontier.Contains(transcript.SourceTranscriptId.Value) &&
                                     transcript.Id != excludedTranscriptId &&
                                     transcript.ReviewStatus != ReviewStatus.Superseded)
                .ToListAsync(cancellationToken);
            var unseen = level.Where(transcript => derived.All(existing => existing.Id != transcript.Id)).ToList();
            if (unseen.Count == 0) break;
            derived.AddRange(unseen);
            frontier = unseen.Select(transcript => transcript.Id).ToArray();
        }
        foreach (var transcript in derived) transcript.MarkStale();
        var transcriptIds = derived.Select(transcript => transcript.Id).Append(excludedTranscriptId).ToArray();
        var artifacts = await database.CaptionArtifacts
            .Where(artifact => transcriptIds.Contains(artifact.TranscriptId) && artifact.Status == ResourceStatus.Ready)
            .ToListAsync(cancellationToken);
        foreach (var artifact in artifacts) artifact.MarkStale();
    }

    private async Task<TranscriptEditorModel> ToEditorModelAsync(
        Transcript transcript,
        CancellationToken cancellationToken)
    {
        var segments = await database.TranscriptSegments
            .Where(segment => segment.TranscriptId == transcript.Id)
            .OrderBy(segment => segment.Sequence)
            .Select(segment => new TranscriptSegmentCommand(
                segment.Sequence,
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                segment.Text,
                segment.SpeakerLabel))
            .ToListAsync(cancellationToken);
        return new TranscriptEditorModel(
            transcript.Id,
            transcript.LessonId,
            transcript.Language,
            transcript.Revision,
            Contract(transcript.Origin),
            Contract(transcript.ReviewStatus),
            transcript.Provider,
            transcript.ProviderVersion,
            segments,
            transcript.Version);
    }

    private static IReadOnlyList<TranscriptSegmentCommand> ValidateSegments(
        IReadOnlyList<TranscriptSegmentCommand> segments,
        long duration)
    {
        if (segments.Count is < 1 or > 10_000) throw InvalidSegments();
        var ordered = segments.OrderBy(segment => segment.Sequence).ToArray();
        long previousEnd = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var segment = ordered[index];
            if (segment.Sequence != index || segment.StartMilliseconds < previousEnd ||
                segment.EndMilliseconds <= segment.StartMilliseconds || segment.EndMilliseconds > duration ||
                string.IsNullOrWhiteSpace(segment.Text)) throw InvalidSegments();
            previousEnd = segment.EndMilliseconds;
        }
        return ordered;
    }

    private static IReadOnlyList<string> NormalizeLanguages(
        IReadOnlyList<string> requested,
        string sourceLanguage)
    {
        var languages = requested.Count == 0 ? new[] { "en", "am" } : requested;
        var normalized = languages.Append(sourceLanguage)
            .Select(language => language.Trim().ToLowerInvariant())
            .Distinct()
            .OrderBy(language => language, StringComparer.Ordinal)
            .ToArray();
        if (normalized.Any(language => language is not ("en" or "am")))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["targetLanguages"] = ["Only English (en) and Amharic (am) are supported in this workflow."]
            });
        }
        return normalized;
    }

    private async Task<string> ComputeTranscriptFingerprintAsync(Guid transcriptId, CancellationToken cancellationToken)
    {
        var content = await database.TranscriptSegments
            .Where(segment => segment.TranscriptId == transcriptId)
            .OrderBy(segment => segment.Sequence)
            .Select(segment => $"{segment.Sequence}|{segment.StartMilliseconds}|{segment.EndMilliseconds}|{segment.Text}|{segment.SpeakerLabel}")
            .ToListAsync(cancellationToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', content)))).ToLowerInvariant();
    }

    private async Task EnsureClientMappingAsync(
        Guid ownerId,
        string clientKey,
        string fingerprint,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        var existing = await database.ProcessingIdempotencyRequests.SingleOrDefaultAsync(
            request => request.OwnerUserId == ownerId && request.ClientKey == clientKey,
            cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestFingerprint != fingerprint || existing.ProcessingJobId != jobId)
            {
                throw new StateConflictException("Idempotency-Key was already used for different processing input.");
            }
            return;
        }

        database.ProcessingIdempotencyRequests.Add(new ProcessingIdempotencyRequest(
            ownerId,
            clientKey,
            fingerprint,
            jobId,
            timeProvider.GetUtcNow()));
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            var replay = await database.ProcessingIdempotencyRequests.SingleOrDefaultAsync(
                request => request.OwnerUserId == ownerId && request.ClientKey == clientKey,
                cancellationToken);
            if (replay is null || replay.RequestFingerprint != fingerprint || replay.ProcessingJobId != jobId)
            {
                throw new StateConflictException("Idempotency-Key was already used for different processing input.");
            }
        }
    }

    private static RequestValidationException InvalidSegments() =>
        new(new Dictionary<string, string[]> { ["segments"] = ["Transcript segment sequence or timing is invalid."] });

    private static ProcessingJobModel ToModel(ProcessingJob job) => new(
        job.Id,
        job.LessonId,
        job.MediaAssetId,
        job.DocumentId,
        Contract(job.Type),
        Contract(job.Status),
        job.ProgressPercentage,
        job.Attempts,
        job.Provider,
        job.ErrorCode,
        job.ErrorMessage,
        job.StartedAt,
        job.CompletedAt,
        job.CancellationRequestedAt,
        job.Version,
        CanRetry(job));

    private static bool CanRetry(ProcessingJob job) =>
        job.Attempts < 4 &&
        (job.Status == ProcessingJobStatus.Cancelled ||
         job.Status == ProcessingJobStatus.Failed && job.ErrorCode is
             "PROCESSOR_UNAVAILABLE" or "STORAGE_UNAVAILABLE" or "LEASE_EXPIRED");

    private static string Contract<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
