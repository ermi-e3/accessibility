namespace Accessibility.Infrastructure.Processing;

public sealed class FfprobeOptions
{
    public const string SectionName = "MediaProbe";

    public string ExecutablePath { get; init; } = "ffprobe";

    public int TimeoutSeconds { get; init; } = 30;
}
