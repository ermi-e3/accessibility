namespace Accessibility.Infrastructure.Identity;

public sealed class IntegrationApiKeyOptions
{
    public const string SectionName = "ApiKeyAuthentication";

    public string Pepper { get; init; } = string.Empty;
}
