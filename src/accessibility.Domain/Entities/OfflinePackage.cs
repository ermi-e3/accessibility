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

    public void MarkBuilding()
    {
        Status = OfflinePackageStatus.Building;
    }

    public void MarkReady(Guid manifestFileId, Guid zipFileId, long totalBytes, string checksum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(totalBytes, 1);
        ManifestFileId = manifestFileId != Guid.Empty ? manifestFileId : throw new ArgumentException("Manifest file is required.", nameof(manifestFileId));
        ZipFileId = zipFileId != Guid.Empty ? zipFileId : throw new ArgumentException("ZIP file is required.", nameof(zipFileId));
        TotalBytes = totalBytes;
        Checksum = DomainValidation.Sha256(checksum, nameof(checksum));
        Error = null;
        Status = OfflinePackageStatus.Ready;
    }

    public void Fail(string error)
    {
        Error = DomainValidation.RequiredText(error, nameof(error), 2_000);
        Status = OfflinePackageStatus.Failed;
    }

    public void MarkStale()
    {
        Status = OfflinePackageStatus.Stale;
    }
}
