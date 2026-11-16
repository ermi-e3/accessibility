namespace Accessibility.Application.Features.Offline;

public interface IOfflinePackageService
{
    Task<OfflinePackageModel> StartAsync(Guid lessonId, CancellationToken cancellationToken);

    Task<OfflinePackageModel> GetAsync(Guid packageId, CancellationToken cancellationToken);

    Task<OfflinePackageModel> RetryAsync(Guid packageId, CancellationToken cancellationToken);

    Task<PublicOfflineManifestV1> GetCurrentPublicManifestAsync(
        Guid lessonId,
        CancellationToken cancellationToken);

    Task<PublicOfflineManifestV1> GetPublicManifestAsync(
        Guid packageId,
        CancellationToken cancellationToken);
}

public interface IOfflinePackageScheduler
{
    string Enqueue(Guid offlinePackageId);

    string Schedule(Guid offlinePackageId, TimeSpan delay);

    bool IsActive(string externalJobId);
}

public sealed record OfflinePackageModel(
    Guid Id,
    Guid LessonPublicationId,
    string? Title,
    string Profile,
    string Status,
    long? TotalBytes,
    string? Checksum,
    string? ErrorCode,
    string? Error,
    int Attempts,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long Version);

public sealed record PublicOfflineManifestV1(
    string SchemaVersion,
    Guid PackageId,
    Guid LessonId,
    Guid PublicationId,
    int PublicationRevision,
    string Title,
    string Profile,
    long TotalBytes,
    string Checksum,
    string ZipUrl,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<PublicOfflineAssetV1> Assets);

public sealed record PublicOfflineAssetV1(
    int Sequence,
    string Path,
    string Url,
    string ContentType,
    long ByteLength,
    string Sha256,
    string Role,
    string? Language,
    bool IsRequired,
    string? DisplayName);
