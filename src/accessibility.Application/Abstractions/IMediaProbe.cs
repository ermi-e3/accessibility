namespace Accessibility.Application.Abstractions;

public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken);
}

public sealed record MediaProbeResult(
    long DurationMilliseconds,
    bool HasVideo,
    bool HasAudio,
    int? Width,
    int? Height,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    string Container);
