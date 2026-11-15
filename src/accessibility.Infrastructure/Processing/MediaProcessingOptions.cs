namespace Accessibility.Infrastructure.Processing;

public sealed class MediaProcessingOptions
{
    public const string SectionName = "MediaProcessing";

    public string FfmpegPath { get; init; } = "ffmpeg";

    public int TimeoutMinutes { get; init; } = 20;
}
