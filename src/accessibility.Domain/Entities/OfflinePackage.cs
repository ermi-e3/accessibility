using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class OfflinePackage : AuditableEntity
{
    private OfflinePackage()
    {
    }

    public OfflinePackage(Guid lessonPublicationId, OfflinePackageProfile profile)
    {
        LessonPublicationId = lessonPublicationId != Guid.Empty
            ? lessonPublicationId
            : throw new ArgumentException("Lesson publication is required.", nameof(lessonPublicationId));
        Profile = profile;
    }

    public Guid LessonPublicationId { get; private set; }

    public OfflinePackageProfile Profile { get; private set; }

    public OfflinePackageStatus Status { get; private set; } = OfflinePackageStatus.Queued;

    public Guid? ManifestFileId { get; private set; }

    public Guid? ZipFileId { get; private set; }

    public long? TotalBytes { get; private set; }

    public string? Checksum { get; private set; }

    public string? Error { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ExternalJobId { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? ExecutionLeaseId { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public string? Title { get; private set; }

    public void SetExternalJobId(string externalJobId)
    {
        ExternalJobId = DomainValidation.RequiredText(externalJobId, nameof(externalJobId), 200);
    }

    public void MarkBuilding(DateTimeOffset startedAt, Guid leaseId, DateTimeOffset leaseExpiresAt)
    {
        if (Status is not (OfflinePackageStatus.Queued or OfflinePackageStatus.Failed))
        {
            throw new InvalidOperationException("Only a queued or failed package can start building.");
        }
        Attempts++;
        StartedAt = startedAt.ToUniversalTime();
        CompletedAt = null;
        Error = null;
        ErrorCode = null;
        ExecutionLeaseId = leaseId;
        LeaseExpiresAt = leaseExpiresAt.ToUniversalTime();
        Status = OfflinePackageStatus.Building;
    }

    public void MarkReady(
        Guid manifestFileId,
        Guid zipFileId,
        long totalBytes,
        string checksum,
        string title,
        DateTimeOffset completedAt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBytes, 1);
        ManifestFileId = manifestFileId != Guid.Empty ? manifestFileId : throw new ArgumentException("Manifest file is required.", nameof(manifestFileId));
        ZipFileId = zipFileId != Guid.Empty ? zipFileId : throw new ArgumentException("ZIP file is required.", nameof(zipFileId));
        TotalBytes = totalBytes;
        Checksum = DomainValidation.Sha256(checksum, nameof(checksum));
        Title = DomainValidation.RequiredText(title, nameof(title), 200);
        Error = null;
        ErrorCode = null;
        CompletedAt = completedAt.ToUniversalTime();
        Status = OfflinePackageStatus.Ready;
        ReleaseLease();
    }

    public void Fail(string errorCode, string error, DateTimeOffset completedAt)
    {
        ErrorCode = DomainValidation.RequiredText(errorCode, nameof(errorCode), 100);
        Error = DomainValidation.RequiredText(error, nameof(error), 2_000);
        CompletedAt = completedAt.ToUniversalTime();
        Status = OfflinePackageStatus.Failed;
        ReleaseLease();
    }

    public void MarkStale()
    {
        Status = OfflinePackageStatus.Stale;
    }

    public void Retry()
    {
        if (Status != OfflinePackageStatus.Failed)
        {
            throw new InvalidOperationException("Only a failed package can be retried.");
        }
        Status = OfflinePackageStatus.Queued;
        ExternalJobId = null;
        Error = null;
        ErrorCode = null;
        CompletedAt = null;
        ReleaseLease();
    }

    public void RenewLease(Guid leaseId, DateTimeOffset expiresAt)
    {
        if (Status != OfflinePackageStatus.Building || ExecutionLeaseId != leaseId)
        {
            throw new InvalidOperationException("Offline package lease is no longer owned by this worker.");
        }
        LeaseExpiresAt = expiresAt.ToUniversalTime();
    }

    public void ReleaseLease()
    {
        ExecutionLeaseId = null;
        LeaseExpiresAt = null;
    }
}
