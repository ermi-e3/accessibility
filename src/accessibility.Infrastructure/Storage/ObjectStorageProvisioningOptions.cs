namespace Accessibility.Infrastructure.Storage;

public sealed class ObjectStorageProvisioningOptions
{
    public const string SectionName = "ObjectStorageProvisioning";

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;
}
