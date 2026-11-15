using Accessibility.Domain.Common;

namespace Accessibility.Domain.Entities;

public sealed class ProcessingIdempotencyRequest : Entity
{
    private ProcessingIdempotencyRequest()
    {
    }

    public ProcessingIdempotencyRequest(
        Guid ownerUserId,
        string clientKey,
        string requestFingerprint,
        Guid processingJobId,
        DateTimeOffset createdAt)
    {
        OwnerUserId = ownerUserId != Guid.Empty ? ownerUserId : throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        ClientKey = DomainValidation.RequiredText(clientKey, nameof(clientKey), 200);
        RequestFingerprint = DomainValidation.RequiredText(requestFingerprint, nameof(requestFingerprint), 200);
        ProcessingJobId = processingJobId != Guid.Empty ? processingJobId : throw new ArgumentException("Job is required.", nameof(processingJobId));
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid OwnerUserId { get; private set; }
    public string ClientKey { get; private set; } = string.Empty;
    public string RequestFingerprint { get; private set; } = string.Empty;
    public Guid ProcessingJobId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
