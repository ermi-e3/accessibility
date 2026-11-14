namespace Accessibility.Application.Abstractions;

public interface IIntegrationApiKeyService
{
    GeneratedIntegrationApiKey Generate();

    Task<AuthenticatedIntegrationClient?> AuthenticateAsync(string token, CancellationToken cancellationToken);

    Task RecordUseAsync(Guid clientId, CancellationToken cancellationToken);
}

public sealed record GeneratedIntegrationApiKey(string Token, string Prefix, string Hash);

public sealed record AuthenticatedIntegrationClient(
    Guid ClientId,
    Guid OwnerUserId,
    string Name,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> AllowedOrigins);
