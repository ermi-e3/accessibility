namespace Accessibility.Application.Abstractions;

public interface IMediaProcessingProvider
{
    Task<PreparedMediaFiles> PrepareAsync(
        string sourceFilePath,
        string outputDirectory,
        bool generatePoster,
        CancellationToken cancellationToken);
}

public interface ITranscriptionProvider
{
    string Name { get; }

    string Version { get; }

    Task<ProviderTranscript> TranscribeAsync(
        string audioFilePath,
        string language,
        CancellationToken cancellationToken);
}

public interface ITranslationProvider
{
    string Name { get; }

    string Version { get; }

    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken);
}

public interface ITranscriptFormatService
{
    IReadOnlyList<ProviderTranscriptSegment> Parse(
        string content,
        TranscriptExchangeFormat format,
        long? mediaDurationMilliseconds);

    string Serialize(
        IReadOnlyList<ProviderTranscriptSegment> segments,
        TranscriptExchangeFormat format);
}

public interface IBackgroundJobScheduler
{
    string Enqueue(Guid processingJobId);

    string Schedule(Guid processingJobId, TimeSpan delay);

    bool Delete(string externalJobId);

    bool IsActive(string externalJobId);
}

public enum TranscriptExchangeFormat
{
    WebVtt,
    Srt,
    Text
}

public sealed record PreparedMediaFiles(
    string SpeechWavPath,
    string OfflineAudioPath,
    string OfflineAudioContentType,
    string OfflineAudioExtension,
    string? PosterPath);

public sealed record ProviderTranscript(
    string Language,
    IReadOnlyList<ProviderTranscriptSegment> Segments);

public sealed record ProviderTranscriptSegment(
    int Sequence,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    string? SpeakerLabel = null);
