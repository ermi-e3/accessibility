using Accessibility.Application.Features.Offline;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Offline;

public sealed class OfflinePackageReconciler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<OfflinePackageReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
                var scheduler = scope.ServiceProvider.GetRequiredService<IOfflinePackageScheduler>();
                var now = timeProvider.GetUtcNow();
                var expired = await database.OfflinePackages
                    .Where(package => package.Status == OfflinePackageStatus.Building &&
                                      package.LeaseExpiresAt < now)
                    .ToListAsync(stoppingToken);
                foreach (var package in expired)
                {
                    package.Fail("LEASE_EXPIRED", "Offline package build lease expired.", now);
                    if (package.Attempts < 3) package.Retry();
                }
                await database.SaveChangesAsync(stoppingToken);
                var queued = await database.OfflinePackages
                    .Where(package => package.Status == OfflinePackageStatus.Queued && package.Attempts < 3)
                    .ToListAsync(stoppingToken);
                foreach (var package in queued)
                {
                    if (package.ExternalJobId is not null && scheduler.IsActive(package.ExternalJobId)) continue;
                    try { package.SetExternalJobId(scheduler.Enqueue(package.Id)); }
                    catch (Exception exception)
                    {
                        logger.LogWarning(exception, "Could not reconcile offline package {OfflinePackageId}", package.Id);
                    }
                }
                await database.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Offline package reconciliation failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
