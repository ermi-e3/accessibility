using Accessibility.Application.Abstractions;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Offline;

public sealed class OfflinePackageCleanupService(
    IServiceScopeFactory scopeFactory,
    IOptions<OfflineLearningOptions> options,
    TimeProvider timeProvider,
    ILogger<OfflinePackageCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await CleanupAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogError(exception, "Offline package cleanup failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        await CleanupWorkspacesAsync(database, cancellationToken);
        var cutoff = timeProvider.GetUtcNow().AddHours(-options.Value.RetentionHours);
        var packageIds = await database.OfflinePackages
            .Where(package =>
                (package.Status == OfflinePackageStatus.Stale ||
                 package.Status == OfflinePackageStatus.Failed &&
                 (package.Attempts >= 3 ||
                  package.ErrorCode == null ||
                  package.ErrorCode != "STORAGE_UNAVAILABLE" &&
                  package.ErrorCode != "PROCESSOR_UNAVAILABLE" &&
                  package.ErrorCode != "LEASE_EXPIRED")) &&
                package.UpdatedAt < cutoff)
            .Select(package => package.Id)
            .Take(20)
            .ToListAsync(cancellationToken);
        foreach (var packageId in packageIds)
        {
            var package = await database.OfflinePackages.SingleAsync(item => item.Id == packageId, cancellationToken);
            var files = await (
                    from packageAsset in database.OfflinePackageAssets
                    join file in database.StoredFiles on packageAsset.StoredFileId equals file.Id
                    where packageAsset.OfflinePackageId == packageId && file.Status != StoredFileStatus.Deleted
                    select file)
                .ToListAsync(cancellationToken);
            var standaloneIds = new[] { package.ManifestFileId, package.ZipFileId }
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .ToArray();
            files.AddRange(await database.StoredFiles
                .Where(file => standaloneIds.Contains(file.Id) && file.Status != StoredFileStatus.Deleted)
                .ToListAsync(cancellationToken));
            var prefix = $"offline/{package.Id:N}/";
            files.AddRange(await database.StoredFiles
                .Where(file => file.ObjectKey.StartsWith(prefix) && file.Status != StoredFileStatus.Deleted)
                .ToListAsync(cancellationToken));
            foreach (var file in files.DistinctBy(file => file.Id))
            {
                try
                {
                    await storage.DeleteAsync(file.ObjectKey, cancellationToken);
                    file.MarkDeleted();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not clean offline object {ObjectKey}", file.ObjectKey);
                }
            }
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CleanupWorkspacesAsync(
        AccessLearnDbContext database,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "AccessLearn", "offline");
        if (!Directory.Exists(root)) return;
        var cutoff = timeProvider.GetUtcNow().AddHours(-1);
        foreach (var packageDirectory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(packageDirectory);
            var hasActiveLease = Guid.TryParseExact(name, "N", out var packageId) &&
                await database.OfflinePackages.AnyAsync(package =>
                    package.Id == packageId && package.Status == OfflinePackageStatus.Building &&
                    package.LeaseExpiresAt > timeProvider.GetUtcNow(),
                    cancellationToken);
            if (hasActiveLease) continue;
            foreach (var attemptDirectory in Directory.EnumerateDirectories(packageDirectory))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(attemptDirectory) < cutoff.UtcDateTime)
                    {
                        Directory.Delete(attemptDirectory, recursive: true);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Could not remove stale offline workspace {Workspace}", attemptDirectory);
                }
            }
            foreach (var zipPath in Directory.EnumerateFiles(packageDirectory, "attempt-*.zip"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(zipPath) < cutoff.UtcDateTime) File.Delete(zipPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "Could not remove stale offline ZIP {ZipPath}", zipPath);
                }
            }
        }
    }
}
