using Accessibility.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Accessibility.Infrastructure.Health;

public sealed class ObjectStorageHealthCheck(IObjectStorage objectStorage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await objectStorage.IsAvailableAsync(cancellationToken)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The S3-compatible object store or bucket is unavailable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new HealthCheckResult(
                context.Registration.FailureStatus,
                "The S3-compatible object store or bucket is unavailable.",
                exception);
        }
    }
}
