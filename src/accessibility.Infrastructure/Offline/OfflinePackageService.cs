using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Offline;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Offline;

public sealed class OfflinePackageService(
    AccessLearnDbContext database,
    IUserContext userContext,
    IOfflinePackageScheduler scheduler,
    IObjectStorage storage,
    TimeProvider timeProvider,
    IOptions<OfflineLearningOptions> options,
    ILogger<OfflinePackageService> logger) : IOfflinePackageService
{
    private readonly OfflineLearningOptions _options = options.Value;

    public async Task<OfflinePackageModel> StartAsync(Guid lessonId, CancellationToken cancellationToken)
    {
        var lesson = await VisibleLessons().SingleOrDefaultAsync(item => item.Id == lessonId, cancellationToken)
            ?? throw new ResourceNotFoundException("Published lesson", lessonId);
        if (lesson.Status != LessonStatus.Published || lesson.CurrentPublicationId is null)
        {
            throw new StateConflictException("The lesson must have a current publication before offline packaging.");
        }

        var existing = await database.OfflinePackages.SingleOrDefaultAsync(package =>
            package.LessonPublicationId == lesson.CurrentPublicationId.Value &&
            package.Profile == OfflinePackageProfile.Lightweight,
            cancellationToken);
        if (existing is not null) return ToModel(existing);

        var package = new OfflinePackage(lesson.CurrentPublicationId.Value, OfflinePackageProfile.Lightweight);
        database.OfflinePackages.Add(package);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            database.ChangeTracker.Clear();
            return ToModel(await database.OfflinePackages.SingleAsync(item =>
                item.LessonPublicationId == lesson.CurrentPublicationId.Value &&
                item.Profile == OfflinePackageProfile.Lightweight,
                cancellationToken));
        }
        try
        {
            package.SetExternalJobId(scheduler.Enqueue(package.Id));
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Offline package {OfflinePackageId} remains queued", package.Id);
        }
        return ToModel(package);
    }

    public async Task<OfflinePackageModel> GetAsync(Guid packageId, CancellationToken cancellationToken) =>
        ToModel(await VisiblePackages().SingleOrDefaultAsync(package => package.Id == packageId, cancellationToken)
            ?? throw new ResourceNotFoundException("Offline package", packageId));

    public async Task<OfflinePackageModel> RetryAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await VisiblePackages().SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
            ?? throw new ResourceNotFoundException("Offline package", packageId);
        if (package.Attempts >= 3)
        {
            throw new StateConflictException("The offline package reached its retry limit.");
        }
        if (package.ErrorCode is not ("STORAGE_UNAVAILABLE" or "PROCESSOR_UNAVAILABLE" or "LEASE_EXPIRED"))
        {
            throw new StateConflictException("The offline package failure is not classified as retryable.");
        }
        try { package.Retry(); }
        catch (InvalidOperationException exception) { throw new StateConflictException(exception.Message); }
        await database.SaveChangesAsync(cancellationToken);
        try
        {
            package.SetExternalJobId(scheduler.Enqueue(package.Id));
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Offline package {OfflinePackageId} remains queued", package.Id);
        }
        return ToModel(package);
    }

    public async Task<PublicOfflineManifestV1> GetCurrentPublicManifestAsync(
        Guid lessonId,
        CancellationToken cancellationToken)
    {
        var publicationId = await database.Lessons
            .Where(lesson => lesson.Id == lessonId && lesson.Status == LessonStatus.Published)
            .Select(lesson => lesson.CurrentPublicationId)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Current offline package", lessonId);
        var packageId = await database.OfflinePackages
            .Where(package => package.LessonPublicationId == publicationId &&
                              package.Profile == OfflinePackageProfile.Lightweight &&
                              package.Status == OfflinePackageStatus.Ready)
            .Select(package => (Guid?)package.Id)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Current offline package", lessonId);
        return await GetPublicManifestAsync(packageId, cancellationToken);
    }

    public async Task<PublicOfflineManifestV1> GetPublicManifestAsync(
        Guid packageId,
        CancellationToken cancellationToken)
    {
        var staleRefreshCutoff = timeProvider.GetUtcNow()
            .AddHours(-_options.RetentionHours)
            .AddMinutes(_options.SignedUrlMinutes);
        var result = await (
                from package in database.OfflinePackages
                join publication in database.LessonPublications on package.LessonPublicationId equals publication.Id
                join lesson in database.Lessons on publication.LessonId equals lesson.Id
                join zip in database.StoredFiles on package.ZipFileId equals (Guid?)zip.Id
                where package.Id == packageId && lesson.Status == LessonStatus.Published &&
                      package.Profile == OfflinePackageProfile.Lightweight &&
                      (package.Status == OfflinePackageStatus.Ready ||
                       package.Status == OfflinePackageStatus.Stale && package.UpdatedAt > staleRefreshCutoff) &&
                      zip.Status == StoredFileStatus.Ready
                select new { lesson, publication, package, zip })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ResourceNotFoundException("Offline package", packageId);
        var assets = await (
                from packageAsset in database.OfflinePackageAssets
                join file in database.StoredFiles on packageAsset.StoredFileId equals file.Id
                where packageAsset.OfflinePackageId == result.package.Id && file.Status == StoredFileStatus.Ready
                orderby packageAsset.Sequence
                select new { packageAsset, file })
            .ToListAsync(cancellationToken);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < assets.Count; index++)
        {
            var asset = assets[index];
            var path = asset.packageAsset.Path;
            if (asset.packageAsset.Sequence != index || !paths.Add(path) ||
                path.StartsWith('/') || path.Contains('\\') ||
                path.Split('/').Any(part => part is "" or "." or ".."))
            {
                throw new FileContentInvalidException("Offline package asset metadata is invalid.");
            }
        }
        var totalBytes = assets.Sum(asset => asset.file.ByteLength);
        var hasOfflineAudio = assets.Any(asset =>
            asset.packageAsset.Role == "offlineAudio" && asset.packageAsset.IsRequired);
        var hasIndex = assets.Any(asset => asset.packageAsset.Path == "index.html" && asset.packageAsset.IsRequired);
        var hasLesson = assets.Any(asset => asset.packageAsset.Path == "lesson.json" && asset.packageAsset.IsRequired);
        var hasStyles = assets.Any(asset => asset.packageAsset.Path == "styles.css" && asset.packageAsset.IsRequired);
        if (assets.Count == 0 || !hasOfflineAudio || !hasIndex || !hasLesson || !hasStyles ||
            result.package.TotalBytes != totalBytes)
        {
            throw new FileContentInvalidException("Offline package is incomplete.");
        }
        var lifetime = TimeSpan.FromMinutes(_options.SignedUrlMinutes);
        var publicAssets = new List<PublicOfflineAssetV1>(assets.Count);
        foreach (var asset in assets)
        {
            publicAssets.Add(new PublicOfflineAssetV1(
                asset.packageAsset.Sequence,
                asset.packageAsset.Path,
                (await storage.CreateReadUriAsync(asset.file.ObjectKey, lifetime, cancellationToken)).ToString(),
                asset.file.ContentType,
                asset.file.ByteLength,
                asset.file.Sha256,
                asset.packageAsset.Role,
                asset.packageAsset.Language,
                asset.packageAsset.IsRequired,
                asset.packageAsset.DisplayName));
        }
        return new PublicOfflineManifestV1(
            "1.0",
            result.package.Id,
            result.lesson.Id,
            result.publication.Id,
            result.publication.Revision,
            result.package.Title ?? throw new FileContentInvalidException("Offline package title is missing."),
            "lightweight",
            totalBytes,
            result.package.Checksum ?? result.zip.Sha256,
            (await storage.CreateReadUriAsync(result.zip.ObjectKey, lifetime, cancellationToken)).ToString(),
            timeProvider.GetUtcNow().Add(lifetime),
            publicAssets);
    }

    private IQueryable<Lesson> VisibleLessons() => userContext.IsInRole(AccessLearnRoles.Administrator)
        ? database.Lessons
        : database.Lessons.Where(lesson => lesson.OwnerUserId == RequireOwner());

    private IQueryable<OfflinePackage> VisiblePackages() =>
        from package in database.OfflinePackages
        join publication in database.LessonPublications on package.LessonPublicationId equals publication.Id
        join lesson in VisibleLessons() on publication.LessonId equals lesson.Id
        select package;

    private Guid RequireOwner() =>
        userContext.UserId ?? throw new UnauthorizedAccessException("An authenticated owner is required.");

    private static OfflinePackageModel ToModel(OfflinePackage package) => new(
        package.Id,
        package.LessonPublicationId,
        package.Title,
        package.Profile.ToString().ToLowerInvariant(),
        package.Status.ToString().ToLowerInvariant(),
        package.TotalBytes,
        package.Checksum,
        package.ErrorCode,
        package.Error,
        package.Attempts,
        package.StartedAt,
        package.CompletedAt,
        package.Version);
}
