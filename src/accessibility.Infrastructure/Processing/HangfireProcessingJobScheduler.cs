using Accessibility.Application.Abstractions;
using Hangfire;

namespace Accessibility.Infrastructure.Processing;

public sealed class HangfireProcessingJobScheduler(IBackgroundJobClient jobs) : IBackgroundJobScheduler
{
    public string Enqueue(Guid processingJobId) =>
        jobs.Enqueue<ProcessingJobRunner>(runner => runner.ExecuteAsync(processingJobId));

    public string Schedule(Guid processingJobId, TimeSpan delay) =>
        jobs.Schedule<ProcessingJobRunner>(runner => runner.ExecuteAsync(processingJobId), delay);

    public bool Delete(string externalJobId) =>
        IsValidJobId(externalJobId) && jobs.Delete(externalJobId);

    public bool IsActive(string externalJobId)
    {
        if (!IsValidJobId(externalJobId))
        {
            return false;
        }
        var details = JobStorage.Current.GetMonitoringApi().JobDetails(externalJobId);
        var state = details?.History.FirstOrDefault()?.StateName;
        return state is "Enqueued" or "Processing" or "Scheduled" or "Awaiting" or "Created";
    }

    private static bool IsValidJobId(string value) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
}
