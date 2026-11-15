namespace Accessibility.Infrastructure.Processing;

public sealed class GoogleTranslationOptions
{
    public const string SectionName = "GoogleTranslation";

    public bool Enabled { get; init; }

    public string ProjectId { get; init; } = string.Empty;

    public string Location { get; init; } = "global";

    public string Version { get; init; } = "google-nmt-v3";
}
