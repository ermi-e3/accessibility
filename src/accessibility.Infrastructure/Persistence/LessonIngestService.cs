using System.Text;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Lessons;
using Accessibility.Application.Features.Media;
using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Accessibility.Infrastructure.Persistence;

public sealed class LessonIngestService(
    AccessLearnDbContext database,
    IUserContext userContext,
    IObjectStorage objectStorage,
    IMediaProbe mediaProbe,
    ILogger<LessonIngestService> logger) : ILessonIngestService
{
    public async Task<LessonModel> CreateAsync(CreateLessonCommand command, CancellationToken cancellationToken)
    {
        var ownerId = RequireOwner();
        Lesson lesson;
        try
        {
            lesson = new Lesson(ownerId, command.Title, command.SourceLanguage, command.Description);
        }
        catch (ArgumentException exception)
        {
            throw Validation(exception.ParamName ?? "lesson", exception.Message);
        }

        database.Lessons.Add(lesson);
        await database.SaveChangesAsync(cancellationToken);
        return ToModel(lesson);
    }

    public async Task<LessonPageModel> ListAsync(LessonListQuery query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > 100)
        {
            throw Validation("limit", "Limit must be between 1 and 100.");
        }

        var lessons = VisibleLessons();
        if (query.Status.HasValue)
        {
            lessons = lessons.Where(lesson => lesson.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            var cursor = DecodeCursor(query.Cursor);
            lessons = lessons.Where(lesson =>
                lesson.UpdatedAt < cursor.UpdatedAt ||
                lesson.UpdatedAt == cursor.UpdatedAt && lesson.Id.CompareTo(cursor.Id) < 0);
        }

        var page = await lessons
            .OrderByDescending(lesson => lesson.UpdatedAt)
            .ThenByDescending(lesson => lesson.Id)
            .Take(query.Limit + 1)
            .ToListAsync(cancellationToken);
        var hasMore = page.Count > query.Limit;
        if (hasMore)
        {
            page.RemoveAt(page.Count - 1);
        }

        var nextCursor = hasMore && page.Count > 0
            ? EncodeCursor(page[^1].UpdatedAt, page[^1].Id)
            : null;
        return new LessonPageModel(page.Select(ToModel).ToArray(), nextCursor);
    }

    public async Task<LessonDetailModel> GetAsync(Guid lessonId, CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        var assets = await (
                from asset in database.MediaAssets
                join file in database.StoredFiles on asset.StoredFileId equals file.Id
                where asset.LessonId == lessonId
                orderby asset.CreatedAt, asset.Id
                select new AssetModel(
                    asset.Id,
                    ToContract(asset.Role),
                    asset.Language,
                    asset.SyncOffsetMilliseconds,
                    ToContract(asset.Status),
                    asset.DurationMilliseconds,
                    asset.Width,
                    asset.Height,
                    asset.Codec,
                    file.OriginalFileName,
                    file.ContentType,
                    file.ByteLength,
                    file.Sha256,
                    asset.CreatedAt))
            .ToListAsync(cancellationToken);
        return new LessonDetailModel(ToModel(lesson), assets);
    }

    public async Task EnsureCanManageAsync(Guid lessonId, CancellationToken cancellationToken)
    {
        if (!await VisibleLessons().AnyAsync(lesson => lesson.Id == lessonId, cancellationToken))
        {
            throw new ResourceNotFoundException("Lesson", lessonId);
        }
    }

    public async Task<AssetModel> IngestAssetAsync(
        Guid lessonId,
        AssetIngestCommand command,
        QuarantinedUpload upload,
        CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Lesson", lessonId);
        if (lesson.Status == LessonStatus.Archived)
        {
            throw new StateConflictException("An archived lesson cannot accept new assets.");
        }
        if (command.ReplaceExisting && lesson.Version != command.ExpectedLessonVersion)
        {
            throw new VersionConflictException();
        }

        var rule = AssetUploadPolicy.For(command.Role);
        if (command.SyncOffsetMilliseconds < 0)
        {
            throw Validation("syncOffsetMs", "Synchronization offset cannot be negative.");
        }

        if (upload.OriginalFileName.Length > 255)
        {
            throw Validation("file", "File name cannot exceed 255 characters.");
        }

        var canonicalContentType = rule.CanonicalContentType(upload.OriginalFileName, upload.ContentType);
        AssetUploadPolicy.ValidateSignature(rule, upload.OriginalFileName, upload.Header);
        if (upload.ByteLength > rule.MaximumBytes)
        {
            throw new FileTooLargeException(rule.MaximumBytes);
        }

        if (command.Role is MediaAssetRole.PrimaryVideo or MediaAssetRole.PrimaryAudio &&
            command.SyncOffsetMilliseconds != 0)
        {
            throw Validation("syncOffsetMs", "Primary media must use a zero synchronization offset.");
        }
        if (command.Role is MediaAssetRole.SignLanguage or MediaAssetRole.AudioDescription &&
            command.SyncOffsetMilliseconds > 2_000)
        {
            throw Validation("syncOffsetMs", "Accessibility-track offsets cannot exceed 2000 milliseconds.");
        }

        var activeQuery = database.MediaAssets.Where(asset => asset.LessonId == lessonId && asset.IsActive);
        activeQuery = command.Role is MediaAssetRole.PrimaryVideo or MediaAssetRole.PrimaryAudio
            ? activeQuery.Where(asset =>
                asset.Role == MediaAssetRole.PrimaryVideo || asset.Role == MediaAssetRole.PrimaryAudio)
            : activeQuery.Where(asset => asset.Role == command.Role);
        var activeAssets = await activeQuery.ToListAsync(cancellationToken);
        if (activeAssets.Count > 0 && !command.ReplaceExisting)
        {
            throw new DuplicateActiveResourceException("The lesson already has an active resource for this role.");
        }
        var probe = await mediaProbe.ProbeAsync(upload.FilePath, cancellationToken);
        if (rule.RequiresVideo && !probe.HasVideo || rule.RequiresAudio && !probe.HasAudio)
        {
            throw new FileContentInvalidException("The media does not contain the required stream type.");
        }

        if (rule.RequiresVideo && (probe.Width is null or <= 0 || probe.Height is null or <= 0))
        {
            throw new FileContentInvalidException("The video dimensions are invalid.");
        }
        AssetUploadPolicy.ValidateProbe(upload.OriginalFileName, probe);

        var language = command.Role == MediaAssetRole.SignLanguage
            ? command.Language ?? LanguageTags.EthiopianSignLanguage
            : command.Language ?? lesson.SourceLanguage;
        var extension = rule.CanonicalExtension(upload.OriginalFileName);
        var objectKey = $"lessons/{lesson.Id:N}/assets/{Guid.CreateVersion7():N}/{upload.Sha256[..16]}{extension}";
        var storedFile = new StoredFile(
            objectKey,
            upload.OriginalFileName,
            canonicalContentType,
            upload.ByteLength,
            upload.Sha256);
        MediaAsset asset;
        try
        {
            asset = new MediaAsset(
                lesson.Id,
                storedFile.Id,
                command.Role,
                language,
                command.SyncOffsetMilliseconds);
            if (command.Role is MediaAssetRole.SignLanguage or MediaAssetRole.AudioDescription)
            {
                if (!command.IsHumanReviewed)
                {
                    throw Validation("reviewed", "Accessibility tracks must be explicitly human reviewed.");
                }

                var requiredMode = command.Role == MediaAssetRole.SignLanguage ? "human-recorded" : "full-mix";
                if (!string.Equals(command.AccessibilityMode, requiredMode, StringComparison.OrdinalIgnoreCase))
                {
                    throw Validation("accessibilityMode", $"Accessibility mode must be '{requiredMode}'.");
                }

                asset.ConfirmAccessibilityReview(requiredMode);
            }
            if (activeAssets.Count > 0)
            {
                asset.StageAsReplacement();
            }
        }
        catch (ArgumentException exception)
        {
            throw Validation(exception.ParamName ?? "asset", exception.Message);
        }
        database.StoredFiles.Add(storedFile);
        database.MediaAssets.Add(asset);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsActiveResourceConflict(exception))
        {
            throw new DuplicateActiveResourceException("The lesson already has an active resource for this role.");
        }

        await using var content = new FileStream(
            upload.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        ObjectStorageWriteResult stored;
        try
        {
            stored = await objectStorage.PutAsync(
                objectKey,
                content,
                canonicalContentType,
                upload.ByteLength,
                new Dictionary<string, string>
                {
                    ["sha256"] = upload.Sha256,
                    ["lesson-id"] = lesson.Id.ToString("N")
                },
                cancellationToken);
        }
        catch (Exception uploadException)
        {
            asset.Reject();
            storedFile.Reject("Object storage did not confirm the upload.");
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await database.SaveChangesAsync(timeout.Token);
            }
            catch (Exception stateException)
            {
                logger.LogError(
                    stateException,
                    "Could not record rejected state for object {ObjectKey}",
                    objectKey);
            }

            if (uploadException is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new StorageUnavailableException("Object storage did not confirm the upload.", uploadException);
        }

        storedFile.MarkReady(stored.ETag);
        var codec = rule.RequiresVideo ? probe.VideoCodecs[0] : probe.AudioCodecs[0];
        asset.MarkReady(
            probe.DurationMilliseconds,
            rule.RequiresVideo ? probe.Width : null,
            rule.RequiresVideo ? probe.Height : null,
            codec);
        try
        {
            if (activeAssets.Count > 0)
            {
                await MarkDependentResourcesStaleAsync(
                    lesson.Id,
                    activeAssets.Select(active => active.Id).ToArray(),
                    cancellationToken);
                await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
                foreach (var activeAsset in activeAssets)
                {
                    activeAsset.Supersede();
                }
                await database.SaveChangesAsync(cancellationToken);
                asset.Activate();
                lesson.AdvanceDraftRevision();
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                lesson.AdvanceDraftRevision();
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception persistenceException)
        {
            logger.LogError(
                persistenceException,
                "Object {ObjectKey} is stored but metadata remains pending and requires reconciliation",
                objectKey);
            throw new StateConflictException(
                "The object was stored but metadata finalization failed. Retry after reconciliation.");
        }

        return new AssetModel(
            asset.Id,
            ToContract(asset.Role),
            asset.Language,
            asset.SyncOffsetMilliseconds,
            ToContract(asset.Status),
            asset.DurationMilliseconds,
            asset.Width,
            asset.Height,
            asset.Codec,
            storedFile.OriginalFileName,
            storedFile.ContentType,
            storedFile.ByteLength,
            storedFile.Sha256,
            asset.CreatedAt);
    }

    private async Task MarkDependentResourcesStaleAsync(
        Guid lessonId,
        Guid[] sourceAssetIds,
        CancellationToken cancellationToken)
    {
        var transcripts = await database.Transcripts
            .Where(transcript => transcript.SourceMediaAssetId.HasValue &&
                                 sourceAssetIds.Contains(transcript.SourceMediaAssetId.Value) &&
                                 transcript.ReviewStatus != ReviewStatus.Superseded)
            .ToListAsync(cancellationToken);
        foreach (var transcript in transcripts) transcript.MarkStale();
        var transcriptIds = transcripts.Select(transcript => transcript.Id).ToArray();
        var artifacts = await database.CaptionArtifacts
            .Where(artifact => transcriptIds.Contains(artifact.TranscriptId) && artifact.Status == ResourceStatus.Ready)
            .ToListAsync(cancellationToken);
        foreach (var artifact in artifacts) artifact.MarkStale();
        var derivedAssets = await database.MediaAssets
            .Where(asset => asset.SourceMediaAssetId.HasValue &&
                            sourceAssetIds.Contains(asset.SourceMediaAssetId.Value) && asset.IsActive)
            .ToListAsync(cancellationToken);
        foreach (var derivedAsset in derivedAssets) derivedAsset.Supersede();
        var packages = await (
                from package in database.OfflinePackages
                join publication in database.LessonPublications on package.LessonPublicationId equals publication.Id
                where publication.LessonId == lessonId && package.Status != OfflinePackageStatus.Stale
                select package)
            .ToListAsync(cancellationToken);
        foreach (var package in packages) package.MarkStale();
    }

    private IQueryable<Lesson> VisibleLessons()
    {
        var lessons = database.Lessons.AsQueryable();
        return userContext.IsInRole(AccessLearnRoles.Administrator)
            ? lessons
            : lessons.Where(lesson => lesson.OwnerUserId == RequireOwner());
    }

    private Guid RequireOwner()
    {
        return userContext.UserId ?? throw new UnauthorizedAccessException("An authenticated owner is required.");
    }

    private static bool IsActiveResourceConflict(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "i_x_media_assets_lesson_id" or "i_x_media_assets_lesson_id_role"
        };
    }

    private static LessonModel ToModel(Lesson lesson)
    {
        return new LessonModel(
            lesson.Id,
            lesson.OwnerUserId,
            lesson.Title,
            lesson.Description,
            lesson.SourceLanguage,
            ToContract(lesson.Status),
            lesson.DraftRevision,
            lesson.Version,
            lesson.CreatedAt,
            lesson.UpdatedAt);
    }

    private static string ToContract<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static RequestValidationException Validation(string field, string message)
    {
        return new RequestValidationException(new Dictionary<string, string[]> { [field] = [message] });
    }

    private static string EncodeCursor(DateTimeOffset updatedAt, Guid id)
    {
        var payload = JsonSerializer.Serialize(new LessonCursor(updatedAt.ToUniversalTime(), id));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static LessonCursor DecodeCursor(string cursor)
    {
        try
        {
            var value = cursor.Replace('-', '+').Replace('_', '/');
            value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<LessonCursor>(Convert.FromBase64String(value))
                ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw Validation("cursor", "Cursor is invalid.");
        }
    }

    private sealed record LessonCursor(DateTimeOffset UpdatedAt, Guid Id);
}
