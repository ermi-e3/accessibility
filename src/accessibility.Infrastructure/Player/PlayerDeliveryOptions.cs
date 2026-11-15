namespace Accessibility.Infrastructure.Player;

public sealed class PlayerDeliveryOptions
{
    public const string SectionName = "PlayerDelivery";

    public int SignedUrlMinutes { get; init; } = 30;

    public string PublicApiBaseUrl { get; init; } = string.Empty;
}
