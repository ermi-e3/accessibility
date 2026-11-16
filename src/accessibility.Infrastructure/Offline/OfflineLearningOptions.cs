namespace Accessibility.Infrastructure.Offline;

public sealed class OfflineLearningOptions
{
    public const string SectionName = "OfflineLearning";

    public int SignedUrlMinutes { get; init; } = 30;

    public long MaximumPackageBytes { get; init; } = 262_144_000;

    public int RetentionHours { get; init; } = 24;
}
