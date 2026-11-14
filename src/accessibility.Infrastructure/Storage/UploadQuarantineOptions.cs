namespace Accessibility.Infrastructure.Storage;

public sealed class UploadQuarantineOptions
{
    public const string SectionName = "LessonIngest";

    public string QuarantinePath { get; init; } = string.Empty;

    public int MaximumAgeMinutes { get; init; } = 120;
}
