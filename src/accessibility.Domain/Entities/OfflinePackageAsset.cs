using Accessibility.Domain.Common;

namespace Accessibility.Domain.Entities;

public sealed class OfflinePackageAsset : Entity
{
    private OfflinePackageAsset()
    {
    }

    public OfflinePackageAsset(
        Guid offlinePackageId,
        Guid storedFileId,
        int sequence,
        string path,
        string role,
        string? language,
        bool isRequired,
        string? displayName = null)
    {
        OfflinePackageId = offlinePackageId != Guid.Empty
            ? offlinePackageId
            : throw new ArgumentException("Offline package is required.", nameof(offlinePackageId));
        StoredFileId = storedFileId != Guid.Empty
            ? storedFileId
            : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        Sequence = sequence;
        Path = DomainValidation.RequiredText(path, nameof(path), 512);
        Role = DomainValidation.RequiredText(role, nameof(role), 50);
        Language = DomainValidation.OptionalText(language, nameof(language), 35);
        IsRequired = isRequired;
        DisplayName = DomainValidation.OptionalText(displayName, nameof(displayName), 200);
    }

    public Guid OfflinePackageId { get; private set; }
    public Guid StoredFileId { get; private set; }
    public int Sequence { get; private set; }
    public string Path { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public string? Language { get; private set; }
    public bool IsRequired { get; private set; }
    public string? DisplayName { get; private set; }
}
