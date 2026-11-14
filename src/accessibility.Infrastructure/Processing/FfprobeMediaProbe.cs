using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Processing;

public sealed class FfprobeMediaProbe(IOptions<FfprobeOptions> options) : IMediaProbe
{
    private const int MaximumOutputCharacters = 1_048_576;
    private const int MaximumErrorCharacters = 65_536;
    private readonly FfprobeOptions _options = options.Value;

    public async Task<MediaProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            throw new FileContentInvalidException("The quarantined media file is unavailable.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add(
            "format=format_name,duration:stream=codec_type,codec_name,width,height,duration:stream_disposition=attached_pic");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(filePath);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new ProcessorUnavailableException("ffprobe could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            var outputTask = ReadBoundedAsync(
                process.StandardOutput,
                MaximumOutputCharacters,
                timeout.Token);
            var errorTask = ReadBoundedAsync(
                process.StandardError,
                MaximumErrorCharacters,
                timeout.Token);

            try
            {
                await Task.WhenAll(
                    process.WaitForExitAsync(timeout.Token),
                    outputTask,
                    errorTask);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new ProcessorUnavailableException("ffprobe timed out while inspecting the upload.");
            }
            catch
            {
                TryKill(process);
                throw;
            }

            var output = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0 || output.Length == 0)
            {
                throw new FileContentInvalidException("ffprobe could not read valid media metadata.");
            }

            return Parse(output);
        }
        catch (Win32Exception exception)
        {
            throw new ProcessorUnavailableException("ffprobe is unavailable. Configure MediaProbe:ExecutablePath.", exception);
        }
    }

    internal static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<char>.Shared.Rent(4_096);
        try
        {
            var output = new StringBuilder(Math.Min(maximumCharacters, 16_384));
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (read == 0)
                {
                    return output.ToString();
                }

                if (output.Length + read > maximumCharacters)
                {
                    throw new ProcessorUnavailableException("ffprobe produced more output than allowed.");
                }

                output.Append(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process exited between the state check and kill request.
        }
    }

    internal static MediaProbeResult Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var streams = root.TryGetProperty("streams", out var streamArray)
                ? streamArray.EnumerateArray().ToArray()
                : [];
            var videos = streams
                .Where(stream => ReadString(stream, "codec_type") == "video" && !IsAttachedPicture(stream))
                .ToArray();
            var audios = streams.Where(stream => ReadString(stream, "codec_type") == "audio").ToArray();
            var hasVideo = videos.Length > 0;
            var hasAudio = audios.Length > 0;
            var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
            var duration = ReadDouble(format, "duration") ??
                streams.Select(stream => ReadDouble(stream, "duration")).Where(value => value.HasValue).Max();
            if (duration is null or <= 0 || (!hasVideo && !hasAudio))
            {
                throw new FileContentInvalidException("The media duration or stream metadata is invalid.");
            }

            return new MediaProbeResult(
                checked((long)Math.Round(duration.Value * 1_000, MidpointRounding.AwayFromZero)),
                hasVideo,
                hasAudio,
                hasVideo ? ReadInt(videos[0], "width") : null,
                hasVideo ? ReadInt(videos[0], "height") : null,
                videos.Select(stream => ReadString(stream, "codec_name") ?? "unknown").Distinct().ToArray(),
                audios.Select(stream => ReadString(stream, "codec_name") ?? "unknown").Distinct().ToArray(),
                ReadString(format, "format_name") ?? "unknown");
        }
        catch (JsonException exception)
        {
            throw new FileContentInvalidException($"ffprobe returned malformed JSON: {exception.Message}");
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.TryGetInt32(out var number) ? number : null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        var value = ReadString(element, name);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;
    }

    private static bool IsAttachedPicture(JsonElement stream)
    {
        return stream.TryGetProperty("disposition", out var disposition) &&
               disposition.TryGetProperty("attached_pic", out var attached) &&
               attached.TryGetInt32(out var value) &&
               value == 1;
    }
}
