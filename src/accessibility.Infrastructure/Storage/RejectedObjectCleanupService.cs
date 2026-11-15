using Accessibility.Application.Abstractions;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Accessibility.Infrastructure.Storage;

public sealed class RejectedObjectCleanupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<RejectedObjectCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Rejected object cleanup failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        var cutoff = timeProvider.GetUtcNow().AddHours(-24);
        var fileIds = await database.StoredFiles
            .Where(file => (file.Status == StoredFileStatus.Rejected || file.Status == StoredFileStatus.Deleting) &&
                           file.UpdatedAt < cutoff)
            .OrderBy(file => file.UpdatedAt)
            .Take(100)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);
        foreach (var fileId in fileIds)
        {
            database.ChangeTracker.Clear();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var file = await database.StoredFiles
                .FromSqlInterpolated($"SELECT * FROM stored_files WHERE id = {fileId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (file is null || file.Status is not (StoredFileStatus.Rejected or StoredFileStatus.Deleting) ||
                file.UpdatedAt >= cutoff)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }
            var retryableProcessingOutput = file.Status == StoredFileStatus.Rejected && await (
                    from asset in database.MediaAssets
                    join job in database.ProcessingJobs on asset.ProcessingJobId equals job.Id
                    where asset.StoredFileId == file.Id &&
                          (job.Status == ProcessingJobStatus.Queued || job.Status == ProcessingJobStatus.Running ||
                           job.Status == ProcessingJobStatus.Cancelled && job.Attempts < 4 ||
                           job.Status == ProcessingJobStatus.Failed && job.Attempts < 4)
                    select asset.Id)
                .AnyAsync(cancellationToken);
            var retryableCaption = file.Status == StoredFileStatus.Rejected && await (
                    from artifact in database.CaptionArtifacts
                    join transcript in database.Transcripts on artifact.TranscriptId equals transcript.Id
                    where artifact.StoredFileId == file.Id && transcript.ReviewStatus == ReviewStatus.Draft
                    select artifact.Id)
                .AnyAsync(cancellationToken);
            if (retryableProcessingOutput || retryableCaption)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }
            if (file.Status == StoredFileStatus.Rejected)
            {
                file.MarkDeleting();
                await database.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            try
            {
                await storage.DeleteAsync(file.ObjectKey, cancellationToken);
                file.MarkDeleted();
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not delete rejected object {ObjectKey}", file.ObjectKey);
                try
                {
                    await database.Entry(file).ReloadAsync(CancellationToken.None);
                    if (file.Status == StoredFileStatus.Deleting)
                    {
                        file.RestoreRejected();
                        await database.SaveChangesAsync(CancellationToken.None);
                    }
                }
                catch (Exception recoveryException)
                {
                    logger.LogWarning(
                        recoveryException,
                        "Could not restore cleanup state for object {ObjectKey}",
                        file.ObjectKey);
                }
            }
        }
    }
}
