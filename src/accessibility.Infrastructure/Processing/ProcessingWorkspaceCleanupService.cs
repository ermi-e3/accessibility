using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Processing;

public sealed class ProcessingWorkspaceCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<ProcessingRuntimeOptions> options,
    TimeProvider timeProvider,
    ILogger<ProcessingWorkspaceCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            try { await CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Processing workspace cleanup failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "AccessLearn", "processing");
        if (!Directory.Exists(root)) return;
        var now = timeProvider.GetUtcNow();
        var cutoff = now.Subtract(TimeSpan.FromMinutes(options.Value.WorkspaceMaximumAgeMinutes));
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                var name = Path.GetFileName(directory);
                var hasActiveLease = Guid.TryParseExact(name, "N", out var jobId) &&
                    await database.ProcessingJobs.AnyAsync(job =>
                        job.Id == jobId && job.Status == ProcessingJobStatus.Running &&
                        job.LeaseExpiresAt > now,
                        cancellationToken);
                if (!hasActiveLease && Directory.GetLastWriteTimeUtc(directory) < cutoff.UtcDateTime)
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove stale processing workspace {Workspace}", directory);
            }
        }
    }
}
