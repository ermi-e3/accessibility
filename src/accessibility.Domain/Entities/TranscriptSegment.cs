using Accessibility.Domain.Common;

namespace Accessibility.Domain.Entities;

public sealed class TranscriptSegment : AuditableEntity
{
    private TranscriptSegment()
    {
    }

    public TranscriptSegment(
        Guid transcriptId,
        int sequence,
        long startMilliseconds,
        long endMilliseconds,
        string text,
        string? speakerLabel = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(startMilliseconds);
        if (endMilliseconds <= startMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endMilliseconds), "End time must be after start time.");
        }

        TranscriptId = transcriptId != Guid.Empty ? transcriptId : throw new ArgumentException("Transcript is required.", nameof(transcriptId));
        Sequence = sequence;
        StartMilliseconds = startMilliseconds;
        EndMilliseconds = endMilliseconds;
        Text = DomainValidation.RequiredText(text, nameof(text), 4_000);
        SpeakerLabel = DomainValidation.OptionalText(speakerLabel, nameof(speakerLabel), 100);
    }

    public Guid TranscriptId { get; private set; }

    public int Sequence { get; private set; }

    public long StartMilliseconds { get; private set; }

    public long EndMilliseconds { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public string? SpeakerLabel { get; private set; }

    public void Update(long startMilliseconds, long endMilliseconds, string text, string? speakerLabel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startMilliseconds);
        if (endMilliseconds <= startMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(endMilliseconds), "End time must be after start time.");
        }

        StartMilliseconds = startMilliseconds;
        EndMilliseconds = endMilliseconds;
        Text = DomainValidation.RequiredText(text, nameof(text), 4_000);
        SpeakerLabel = DomainValidation.OptionalText(speakerLabel, nameof(speakerLabel), 100);
    }
}
