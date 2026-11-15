using System.ComponentModel.DataAnnotations;

namespace Accessibility.Api.Contracts.V1;

public sealed class StartProcessingRequest
{
    public Guid SourceAssetId { get; init; }

    [Required, MaxLength(2)]
    public IReadOnlyList<string> TargetLanguages { get; init; } = ["en", "am"];
}

public sealed class UpdateTranscriptRequest
{
    [Required, MinLength(1), MaxLength(10_000)]
    public IReadOnlyList<TranscriptSegmentRequest> Segments { get; init; } = [];
}
