using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Accessibility.Infrastructure.Persistence;

public sealed class AccessLearnDbContextFactory : IDesignTimeDbContextFactory<AccessLearnDbContext>
{
    public AccessLearnDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_MIGRATION_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=accesslearn;Username=accesslearn_migrator;Password=design-time-only";

        var options = new DbContextOptionsBuilder<AccessLearnDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AccessLearnDbContext(options, TimeProvider.System);
    }
}
