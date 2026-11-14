namespace Accessibility.Infrastructure.Storage;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    public string ServiceUrl { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    public string BucketName { get; init; } = "accesslearn";

    public string Region { get; init; } = "us-east-1";

    public bool ForcePathStyle { get; init; } = true;

    public int DefaultReadUrlLifetimeMinutes { get; init; } = 30;

    public string[] CorsAllowedOrigins { get; init; } = ["*"];

    public bool ProvisionBucketCors { get; init; } = true;
}
