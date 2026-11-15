using Accessibility.Application.Abstractions;
using Hangfire;

namespace Accessibility.Infrastructure.Processing;

public sealed class HangfireProcessingJobScheduler(IBackgroundJobClient jobs) : IBackgroundJobScheduler
{
    public string Enqueue(Guid processingJobId) =>
        jobs.Enqueue<ProcessingJobRunner>(runner => runner.ExecuteAsync(processingJobId));

    public string Schedule(Guid processingJobId, TimeSpan delay) =>
        jobs.Schedule<ProcessingJobRunner>(runner => runner.ExecuteAsync(processingJobId), delay);

    public bool Delete(string externalJobId) => jobs.Delete(externalJobId);

    public bool IsActive(string externalJobId)
    {
        var details = JobStorage.Current.GetMonitoringApi().JobDetails(externalJobId);
        var state = details?.History.FirstOrDefault()?.StateName;
        return state is "Enqueued" or "Processing" or "Scheduled" or "Awaiting" or "Created";
    }
}
