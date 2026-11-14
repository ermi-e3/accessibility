using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Storage;

public sealed class UploadQuarantineCleanupService(
    TemporaryUploadQuarantine quarantine,
    IOptions<UploadQuarantineOptions> options,
    TimeProvider timeProvider,
    ILogger<UploadQuarantineCleanupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(quarantine.QuarantinePath))
        {
            return Task.CompletedTask;
        }

        var cutoff = timeProvider.GetUtcNow().Subtract(TimeSpan.FromMinutes(options.Value.MaximumAgeMinutes));
        foreach (var filePath in Directory.EnumerateFiles(quarantine.QuarantinePath, "*.upload"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(filePath) < cutoff.UtcDateTime)
                {
                    File.Delete(filePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Could not remove stale quarantine file {FilePath}", filePath);
            }
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
