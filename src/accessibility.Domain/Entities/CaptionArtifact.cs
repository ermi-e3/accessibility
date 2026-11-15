using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class CaptionArtifact : AuditableEntity
{
    private CaptionArtifact()
    {
    }

    public CaptionArtifact(Guid transcriptId, Guid storedFileId, CaptionFormat format, string generatorVersion, string checksum)
    {
        TranscriptId = transcriptId != Guid.Empty ? transcriptId : throw new ArgumentException("Transcript is required.", nameof(transcriptId));
        StoredFileId = storedFileId != Guid.Empty ? storedFileId : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        Format = format;
        GeneratorVersion = DomainValidation.RequiredText(generatorVersion, nameof(generatorVersion), 100);
        Checksum = DomainValidation.Sha256(checksum, nameof(checksum));
    }

    public Guid TranscriptId { get; private set; }

    public Guid StoredFileId { get; private set; }

    public CaptionFormat Format { get; private set; }

    public string GeneratorVersion { get; private set; } = string.Empty;

    public string Checksum { get; private set; } = string.Empty;

    public ResourceStatus Status { get; private set; } = ResourceStatus.Pending;

    public void MarkReady()
    {
        if (Status != ResourceStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending caption artifact can become ready.");
        }

        Status = ResourceStatus.Ready;
    }

    public void Fail()
    {
        Status = ResourceStatus.Failed;
    }

    public void Retry()
    {
        if (Status != ResourceStatus.Failed)
        {
            throw new InvalidOperationException("Only a failed caption artifact can be retried.");
        }

        Status = ResourceStatus.Pending;
    }

    public void MarkStale()
    {
        Status = ResourceStatus.Stale;
    }
}
