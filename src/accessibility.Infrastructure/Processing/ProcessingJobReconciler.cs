using Accessibility.Application.Abstractions;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Processing;

public sealed class ProcessingJobReconciler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ProcessingJobReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Processing job reconciliation failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var scheduler = scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>();
        var now = timeProvider.GetUtcNow();
        var expired = await database.ProcessingJobs
            .Where(job => job.Status == ProcessingJobStatus.Running && job.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);
        foreach (var job in expired)
        {
            job.Fail("LEASE_EXPIRED", "The processing lease expired before completion.", now);
            var attempt = await database.ProcessingJobAttempts.SingleOrDefaultAsync(
                item => item.ProcessingJobId == job.Id && item.AttemptNumber == job.Attempts,
                cancellationToken);
            if (attempt?.Status == ProcessingJobStatus.Running)
            {
                attempt.Fail("LEASE_EXPIRED", "The processing lease expired before completion.", now);
            }
            if (job.Attempts < 4) job.Requeue();
        }
        await database.SaveChangesAsync(cancellationToken);

        var queued = await database.ProcessingJobs
            .Where(job => job.Status == ProcessingJobStatus.Queued && job.Attempts < 4)
            .ToListAsync(cancellationToken);
        foreach (var job in queued)
        {
            try
            {
                if (job.ExternalJobId is not null && scheduler.IsActive(job.ExternalJobId)) continue;
                job.ClearExternalJobId();
                job.SetExternalJobId(scheduler.Enqueue(job.Id));
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not reconcile queued processing job {ProcessingJobId}", job.Id);
            }
        }
        await database.SaveChangesAsync(cancellationToken);
    }
}
