using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class ProcessingJob : AuditableEntity
{
    private ProcessingJob()
    {
    }

    public ProcessingJob(
        ProcessingJobType type,
        string idempotencyKey,
        Guid? lessonId = null,
        Guid? documentId = null,
        Guid? mediaAssetId = null,
        Guid? parentJobId = null,
        Guid? requestedByUserId = null,
        string? payloadJson = null,
        string? clientRequestKey = null,
        string? requestFingerprint = null)
    {
        if (lessonId is null && documentId is null && mediaAssetId is null)
        {
            throw new ArgumentException("A processing target is required.");
        }

        Type = type;
        IdempotencyKey = DomainValidation.RequiredText(idempotencyKey, nameof(idempotencyKey), 200);
        LessonId = lessonId;
        DocumentId = documentId;
        MediaAssetId = mediaAssetId;
        ParentJobId = parentJobId;
        RequestedByUserId = requestedByUserId;
        PayloadJson = DomainValidation.OptionalText(payloadJson, nameof(payloadJson), 8_000);
        ClientRequestKey = DomainValidation.OptionalText(clientRequestKey, nameof(clientRequestKey), 200);
        RequestFingerprint = DomainValidation.OptionalText(requestFingerprint, nameof(requestFingerprint), 200);
    }

    public ProcessingJobType Type { get; private set; }

    public ProcessingJobStatus Status { get; private set; } = ProcessingJobStatus.Queued;

    public Guid? LessonId { get; private set; }

    public Guid? DocumentId { get; private set; }

    public Guid? MediaAssetId { get; private set; }

    public Guid? ParentJobId { get; private set; }

    public Guid? RequestedByUserId { get; private set; }

    public string IdempotencyKey { get; private set; } = string.Empty;

    public string? Provider { get; private set; }

    public string? ExternalJobId { get; private set; }

    public string? PayloadJson { get; private set; }

    public string? ClientRequestKey { get; private set; }

    public string? RequestFingerprint { get; private set; }

    public int ProgressPercentage { get; private set; }

    public int Attempts { get; private set; }

    public string? ErrorCode { get; private set; }

    public string? ErrorMessage { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancellationRequestedAt { get; private set; }

    public Guid? ExecutionLeaseId { get; private set; }

    public DateTimeOffset? LeaseExpiresAt { get; private set; }

    public bool IsCancellationRequested => CancellationRequestedAt.HasValue;

    public void SetExternalJobId(string externalJobId)
    {
        ExternalJobId = DomainValidation.RequiredText(externalJobId, nameof(externalJobId), 200);
    }

    public void ClearExternalJobId()
    {
        ExternalJobId = null;
    }

    public void Requeue()
    {
        if (Status is not (ProcessingJobStatus.Failed or ProcessingJobStatus.Cancelled))
        {
            throw new InvalidOperationException("Only a failed job can be requeued.");
        }

        Status = ProcessingJobStatus.Queued;
        ProgressPercentage = 0;
        CompletedAt = null;
        ErrorCode = null;
        ErrorMessage = null;
        CancellationRequestedAt = null;
        ExternalJobId = null;
        ExecutionLeaseId = null;
        LeaseExpiresAt = null;
    }

    public void RequestCancellation(DateTimeOffset requestedAt)
    {
        if (Status is not (ProcessingJobStatus.Queued or ProcessingJobStatus.Running))
        {
            throw new InvalidOperationException("Only a queued or running job can be cancelled.");
        }

        CancellationRequestedAt = requestedAt.ToUniversalTime();
        if (Status == ProcessingJobStatus.Queued)
        {
            Cancel(requestedAt);
        }
    }

    public void FailBeforeStart(string errorCode, string errorMessage, DateTimeOffset completedAt)
    {
        if (Status != ProcessingJobStatus.Queued)
        {
            throw new InvalidOperationException("Only a queued job can fail before start.");
        }

        ErrorCode = DomainValidation.RequiredText(errorCode, nameof(errorCode), 100);
        ErrorMessage = DomainValidation.RequiredText(errorMessage, nameof(errorMessage), 2_000);
        CompletedAt = completedAt.ToUniversalTime();
        Status = ProcessingJobStatus.Failed;
    }

    public void AcquireLease(Guid leaseId, DateTimeOffset expiresAt)
    {
        if (Status != ProcessingJobStatus.Running)
        {
            throw new InvalidOperationException("Only a running job can acquire an execution lease.");
        }
        ExecutionLeaseId = leaseId;
        LeaseExpiresAt = expiresAt.ToUniversalTime();
    }

    public void RenewLease(Guid leaseId, DateTimeOffset expiresAt)
    {
        if (ExecutionLeaseId != leaseId || Status != ProcessingJobStatus.Running)
        {
            throw new InvalidOperationException("Processing lease is no longer owned by this execution.");
        }
        LeaseExpiresAt = expiresAt.ToUniversalTime();
    }

    public void ReleaseLease()
    {
        ExecutionLeaseId = null;
        LeaseExpiresAt = null;
    }

    public void Start(DateTimeOffset startedAt, string? provider = null, string? externalJobId = null)
    {
        if (Status is not (ProcessingJobStatus.Queued or ProcessingJobStatus.Failed))
        {
            throw new InvalidOperationException("Only a queued or failed job can start.");
        }

        Attempts++;
        ProgressPercentage = 0;
        StartedAt = startedAt.ToUniversalTime();
        CompletedAt = null;
        ErrorCode = null;
        ErrorMessage = null;
        Provider = DomainValidation.OptionalText(provider, nameof(provider), 100);
        ExternalJobId = DomainValidation.OptionalText(externalJobId, nameof(externalJobId), 200);
        Status = ProcessingJobStatus.Running;
    }

    public void ReportProgress(int percentage)
    {
        if (Status != ProcessingJobStatus.Running)
        {
            throw new InvalidOperationException("Only a running job can report progress.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(percentage, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentage, 100);
        ProgressPercentage = percentage;
    }

    public void Succeed(DateTimeOffset completedAt)
    {
        if (Status != ProcessingJobStatus.Running)
        {
            throw new InvalidOperationException("Only a running job can succeed.");
        }

        ProgressPercentage = 100;
        CompletedAt = completedAt.ToUniversalTime();
        Status = ProcessingJobStatus.Succeeded;
        ReleaseLease();
    }

    public void Fail(string errorCode, string errorMessage, DateTimeOffset completedAt)
    {
        if (Status != ProcessingJobStatus.Running)
        {
            throw new InvalidOperationException("Only a running job can fail.");
        }

        ErrorCode = DomainValidation.RequiredText(errorCode, nameof(errorCode), 100);
        ErrorMessage = DomainValidation.RequiredText(errorMessage, nameof(errorMessage), 2_000);
        CompletedAt = completedAt.ToUniversalTime();
        Status = ProcessingJobStatus.Failed;
        ReleaseLease();
    }

    public void Cancel(DateTimeOffset completedAt)
    {
        if (Status is not (ProcessingJobStatus.Queued or ProcessingJobStatus.Running))
        {
            throw new InvalidOperationException("Only a queued or running job can be cancelled.");
        }

        CompletedAt = completedAt.ToUniversalTime();
        Status = ProcessingJobStatus.Cancelled;
        ReleaseLease();
    }
}
