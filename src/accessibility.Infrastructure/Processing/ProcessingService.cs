using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
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
    IObjectStorage objectStorage,
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
        var payload = JsonSerializer.Deserialize<PrepareAccessibilityPayload>(job.PayloadJson ?? string.Empty)
            ?? throw new StateConflictException("The processing payload is invalid.");
        var currentConfiguration = $"{WorkflowVersion}:{transcription.Version}:{translation.Version}";
        if (payload.ConfigurationVersion != currentConfiguration)
        {
            throw new StateConflictException("Provider configuration changed and a new processing job is required.");
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
        if (payload.SourceTranscriptId.HasValue)
        {
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

    public async Task<DocumentSourceModel> UploadDocumentAsync(
        Guid lessonId,
        UploadDocumentCommand command,
        QuarantinedUpload upload,
        CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        if (!string.Equals(Path.GetExtension(upload.OriginalFileName), ".docx", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                upload.ContentType,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentTypeMismatchException("Structured documents must be DOCX files.");
        }
        if (upload.Header.Length < 4 || upload.Header[0] != 0x50 || upload.Header[1] != 0x4B ||
            upload.Header[2] != 0x03 || upload.Header[3] != 0x04)
        {
            throw new FileContentInvalidException("The DOCX ZIP signature is invalid.");
        }
        ValidateDocxPackage(upload.FilePath);

        var objectKey = $"lessons/{lesson.Id:N}/documents/{Guid.CreateVersion7():N}/{upload.Sha256[..16]}.docx";
        var file = new StoredFile(
            objectKey,
            upload.OriginalFileName,
            upload.ContentType,
            upload.ByteLength,
            upload.Sha256);
        AccessibleDocument document;
        try
        {
            document = new AccessibleDocument(lesson.Id, file.Id, command.Title, command.Language);
        }
        catch (ArgumentException exception)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                [exception.ParamName ?? "document"] = [exception.Message]
            });
        }
        database.StoredFiles.Add(file);
        database.Documents.Add(document);
        await database.SaveChangesAsync(cancellationToken);
        try
        {
            await using var stream = new FileStream(upload.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
            var write = await objectStorage.PutAsync(
                objectKey,
                stream,
                upload.ContentType,
                upload.ByteLength,
                new Dictionary<string, string> { ["sha256"] = upload.Sha256, ["lesson-id"] = lesson.Id.ToString("N") },
                cancellationToken);
            file.MarkReady(write.ETag);
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            file.Reject("Document upload was cancelled.");
            document.Fail();
            await database.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            file.Reject("Document upload did not complete.");
            document.Fail();
            await database.SaveChangesAsync(CancellationToken.None);
            throw new StorageUnavailableException("The document could not be stored.", exception);
        }

        return new DocumentSourceModel(
            document.Id,
            lesson.Id,
            document.Title,
            document.Language,
            Contract(document.Status),
            file.OriginalFileName,
            file.ByteLength,
            file.Sha256,
            document.Version);
    }

    private IQueryable<Lesson> VisibleLessons() => userContext.IsInRole(AccessLearnRoles.Administrator)
        ? database.Lessons
        : database.Lessons.Where(lesson => lesson.OwnerUserId == RequireOwner());

    private IQueryable<ProcessingJob> VisibleJobs() => userContext.IsInRole(AccessLearnRoles.Administrator)
        ? database.ProcessingJobs
        : database.ProcessingJobs.Where(job => job.RequestedByUserId == RequireOwner());

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

    internal static void ValidateDocxPackage(string filePath)
    {
        const int maximumEntries = 10_000;
        const long maximumExpandedBytes = 100 * 1024 * 1024;
        const double maximumCompressionRatio = 100;
        try
        {
            using var archive = ZipFile.OpenRead(filePath);
            if (archive.Entries.Count is < 1 or > maximumEntries)
            {
                throw new FileContentInvalidException("The DOCX archive contains an invalid number of entries.");
            }
            long expandedBytes = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Contains("../", StringComparison.Ordinal) ||
                    name.Contains(':') || !names.Add(name) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                {
                    throw new FileContentInvalidException("The DOCX archive contains an unsafe entry.");
                }
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > maximumExpandedBytes ||
                    entry.Length > 0 && (entry.CompressedLength == 0 ||
                        entry.Length / (double)entry.CompressedLength > maximumCompressionRatio))
                {
                    throw new FileContentInvalidException("The DOCX archive exceeds safe expansion limits.");
                }
            }
            if (!names.Contains("[Content_Types].xml") || !names.Contains("word/document.xml"))
            {
                throw new FileContentInvalidException("The archive is not a valid WordprocessingML document.");
            }
            var contentTypes = LoadSafeXml(archive.GetEntry("[Content_Types].xml")!);
            XNamespace contentTypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
            if (contentTypes.Root?.Name != contentTypesNamespace + "Types" ||
                !contentTypes.Root.Elements(contentTypesNamespace + "Override").Any(element =>
                    (string?)element.Attribute("PartName") == "/word/document.xml" &&
                    (string?)element.Attribute("ContentType") ==
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"))
            {
                throw new FileContentInvalidException("The DOCX content-type manifest is invalid.");
            }
            var document = LoadSafeXml(archive.GetEntry("word/document.xml")!);
            XNamespace wordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            if (document.Root?.Name != wordNamespace + "document")
            {
                throw new FileContentInvalidException("The DOCX main document namespace is invalid.");
            }
            XNamespace relationshipsNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
            foreach (var relationshipEntry in archive.Entries.Where(entry =>
                         entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                var relationships = LoadSafeXml(relationshipEntry);
                if (relationships.Root?.Name != relationshipsNamespace + "Relationships" ||
                    relationships.Root.Elements(relationshipsNamespace + "Relationship").Any(element =>
                        string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new FileContentInvalidException("The DOCX contains invalid or external relationships.");
                }
            }
        }
        catch (InvalidDataException exception)
        {
            throw new FileContentInvalidException($"The DOCX archive is invalid: {exception.Message}");
        }
        catch (XmlException exception)
        {
            throw new FileContentInvalidException($"The DOCX XML is invalid: {exception.Message}");
        }
    }

    private static XDocument LoadSafeXml(ZipArchiveEntry entry)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10 * 1024 * 1024
        };
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static ProcessingJobModel ToModel(ProcessingJob job) => new(
        job.Id,
        job.LessonId,
        job.MediaAssetId,
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
        job.Version);

    private static string Contract<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
