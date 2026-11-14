namespace Accessibility.Api.Configuration;

public sealed class LocalizationSettings
{
    public const string SectionName = "Localization";

    public string DefaultCulture { get; init; } = "en";

    public string[] SupportedCultures { get; init; } = ["en", "am"];
}
