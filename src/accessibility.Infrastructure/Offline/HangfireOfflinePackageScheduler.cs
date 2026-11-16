using Accessibility.Application.Features.Offline;
using Hangfire;

namespace Accessibility.Infrastructure.Offline;

public sealed class HangfireOfflinePackageScheduler(IBackgroundJobClient jobs) : IOfflinePackageScheduler
{
    public string Enqueue(Guid offlinePackageId) =>
        jobs.Enqueue<OfflinePackageJobRunner>(runner => runner.ExecuteAsync(offlinePackageId));

    public string Schedule(Guid offlinePackageId, TimeSpan delay) =>
        jobs.Schedule<OfflinePackageJobRunner>(runner => runner.ExecuteAsync(offlinePackageId), delay);

    public bool IsActive(string externalJobId)
    {
        var state = JobStorage.Current.GetMonitoringApi().JobDetails(externalJobId)?.History.FirstOrDefault()?.StateName;
        return state is "Enqueued" or "Processing" or "Scheduled" or "Awaiting" or "Created";
    }
}
