namespace Accessibility.Infrastructure.Processing;

public sealed class WhisperOptions
{
    public const string SectionName = "Whisper";

    public string ExecutablePath { get; init; } = string.Empty;

    public string ModelPath { get; init; } = string.Empty;

    public string Version { get; init; } = "whisper.cpp-v1.9.5-b5454";

    public int Threads { get; init; } = 4;

    public int TimeoutMinutes { get; init; } = 30;
}
