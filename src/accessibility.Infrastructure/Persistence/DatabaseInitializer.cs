using Accessibility.Application.Authorization;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Accessibility.FoundationInitializer");
        var bootstrapAdmin = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;
        if (bootstrapAdmin.HasAnyValue && !bootstrapAdmin.IsConfigured)
        {
            throw new InvalidOperationException(
                "BootstrapAdmin must provide Email, DisplayName, and Password together.");
        }

        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var migrationConnectionString = configuration.GetConnectionString("MigrationDatabase");
        if (string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            throw new InvalidOperationException("Connection string 'MigrationDatabase' is required for --initialize.");
        }

        var migrationOptions = new DbContextOptionsBuilder<AccessLearnDbContext>()
            .UseNpgsql(migrationConnectionString)
            .Options;
        logger.LogInformation("Applying AccessLearn database migrations.");
        await using (var migrationDatabase = new AccessLearnDbContext(migrationOptions, TimeProvider.System))
        {
            await migrationDatabase.Database.MigrateAsync(cancellationToken);
        }
        logger.LogInformation("Database migrations are current.");

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        logger.LogInformation("Ensuring AccessLearn Identity roles and bootstrap administrator.");
        foreach (var role in new[] { AccessLearnRoles.Educator, AccessLearnRoles.Administrator })
        {
            if (await roleManager.RoleExistsAsync(role))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new IdentityRole<Guid>(role));
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(error => error.Description));
                throw new InvalidOperationException($"Could not create role '{role}': {errors}");
            }
        }

        if (bootstrapAdmin.IsConfigured)
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var bootstrapEmail = bootstrapAdmin.Email.Trim();
            var administrator = await userManager.FindByEmailAsync(bootstrapEmail);
            if (administrator is null)
            {
                administrator = new ApplicationUser
                {
                    Id = Guid.CreateVersion7(),
                    Email = bootstrapEmail,
                    UserName = bootstrapEmail,
                    DisplayName = bootstrapAdmin.DisplayName.Trim(),
                    EmailConfirmed = true
                };

                var createResult = await userManager.CreateAsync(administrator, bootstrapAdmin.Password);
                if (!createResult.Succeeded)
                {
                    var errors = string.Join(", ", createResult.Errors.Select(error => error.Description));
                    throw new InvalidOperationException($"Could not create the bootstrap administrator: {errors}");
                }
            }

            if (!await userManager.IsInRoleAsync(administrator, AccessLearnRoles.Administrator))
            {
                var addRoleResult = await userManager.AddToRoleAsync(administrator, AccessLearnRoles.Administrator);
                if (!addRoleResult.Succeeded)
                {
                    var errors = string.Join(", ", addRoleResult.Errors.Select(error => error.Description));
                    throw new InvalidOperationException($"Could not assign the bootstrap administrator role: {errors}");
                }
            }
        }
        logger.LogInformation("Identity initialization is complete.");

        logger.LogInformation("Provisioning AccessLearn object storage.");
        var storageProvisioner = scope.ServiceProvider.GetRequiredService<ObjectStorageProvisioner>();
        await storageProvisioner.InitializeAsync(cancellationToken);
        logger.LogInformation("Object-storage initialization is complete.");
    }
}
