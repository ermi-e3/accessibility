using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Player;
using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Player;

public sealed class PlayerPublicationService(
    AccessLearnDbContext database,
    IUserContext userContext,
    IObjectStorage objectStorage,
    TimeProvider timeProvider,
    IOptions<PlayerDeliveryOptions> options,
    ILogger<PlayerPublicationService> logger) : IPlayerPublicationService
{
    private const string SchemaVersion = "1.0";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PlayerDeliveryOptions _options = options.Value;

    public async Task<TranscriptModel> CreateManualTranscriptAsync(
        Guid lessonId,
        CreateManualTranscriptCommand command,
        CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        if (lesson.Status == LessonStatus.Archived)
        {
            throw new StateConflictException("An archived lesson cannot accept transcripts.");
        }

        var primary = await database.MediaAssets
            .Where(asset => asset.LessonId == lessonId && asset.IsActive && asset.Status == ResourceStatus.Ready &&
                            (asset.Role == MediaAssetRole.PrimaryVideo || asset.Role == MediaAssetRole.PrimaryAudio))
            .Select(asset => new { asset.Id, asset.Version, asset.DurationMilliseconds })
            .SingleOrDefaultAsync(cancellationToken);
        if (primary?.DurationMilliseconds is null)
        {
            throw new StateConflictException("Ready primary media is required before adding a transcript.");
        }

        var ordered = ValidateSegments(command.Segments, primary.DurationMilliseconds.Value);
        var normalizedLanguage = command.Language.Trim().ToLowerInvariant();
        var revision = await database.Transcripts
            .Where(transcript => transcript.LessonId == lessonId && transcript.Language == normalizedLanguage)
            .Select(transcript => (int?)transcript.Revision)
            .MaxAsync(cancellationToken) ?? 0;
        Transcript transcript;
        try
        {
            transcript = new Transcript(
                lessonId,
                normalizedLanguage,
                revision + 1,
                TranscriptOrigin.Manual,
                lesson.DraftRevision,
                sourceMediaAssetId: primary.Id,
                sourceMediaVersion: primary.Version);
        }
        catch (ArgumentException exception)
        {
            throw Validation(exception.ParamName ?? "language", exception.Message);
        }

        var segments = ordered.Select(segment => new TranscriptSegment(
            transcript.Id,
            segment.Sequence,
            segment.StartMilliseconds,
            segment.EndMilliseconds,
            segment.Text,
            segment.SpeakerLabel)).ToArray();
        database.Transcripts.Add(transcript);
        database.TranscriptSegments.AddRange(segments);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new VersionConflictException();
        }
        return ToTranscriptModel(transcript, segments);
    }

    public async Task<TranscriptModel> ApproveTranscriptAsync(
        Guid transcriptId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var result = await (
                from candidate in database.Transcripts
                join visibleLesson in VisibleLessons() on candidate.LessonId equals visibleLesson.Id
                where candidate.Id == transcriptId
                select new { Transcript = candidate, Lesson = visibleLesson })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Transcript", transcriptId);
        var transcript = result.Transcript;
        var lesson = result.Lesson;
        if (transcript.Version != expectedVersion)
        {
            throw new VersionConflictException();
        }
        if (transcript.ReviewStatus != ReviewStatus.Draft)
        {
            throw new StateConflictException("Only a draft transcript can be approved.");
        }
        var previousApproved = await database.Transcripts.SingleOrDefaultAsync(
            candidate => candidate.LessonId == transcript.LessonId &&
                         candidate.Language == transcript.Language &&
                         candidate.ReviewStatus == ReviewStatus.Approved,
            cancellationToken);
        var currentPrimary = await database.MediaAssets
            .Where(asset => asset.LessonId == lesson.Id && asset.IsActive && asset.Status == ResourceStatus.Ready &&
                            (asset.Role == MediaAssetRole.PrimaryVideo || asset.Role == MediaAssetRole.PrimaryAudio))
            .Select(asset => new { asset.Id, asset.Version, asset.DurationMilliseconds })
            .SingleOrDefaultAsync(cancellationToken);
        if (currentPrimary?.DurationMilliseconds is null ||
            transcript.SourceMediaAssetId != currentPrimary.Id ||
            transcript.SourceMediaVersion != currentPrimary.Version)
        {
            throw new StateConflictException("The primary media changed after this transcript was created.");
        }

        var segments = await database.TranscriptSegments
            .Where(segment => segment.TranscriptId == transcript.Id)
            .OrderBy(segment => segment.Sequence)
            .ToListAsync(cancellationToken);
        if (segments.Count == 0)
        {
            throw Validation("segments", "At least one transcript segment is required.");
        }
        ValidateSegments(
            segments.Select(segment => new TranscriptSegmentInput(
                segment.Sequence,
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                segment.Text,
                segment.SpeakerLabel)).ToArray(),
            currentPrimary.DurationMilliseconds.Value);

        var vtt = Encoding.UTF8.GetBytes(SerializeWebVtt(segments));
        ValidateSerializedWebVtt(vtt, segments.Count);
        var checksum = Convert.ToHexString(SHA256.HashData(vtt)).ToLowerInvariant();
        var objectKey = $"lessons/{transcript.LessonId:N}/captions/{transcript.Language}/r{transcript.Revision}-{checksum[..16]}.vtt";
        var existing = await (
                from candidate in database.CaptionArtifacts
                join file in database.StoredFiles on candidate.StoredFileId equals file.Id
                where candidate.TranscriptId == transcript.Id && candidate.Format == CaptionFormat.WebVtt &&
                      candidate.GeneratorVersion == "accesslearn-vtt-v1"
                select new { Artifact = candidate, File = file })
            .SingleOrDefaultAsync(cancellationToken);
        CaptionArtifact artifact;
        StoredFile storedFile;
        if (existing is null)
        {
            storedFile = new StoredFile(
                objectKey,
                $"{transcript.Language}.vtt",
                "text/vtt; charset=utf-8",
                vtt.Length,
                checksum);
            artifact = new CaptionArtifact(
                transcript.Id,
                storedFile.Id,
                CaptionFormat.WebVtt,
                "accesslearn-vtt-v1",
                checksum);
            database.StoredFiles.Add(storedFile);
            database.CaptionArtifacts.Add(artifact);
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                throw new VersionConflictException();
            }
        }
        else
        {
            artifact = existing.Artifact;
            storedFile = existing.File;
            if (artifact.Checksum != checksum || storedFile.ObjectKey != objectKey)
            {
                throw new StateConflictException("The existing caption retry metadata does not match this transcript.");
            }

            if (artifact.Status == ResourceStatus.Failed) artifact.Retry();
            if (storedFile.Status == StoredFileStatus.Rejected) storedFile.RetryQuarantine();
        }

        if (artifact.Status != ResourceStatus.Ready || storedFile.Status != StoredFileStatus.Ready)
        {
            ObjectStorageWriteResult captionWrite;
            try
            {
                await using var content = new MemoryStream(vtt, writable: false);
                captionWrite = await objectStorage.PutAsync(
                    objectKey,
                    content,
                    "text/vtt; charset=utf-8",
                    vtt.Length,
                    new Dictionary<string, string>
                    {
                        ["sha256"] = checksum,
                        ["transcript-id"] = transcript.Id.ToString("N")
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                artifact.Fail();
                storedFile.Reject("Caption artifact generation did not complete.");
                await TrySaveFailureStateAsync();
                throw new StorageUnavailableException("The caption artifact could not be stored.", exception);
            }

            if (storedFile.Status == StoredFileStatus.Quarantined) storedFile.MarkReady(captionWrite.ETag);
            if (artifact.Status == ResourceStatus.Pending) artifact.MarkReady();
        }
        try
        {
            if (previousApproved is not null)
            {
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                previousApproved.Supersede();
                await database.SaveChangesAsync(cancellationToken);
                transcript.Approve(RequireOwner(), timeProvider.GetUtcNow());
                lesson.AdvanceDraftRevision();
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                transcript.Approve(RequireOwner(), timeProvider.GetUtcNow());
                lesson.AdvanceDraftRevision();
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new VersionConflictException();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Caption {ObjectKey} is stored while metadata finalization remains pending",
                objectKey);
            throw new StateConflictException(
                "The caption was stored but metadata finalization requires reconciliation.");
        }

        return ToTranscriptModel(transcript, segments);
    }

    public async Task<PublicationModel> PublishAsync(
        Guid lessonId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        if (lesson.Status == LessonStatus.Archived)
        {
            throw new StateConflictException("An archived lesson cannot be published.");
        }

        if (lesson.CurrentPublicationId.HasValue)
        {
            var current = await LoadSnapshotAsync(
                lesson.Id,
                lesson.CurrentPublicationId.Value,
                requireCurrent: true,
                cancellationToken);
            if (current.LessonDraftRevision == lesson.DraftRevision)
            {
                var currentPublication = await database.LessonPublications.SingleAsync(
                    item => item.Id == lesson.CurrentPublicationId.Value,
                    cancellationToken);
                return new PublicationModel(
                    currentPublication.Id,
                    lesson.Id,
                    currentPublication.Revision,
                    currentPublication.PublishedAt,
                    currentPublication.Checksum,
                    current.Warnings);
            }
        }
        if (lesson.Version != expectedVersion)
        {
            throw new VersionConflictException();
        }

        var assets = await (
                from asset in database.MediaAssets
                join file in database.StoredFiles on asset.StoredFileId equals file.Id
                where asset.LessonId == lesson.Id && asset.IsActive &&
                      asset.Status == ResourceStatus.Ready && file.Status == StoredFileStatus.Ready
                select new SnapshotAsset(
                    asset.Id,
                    file.Id,
                    file.ObjectKey,
                    file.ContentType,
                    asset.Role,
                    asset.Version,
                    asset.Language,
                    asset.DurationMilliseconds,
                    asset.SyncOffsetMilliseconds,
                    asset.IsHumanReviewed,
                    asset.AccessibilityMode))
            .ToListAsync(cancellationToken);
        var primary = assets.SingleOrDefault(asset =>
            asset.Role is MediaAssetRole.PrimaryVideo or MediaAssetRole.PrimaryAudio)
            ?? throw new StateConflictException("Ready primary media is required before publication.");

        var captionReferences = await (
                from transcript in database.Transcripts
                join artifact in database.CaptionArtifacts on transcript.Id equals artifact.TranscriptId
                join file in database.StoredFiles on artifact.StoredFileId equals file.Id
                where transcript.LessonId == lesson.Id && transcript.ReviewStatus == ReviewStatus.Approved &&
                      artifact.Status == ResourceStatus.Ready && artifact.Format == CaptionFormat.WebVtt &&
                      file.Status == StoredFileStatus.Ready
                select new CaptionReference(
                    transcript.Id,
                    transcript.Language,
                    transcript.Revision,
                    transcript.SourceMediaAssetId,
                    transcript.SourceMediaVersion,
                    file.Id,
                    file.ObjectKey))
            .ToListAsync(cancellationToken);
        if (captionReferences.Count == 0)
        {
            throw new StateConflictException("At least one approved caption/transcript is required before publication.");
        }
        if (captionReferences.Any(caption =>
                caption.SourceMediaAssetId != primary.AssetId ||
                caption.SourceMediaVersion != primary.Version))
        {
            throw new StateConflictException("An approved transcript is stale for the current primary media.");
        }

        var captions = new List<SnapshotCaption>(captionReferences.Count);
        foreach (var caption in captionReferences)
        {
            var segments = await database.TranscriptSegments
                .Where(segment => segment.TranscriptId == caption.TranscriptId)
                .OrderBy(segment => segment.Sequence)
                .Select(segment => new TranscriptSegmentModel(
                    segment.Sequence,
                    segment.StartMilliseconds,
                    segment.EndMilliseconds,
                    segment.Text,
                    segment.SpeakerLabel))
                .ToListAsync(cancellationToken);
            captions.Add(new SnapshotCaption(
                caption.TranscriptId,
                caption.Language,
                caption.Revision,
                caption.FileId,
                caption.ObjectKey,
                segments));
        }

        var warnings = BuildWarnings(captions, assets);
        var signLanguage = SelectSecondaryAsset(assets, MediaAssetRole.SignLanguage, primary);
        var audioDescription = SelectSecondaryAsset(assets, MediaAssetRole.AudioDescription, primary);
        var publicationRevision = await database.LessonPublications
            .Where(publication => publication.LessonId == lesson.Id)
            .Select(publication => (int?)publication.Revision)
            .MaxAsync(cancellationToken) ?? 0;
        var publicationId = Guid.CreateVersion7();
        var snapshot = new PlayerPublicationSnapshot(
            SchemaVersion,
            lesson.Id,
            publicationId,
            publicationRevision + 1,
            lesson.DraftRevision,
            lesson.Title,
            lesson.SourceLanguage,
            primary,
            assets.FirstOrDefault(asset => asset.Role == MediaAssetRole.Poster),
            captions,
            signLanguage,
            audioDescription,
            warnings);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (manifestBytes.Length > 2_097_152)
        {
            throw new StateConflictException(
                "The publication snapshot exceeds 2 MiB. Reduce transcript segment size before publishing.");
        }
        var checksum = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        var objectKey = $"lessons/{lesson.Id:N}/publications/{publicationId:N}/manifest.json";
        var manifestFile = new StoredFile(
            objectKey,
            "manifest.json",
            "application/json",
            manifestBytes.Length,
            checksum);
        var now = timeProvider.GetUtcNow();
        var publication = new LessonPublication(
            lesson.Id,
            snapshot.Revision,
            manifestFile.Id,
            RequireOwner(),
            checksum,
            now,
            publicationId);
        database.StoredFiles.Add(manifestFile);
        database.LessonPublications.Add(publication);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new VersionConflictException();
        }

        ObjectStorageWriteResult manifestWrite;
        try
        {
            await using var content = new MemoryStream(manifestBytes, writable: false);
            manifestWrite = await objectStorage.PutAsync(
                objectKey,
                content,
                "application/json",
                manifestBytes.Length,
                new Dictionary<string, string> { ["sha256"] = checksum },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            manifestFile.Reject("Publication manifest storage did not complete.");
            await TrySaveFailureStateAsync();
            throw new StorageUnavailableException("The publication manifest could not be stored.", exception);
        }

        manifestFile.MarkReady(manifestWrite.ETag);
        lesson.Publish(publication);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new VersionConflictException();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Publication manifest {ObjectKey} is stored while metadata finalization remains pending",
                objectKey);
            throw new StateConflictException(
                "The publication manifest was stored but metadata finalization requires reconciliation.");
        }

        return new PublicationModel(
            publication.Id,
            lesson.Id,
            publication.Revision,
            publication.PublishedAt,
            publication.Checksum,
            warnings);
    }

    public async Task<PlayerManifestV1> GetManifestAsync(
        Guid lessonId,
        Uri publicApiBase,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadCurrentSnapshotAsync(lessonId, cancellationToken);
        var lifetime = TimeSpan.FromMinutes(_options.SignedUrlMinutes);
        var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
        var primaryUrl = await objectStorage.CreateReadUriAsync(snapshot.Primary.ObjectKey, lifetime, cancellationToken);
        var posterUrl = snapshot.Poster is null
            ? null
            : (await objectStorage.CreateReadUriAsync(snapshot.Poster.ObjectKey, lifetime, cancellationToken)).ToString();
        var captionTracks = new List<PlayerCaptionTrackV1>(snapshot.Captions.Count);
        var transcriptLinks = new List<PlayerTranscriptLinkV1>(snapshot.Captions.Count);
        foreach (var caption in snapshot.Captions)
        {
            var captionUrl = await objectStorage.CreateReadUriAsync(caption.ObjectKey, lifetime, cancellationToken);
            captionTracks.Add(new PlayerCaptionTrackV1(
                caption.Language,
                LanguageLabel(caption.Language),
                captionUrl.ToString(),
                caption.Language == snapshot.SourceLanguage,
                IsHumanReviewed: true));
            transcriptLinks.Add(new PlayerTranscriptLinkV1(
                caption.Language,
                LanguageLabel(caption.Language),
                new Uri(
                    publicApiBase,
                    $"api/v1/public/lessons/{lessonId}/publications/{snapshot.PublicationId}/transcripts/{caption.Language}")
                    .ToString(),
                IsHumanReviewed: true));
        }

        PlayerSignLanguageV1? sign = null;
        if (snapshot.SignLanguage is not null)
        {
            sign = new PlayerSignLanguageV1(
                snapshot.SignLanguage.Language ?? LanguageTags.EthiopianSignLanguage,
                (await objectStorage.CreateReadUriAsync(snapshot.SignLanguage.ObjectKey, lifetime, cancellationToken)).ToString(),
                snapshot.SignLanguage.SyncOffsetMilliseconds);
        }

        PlayerAudioDescriptionV1? description = null;
        if (snapshot.AudioDescription is not null)
        {
            description = new PlayerAudioDescriptionV1(
                (await objectStorage.CreateReadUriAsync(snapshot.AudioDescription.ObjectKey, lifetime, cancellationToken)).ToString(),
                snapshot.AudioDescription.SyncOffsetMilliseconds,
                snapshot.AudioDescription.AccessibilityMode ?? "full-mix");
        }

        return new PlayerManifestV1(
            snapshot.SchemaVersion,
            snapshot.LessonId,
            snapshot.PublicationId,
            snapshot.Revision,
            snapshot.Title,
            snapshot.SourceLanguage,
            expiresAt,
            new PlayerMediaV1(
                primaryUrl.ToString(),
                snapshot.Primary.ContentType,
                snapshot.Primary.DurationMilliseconds ?? 0,
                posterUrl),
            captionTracks,
            transcriptLinks,
            sign,
            description,
            snapshot.Warnings);
    }

    public async Task<PublicTranscriptV1> GetPublicTranscriptAsync(
        Guid lessonId,
        Guid publicationId,
        string language,
        CancellationToken cancellationToken)
    {
        var snapshot = await LoadSnapshotAsync(
            lessonId,
            publicationId,
            requireCurrent: false,
            cancellationToken);
        var normalized = language.Trim().ToLowerInvariant();
        var caption = snapshot.Captions.SingleOrDefault(item => item.Language == normalized)
            ?? throw new ResourceNotFoundException("Published transcript", normalized);
        return new PublicTranscriptV1(
            lessonId,
            snapshot.PublicationId,
            caption.Language,
            caption.Revision,
            caption.Segments);
    }

    private IQueryable<Lesson> VisibleLessons()
    {
        var lessons = database.Lessons.AsQueryable();
        return userContext.IsInRole(AccessLearnRoles.Administrator)
            ? lessons
            : lessons.Where(lesson => lesson.OwnerUserId == RequireOwner());
    }

    private async Task<PlayerPublicationSnapshot> LoadCurrentSnapshotAsync(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        var publicationId = await database.Lessons
            .Where(lesson => lesson.Id == lessonId && lesson.Status == LessonStatus.Published)
            .Select(lesson => lesson.CurrentPublicationId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Published lesson", lessonId);
        return await LoadSnapshotAsync(lessonId, publicationId, requireCurrent: true, cancellationToken);
    }

    private async Task<PlayerPublicationSnapshot> LoadSnapshotAsync(
        Guid lessonId,
        Guid publicationId,
        bool requireCurrent,
        CancellationToken cancellationToken)
    {
        var publication = await (
                from lesson in database.Lessons
                join candidate in database.LessonPublications on lesson.Id equals candidate.LessonId
                join file in database.StoredFiles on candidate.ManifestFileId equals file.Id
                where lesson.Id == lessonId && candidate.Id == publicationId &&
                      lesson.Status == LessonStatus.Published &&
                      (!requireCurrent || lesson.CurrentPublicationId == candidate.Id) &&
                      file.Status == StoredFileStatus.Ready
                select new { candidate, file })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Published lesson", lessonId);
        if (!publication.file.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
            publication.file.ByteLength is <= 0 or > 2_097_152)
        {
            throw new FileContentInvalidException("The publication manifest metadata is invalid.");
        }

        await using var stored = await objectStorage.OpenReadAsync(publication.file.ObjectKey, cancellationToken);
        var manifestBytes = await ReadBoundedAsync(stored.Content, 2_097_152, cancellationToken);
        var actualChecksum = SHA256.HashData(manifestBytes);
        if (!ChecksumEquals(actualChecksum, publication.file.Sha256) ||
            !ChecksumEquals(actualChecksum, publication.candidate.Checksum))
        {
            throw new FileContentInvalidException("The publication manifest checksum is invalid.");
        }

        var snapshot = JsonSerializer.Deserialize<PlayerPublicationSnapshot>(manifestBytes, JsonOptions);
        if (snapshot is null || snapshot.LessonId != lessonId || snapshot.PublicationId != publicationId ||
            snapshot.Revision != publication.candidate.Revision)
        {
            throw new FileContentInvalidException("The publication manifest is invalid.");
        }

        await ValidateSnapshotReferencesAsync(snapshot, cancellationToken);
        return snapshot;
    }

    private async Task TrySaveFailureStateAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await database.SaveChangesAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not persist player publication failure state");
        }
    }

    private async Task ValidateSnapshotReferencesAsync(
        PlayerPublicationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var assets = new[] { snapshot.Primary, snapshot.Poster, snapshot.SignLanguage, snapshot.AudioDescription }
            .Where(asset => asset is not null)
            .Cast<SnapshotAsset>()
            .ToArray();
        var fileReferences = assets
            .Select(asset => (asset.FileId, asset.ObjectKey))
            .Concat(snapshot.Captions.Select(caption => (caption.FileId, caption.ObjectKey)))
            .ToArray();
        var fileIds = fileReferences.Select(reference => reference.FileId).Distinct().ToArray();
        var files = await database.StoredFiles
            .Where(file => fileIds.Contains(file.Id))
            .Select(file => new { file.Id, file.ObjectKey })
            .ToDictionaryAsync(file => file.Id, cancellationToken);
        if (files.Count != fileIds.Length || fileReferences.Any(reference =>
                !files.TryGetValue(reference.FileId, out var file) || file.ObjectKey != reference.ObjectKey))
        {
            throw new FileContentInvalidException("A publication file reference is invalid.");
        }

        var assetIds = assets.Select(asset => asset.AssetId).ToArray();
        var persistedAssets = await database.MediaAssets
            .Where(asset => assetIds.Contains(asset.Id) && asset.LessonId == snapshot.LessonId)
            .Select(asset => new { asset.Id, asset.StoredFileId, asset.Role })
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
        if (persistedAssets.Count != assetIds.Length || assets.Any(asset =>
                !persistedAssets.TryGetValue(asset.AssetId, out var persisted) ||
                persisted.StoredFileId != asset.FileId || persisted.Role != asset.Role))
        {
            throw new FileContentInvalidException("A publication media reference is invalid.");
        }

        foreach (var caption in snapshot.Captions)
        {
            if (!await database.CaptionArtifacts.AnyAsync(
                    artifact => artifact.TranscriptId == caption.TranscriptId &&
                                artifact.StoredFileId == caption.FileId &&
                                artifact.Format == CaptionFormat.WebVtt,
                    cancellationToken))
            {
                throw new FileContentInvalidException("A publication caption reference is invalid.");
            }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > maximumBytes)
            {
                throw new FileContentInvalidException("The publication manifest exceeds the allowed size.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool ChecksumEquals(byte[] actual, string expectedHex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHex));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private Guid RequireOwner() =>
        userContext.UserId ?? throw new UnauthorizedAccessException("An authenticated owner is required.");

    private static IReadOnlyList<TranscriptSegmentInput> ValidateSegments(
        IReadOnlyList<TranscriptSegmentInput> segments,
        long mediaDurationMilliseconds)
    {
        if (segments.Count is < 1 or > 10_000)
        {
            throw Validation("segments", "Transcript must contain between 1 and 10000 segments.");
        }

        var ordered = segments.OrderBy(segment => segment.Sequence).ToArray();
        long previousEnd = 0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var segment = ordered[index];
            if (segment.Sequence != index || segment.StartMilliseconds < previousEnd ||
                segment.EndMilliseconds <= segment.StartMilliseconds ||
                segment.EndMilliseconds > mediaDurationMilliseconds || string.IsNullOrWhiteSpace(segment.Text) ||
                segment.Text.Any(character => char.IsControl(character) && character is not '\r' and not '\n' and not '\t'))
            {
                throw Validation("segments", $"Segment {index} has invalid sequence, timing, or text.");
            }

            previousEnd = segment.EndMilliseconds;
        }

        return ordered;
    }

    private static string SerializeWebVtt(IReadOnlyList<TranscriptSegment> segments)
    {
        var output = new StringBuilder("WEBVTT\n\n");
        foreach (var segment in segments)
        {
            output.AppendLine((segment.Sequence + 1).ToString());
            output.Append(FormatVttTime(segment.StartMilliseconds));
            output.Append(" --> ");
            output.AppendLine(FormatVttTime(segment.EndMilliseconds));
            if (!string.IsNullOrWhiteSpace(segment.SpeakerLabel))
            {
                output.Append(EscapeCue(segment.SpeakerLabel));
                output.Append(": ");
            }

            output.AppendLine(EscapeCue(segment.Text));
            output.AppendLine();
        }

        return output.ToString();
    }

    private static void ValidateSerializedWebVtt(byte[] value, int expectedCueCount)
    {
        var text = Encoding.UTF8.GetString(value);
        if (!text.StartsWith("WEBVTT\n\n", StringComparison.Ordinal) ||
            text.Split(" --> ", StringSplitOptions.None).Length - 1 != expectedCueCount)
        {
            throw new FileContentInvalidException("Generated WebVTT validation failed.");
        }
    }

    private static string FormatVttTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static string EscapeCue(string value) => string.Join(
            "\n",
            value.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("-->", "-- >", StringComparison.Ordinal)
        .Trim();

    private static TranscriptModel ToTranscriptModel(
        Transcript transcript,
        IReadOnlyList<TranscriptSegment> segments) =>
        new(
            transcript.Id,
            transcript.LessonId,
            transcript.Language,
            transcript.Revision,
            ToContract(transcript.ReviewStatus),
            segments.Select(segment => new TranscriptSegmentModel(
                segment.Sequence,
                segment.StartMilliseconds,
                segment.EndMilliseconds,
                segment.Text,
                segment.SpeakerLabel)).ToArray(),
            transcript.Version);

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<SnapshotCaption> captions,
        IReadOnlyList<SnapshotAsset> assets)
    {
        var warnings = new List<string>();
        if (captions.All(caption => caption.Language != LanguageTags.English))
        {
            warnings.Add("missingEnglishCaptions");
        }

        if (captions.All(caption => caption.Language != LanguageTags.Amharic))
        {
            warnings.Add("missingAmharicCaptions");
        }

        if (assets.All(asset => asset.Role != MediaAssetRole.SignLanguage))
        {
            warnings.Add("missingSignLanguage");
        }

        if (assets.All(asset => asset.Role != MediaAssetRole.AudioDescription))
        {
            warnings.Add("missingAudioDescription");
        }

        return warnings;
    }

    private static string LanguageLabel(string language) => language switch
    {
        LanguageTags.English => "English",
        LanguageTags.Amharic => "Amharic / አማርኛ",
        LanguageTags.EthiopianSignLanguage => "Ethiopian Sign Language",
        _ => language
    };

    private static string ToContract<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static RequestValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string[]> { [field] = [message] });

    private static SnapshotAsset? SelectSecondaryAsset(
        IReadOnlyList<SnapshotAsset> assets,
        MediaAssetRole role,
        SnapshotAsset primary)
    {
        var matches = assets.Where(asset => asset.Role == role).ToArray();
        if (matches.Length > 1)
        {
            throw new StateConflictException($"Only one active {role} asset can be published.");
        }

        var selected = matches.SingleOrDefault();
        long? requiredDuration = primary.DurationMilliseconds is null
            ? null
            : primary.DurationMilliseconds.Value + (selected?.SyncOffsetMilliseconds ?? 0);
        if (selected is not null &&
            (!selected.IsHumanReviewed || string.IsNullOrWhiteSpace(selected.AccessibilityMode) ||
             selected.DurationMilliseconds is null || requiredDuration is null ||
             Math.Abs(selected.DurationMilliseconds.Value - requiredDuration.Value) > 2_000 ||
             Math.Abs(selected.SyncOffsetMilliseconds) > 2_000))
        {
            throw new StateConflictException($"The {role} asset duration or synchronization offset is incompatible.");
        }

        return selected;
    }

    private sealed record PlayerPublicationSnapshot(
        string SchemaVersion,
        Guid LessonId,
        Guid PublicationId,
        int Revision,
        long LessonDraftRevision,
        string Title,
        string SourceLanguage,
        SnapshotAsset Primary,
        SnapshotAsset? Poster,
        IReadOnlyList<SnapshotCaption> Captions,
        SnapshotAsset? SignLanguage,
        SnapshotAsset? AudioDescription,
        IReadOnlyList<string> Warnings);

    private sealed record SnapshotAsset(
        Guid AssetId,
        Guid FileId,
        string ObjectKey,
        string ContentType,
        MediaAssetRole Role,
        long Version,
        string? Language,
        long? DurationMilliseconds,
        long SyncOffsetMilliseconds,
        bool IsHumanReviewed,
        string? AccessibilityMode);

    private sealed record SnapshotCaption(
        Guid TranscriptId,
        string Language,
        int Revision,
        Guid FileId,
        string ObjectKey,
        IReadOnlyList<TranscriptSegmentModel> Segments);

    private sealed record CaptionReference(
        Guid TranscriptId,
        string Language,
        int Revision,
        Guid? SourceMediaAssetId,
        long? SourceMediaVersion,
        Guid FileId,
        string ObjectKey);
}
