using System.Security.Cryptography;
using System.Text;
using Accessibility.Application.Abstractions;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Identity;

public sealed class IntegrationApiKeyService(
    AccessLearnDbContext database,
    IOptions<IntegrationApiKeyOptions> options,
    TimeProvider timeProvider) : IIntegrationApiKeyService
{
    private const string VersionPrefix = "alk_v1";
    private readonly byte[] _pepper = Encoding.UTF8.GetBytes(options.Value.Pepper);

    public GeneratedIntegrationApiKey Generate()
    {
        var prefix = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secret = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var token = $"{VersionPrefix}.{prefix}.{secret}";
        return new GeneratedIntegrationApiKey(token, prefix, ComputeHash(token));
    }

    public async Task<AuthenticatedIntegrationClient?> AuthenticateAsync(
        string token,
        CancellationToken cancellationToken)
    {
        if (!TryReadPrefix(token, out var prefix))
        {
            return null;
        }

        var client = await database.IntegrationClients.AsNoTracking().SingleOrDefaultAsync(
            integrationClient => integrationClient.KeyPrefix == prefix &&
                                 integrationClient.Status == IntegrationClientStatus.Active,
            cancellationToken);
        if (client is null || !FixedTimeEquals(client.KeyHash, ComputeHash(token)))
        {
            return null;
        }

        var ownerIsActive = await database.Users.AnyAsync(
            user => user.Id == client.OwnerUserId && user.IsActive,
            cancellationToken);
        if (!ownerIsActive)
        {
            return null;
        }

        return new AuthenticatedIntegrationClient(
            client.Id,
            client.OwnerUserId,
            client.Name,
            client.Scopes,
            client.AllowedOrigins);
    }

    public Task RecordUseAsync(Guid clientId, CancellationToken cancellationToken)
    {
        return database.IntegrationClients
            .Where(integrationClient => integrationClient.Id == clientId)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(
                    integrationClient => integrationClient.LastUsedAt,
                    timeProvider.GetUtcNow()),
                cancellationToken);
    }

    private string ComputeHash(string token)
    {
        using var hmac = new HMACSHA256(_pepper);
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    }

    private static bool TryReadPrefix(string token, out string prefix)
    {
        prefix = string.Empty;
        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != VersionPrefix || parts[1].Length != 16 || parts[2].Length != 43)
        {
            return false;
        }

        prefix = parts[1];
        return prefix.All(character => char.IsAsciiHexDigit(character));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(left),
                Convert.FromHexString(right));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
