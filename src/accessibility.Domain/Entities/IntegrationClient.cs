using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class IntegrationClient : AuditableEntity
{
    private IntegrationClient()
    {
    }

    public IntegrationClient(
        Guid ownerUserId,
        string name,
        string keyPrefix,
        string keyHash,
        IEnumerable<string> scopes,
        IEnumerable<string> allowedOrigins)
    {
        OwnerUserId = ownerUserId != Guid.Empty ? ownerUserId : throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        Name = DomainValidation.RequiredText(name, nameof(name), 200);
        KeyPrefix = DomainValidation.RequiredText(keyPrefix, nameof(keyPrefix), 32);
        KeyHash = DomainValidation.RequiredText(keyHash, nameof(keyHash), 256);
        Scopes = scopes.Select(scope => DomainValidation.RequiredText(scope, nameof(scopes), 100)).Distinct(StringComparer.Ordinal).ToArray();
        AllowedOrigins = allowedOrigins.Select(ValidateOrigin).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public Guid OwnerUserId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string KeyPrefix { get; private set; } = string.Empty;

    public string KeyHash { get; private set; } = string.Empty;

    public string[] Scopes { get; private set; } = [];

    public string[] AllowedOrigins { get; private set; } = [];

    public IntegrationClientStatus Status { get; private set; } = IntegrationClientStatus.Active;

    public DateTimeOffset? LastUsedAt { get; private set; }

    public void RecordUse(DateTimeOffset usedAt)
    {
        LastUsedAt = usedAt.ToUniversalTime();
    }

    public void Revoke()
    {
        Status = IntegrationClientStatus.Revoked;
    }

    private static string ValidateOrigin(string origin)
    {
        var normalized = DomainValidation.RequiredText(origin, nameof(origin), 2_048).TrimEnd('/');
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            uri.PathAndQuery != "/")
        {
            throw new ArgumentException("Origin must be an HTTPS origin or loopback HTTP origin without a path.", nameof(origin));
        }

        return normalized;
    }
}
