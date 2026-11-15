using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class ProcessingJobAttempt : Entity
{
    private ProcessingJobAttempt()
    {
    }

    public ProcessingJobAttempt(Guid processingJobId, int attemptNumber, string? provider, DateTimeOffset startedAt)
    {
        ProcessingJobId = processingJobId != Guid.Empty
            ? processingJobId
            : throw new ArgumentException("Processing job is required.", nameof(processingJobId));
        ArgumentOutOfRangeException.ThrowIfLessThan(attemptNumber, 1);
        AttemptNumber = attemptNumber;
        Provider = DomainValidation.OptionalText(provider, nameof(provider), 100);
        StartedAt = startedAt.ToUniversalTime();
    }

    public Guid ProcessingJobId { get; private set; }
    public int AttemptNumber { get; private set; }
    public ProcessingJobStatus Status { get; private set; } = ProcessingJobStatus.Running;
    public string? Provider { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Version { get; internal set; } = 1;

    public void Succeed(DateTimeOffset completedAt)
    {
        EnsureRunning();
        Status = ProcessingJobStatus.Succeeded;
        CompletedAt = completedAt.ToUniversalTime();
        Version++;
    }

    public void Fail(string code, string message, DateTimeOffset completedAt)
    {
        EnsureRunning();
        ErrorCode = DomainValidation.RequiredText(code, nameof(code), 100);
        ErrorMessage = DomainValidation.RequiredText(message, nameof(message), 2_000);
        Status = ProcessingJobStatus.Failed;
        CompletedAt = completedAt.ToUniversalTime();
        Version++;
    }

    public void Cancel(DateTimeOffset completedAt)
    {
        EnsureRunning();
        Status = ProcessingJobStatus.Cancelled;
        CompletedAt = completedAt.ToUniversalTime();
        Version++;
    }

    private void EnsureRunning()
    {
        if (Status != ProcessingJobStatus.Running)
        {
            throw new InvalidOperationException("Only a running processing attempt can transition to a terminal state.");
        }
    }
}
