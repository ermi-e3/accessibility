using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class StoredFile : AuditableEntity
{
    private StoredFile()
    {
    }

    public StoredFile(string objectKey, string originalFileName, string contentType, long byteLength, string sha256)
    {
        if (byteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteLength), "File length must be positive.");
        }

        ObjectKey = DomainValidation.RequiredText(objectKey, nameof(objectKey), 1_024);
        OriginalFileName = DomainValidation.RequiredText(originalFileName, nameof(originalFileName), 255);
        ContentType = DomainValidation.RequiredText(contentType, nameof(contentType), 255).ToLowerInvariant();
        ByteLength = byteLength;
        Sha256 = DomainValidation.Sha256(sha256, nameof(sha256));
    }

    public string ObjectKey { get; private set; } = string.Empty;

    public string OriginalFileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long ByteLength { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public string? ETag { get; private set; }

    public StoredFileStatus Status { get; private set; } = StoredFileStatus.Quarantined;

    public string? RejectionReason { get; private set; }

    public void MarkReady(string? eTag)
    {
        if (Status != StoredFileStatus.Quarantined)
        {
            throw new InvalidOperationException("Only a quarantined file can be promoted.");
        }

        ETag = DomainValidation.OptionalText(eTag, nameof(eTag), 255);
        Status = StoredFileStatus.Ready;
    }

    public void Reject(string reason)
    {
        RejectionReason = DomainValidation.RequiredText(reason, nameof(reason), 1_000);
        Status = StoredFileStatus.Rejected;
    }

    public void MarkDeleted()
    {
        Status = StoredFileStatus.Deleted;
    }
}
