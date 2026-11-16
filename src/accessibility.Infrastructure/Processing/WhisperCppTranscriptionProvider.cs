using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Processing;

public sealed class WhisperCppTranscriptionProvider(IOptions<WhisperOptions> options) : ITranscriptionProvider
{
    private readonly WhisperOptions _options = options.Value;

    public string Name => "whisper.cpp";

    public string Version => _options.Version;

    public async Task<ProviderTranscript> TranscribeAsync(
        string audioFilePath,
        string language,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ExecutablePath) || !File.Exists(_options.ModelPath))
        {
            throw new ProcessorUnavailableException("whisper.cpp executable or multilingual model is unavailable.");
        }

        var prefix = Path.Combine(Path.GetDirectoryName(audioFilePath)!, "whisper-result");
        var outputPath = prefix + ".json";
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-m", _options.ModelPath, "-f", audioFilePath, "-l", language,
                     "-t", _options.Threads.ToString(CultureInfo.InvariantCulture), "-oj", "-of", prefix
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new ProcessorUnavailableException("whisper.cpp could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(_options.TimeoutMinutes));
            var stdout = FfmpegMediaProcessingProvider.ReadBoundedAsync(process.StandardOutput, 128 * 1024, timeout.Token);
            var stderr = FfmpegMediaProcessingProvider.ReadBoundedAsync(process.StandardError, 256 * 1024, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                await FfmpegMediaProcessingProvider.KillAndWaitAsync(process);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new ProcessorUnavailableException("whisper.cpp timed out while transcribing media.");
            }
            catch
            {
                await FfmpegMediaProcessingProvider.KillAndWaitAsync(process);
                throw;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new FileContentInvalidException("whisper.cpp could not transcribe the extracted audio.");
            }

            return Parse(await File.ReadAllTextAsync(outputPath, cancellationToken), language);
        }
        catch (Win32Exception exception)
        {
            throw new ProcessorUnavailableException("whisper.cpp could not be executed.", exception);
        }
    }

    internal static ProviderTranscript Parse(string json, string language)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("transcription", out var transcription))
            {
                throw new FileContentInvalidException("whisper.cpp returned no transcription array.");
            }

            var segments = new List<ProviderTranscriptSegment>();
            foreach (var item in transcription.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var textValue) ? textValue.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(text)) continue;
                long start;
                long end;
                if (item.TryGetProperty("offsets", out var offsets) &&
                    offsets.TryGetProperty("from", out var from) && offsets.TryGetProperty("to", out var to) &&
                    from.TryGetInt64(out start) && to.TryGetInt64(out end))
                {
                }
                else
                {
                    throw new FileContentInvalidException("whisper.cpp returned a segment without millisecond offsets.");
                }

                if (start < 0 || end <= start)
                {
                    throw new FileContentInvalidException("whisper.cpp returned invalid segment timing.");
                }
                segments.Add(new ProviderTranscriptSegment(segments.Count, start, end, text));
            }

            if (segments.Count == 0)
            {
                throw new FileContentInvalidException("whisper.cpp produced no usable transcript segments.");
            }

            return new ProviderTranscript(language.ToLowerInvariant(), segments);
        }
        catch (JsonException exception)
        {
            throw new FileContentInvalidException($"whisper.cpp returned malformed JSON: {exception.Message}");
        }
    }
}
