using Accessibility.Application.Authorization;
using Accessibility.Domain.Entities;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class IntegrationApiKeyServiceTests
{
    [DatabaseIntegrationFact]
    public async Task GeneratedKeyAuthenticatesAndRevocationTakesEffect()
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AccessLearnDbContext>().UseNpgsql(connectionString).Options;
        var ownerId = Guid.CreateVersion7();
        var clientId = Guid.Empty;
        var pepper = new string('p', 48);
        await using var database = new AccessLearnDbContext(options, TimeProvider.System);
        var email = $"api-key-{ownerId:N}@accesslearn.test";
        database.Users.Add(new ApplicationUser
        {
            Id = ownerId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "API Key Owner",
            EmailConfirmed = true
        });
        await database.SaveChangesAsync();

        var service = new IntegrationApiKeyService(
            database,
            Options.Create(new IntegrationApiKeyOptions { Pepper = pepper }),
            TimeProvider.System);
        var credential = service.Generate();
        var client = new IntegrationClient(
            ownerId,
            "Test client",
            credential.Prefix,
            credential.Hash,
            [AccessLearnScopes.LessonsRead],
            []);
        clientId = client.Id;
        database.IntegrationClients.Add(client);
        await database.SaveChangesAsync();

        try
        {
            var authenticated = await service.AuthenticateAsync(credential.Token, CancellationToken.None);
            Assert.NotNull(authenticated);
            Assert.Equal(ownerId, authenticated.OwnerUserId);
            Assert.Contains(AccessLearnScopes.LessonsRead, authenticated.Scopes);
            Assert.Null(await service.AuthenticateAsync(credential.Token + "x", CancellationToken.None));

            client.Revoke();
            await database.SaveChangesAsync();
            Assert.Null(await service.AuthenticateAsync(credential.Token, CancellationToken.None));
        }
        finally
        {
            await database.IntegrationClients.Where(value => value.Id == clientId).ExecuteDeleteAsync();
            await database.Users.Where(value => value.Id == ownerId).ExecuteDeleteAsync();
        }
    }
}
