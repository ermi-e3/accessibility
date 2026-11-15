using System.ComponentModel.DataAnnotations;

namespace Accessibility.Api.Contracts.V1;

public sealed class CreateManualTranscriptRequest
{
    [Required, MaxLength(35)]
    public string Language { get; init; } = string.Empty;

    [Required, MinLength(1), MaxLength(10_000)]
    public IReadOnlyList<TranscriptSegmentRequest> Segments { get; init; } = [];
}

public sealed class TranscriptSegmentRequest
{
    [Range(0, int.MaxValue)]
    public int Sequence { get; init; }

    [Range(0, long.MaxValue)]
    public long StartMilliseconds { get; init; }

    [Range(1, long.MaxValue)]
    public long EndMilliseconds { get; init; }

    [Required, MaxLength(4_000)]
    public string Text { get; init; } = string.Empty;

    [MaxLength(100)]
    public string? SpeakerLabel { get; init; }
}
