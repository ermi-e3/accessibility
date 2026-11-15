using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Processing;

public sealed class FfmpegMediaProcessingProvider(IOptions<MediaProcessingOptions> options)
    : IMediaProcessingProvider
{
    private readonly MediaProcessingOptions _options = options.Value;

    public async Task<PreparedMediaFiles> PrepareAsync(
        string sourceFilePath,
        string outputDirectory,
        bool generatePoster,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var speechPath = Path.Combine(outputDirectory, "speech.wav");
        var offlinePath = Path.Combine(outputDirectory, "offline.ogg");
        var posterPath = generatePoster ? Path.Combine(outputDirectory, "poster.jpg") : null;
        await RunAsync(
            ["-y", "-i", sourceFilePath, "-vn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", speechPath],
            cancellationToken);
        if (posterPath is not null)
        {
            await RunAsync(
                ["-y", "-ss", "1", "-i", sourceFilePath, "-frames:v", "1", "-vf", "scale=640:-2", posterPath],
                cancellationToken);
        }
        await RunAsync(
            ["-y", "-i", sourceFilePath, "-vn", "-ac", "1", "-c:a", "libopus", "-b:a", "32k", offlinePath],
            cancellationToken);
        if (!File.Exists(speechPath) || !File.Exists(offlinePath) ||
            posterPath is not null && !File.Exists(posterPath))
        {
            throw new FileContentInvalidException("FFmpeg did not create the expected processing outputs.");
        }

        return new PreparedMediaFiles(speechPath, offlinePath, "audio/ogg", ".ogg", posterPath);
    }

    private async Task RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.FfmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new ProcessorUnavailableException("FFmpeg could not be started.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(_options.TimeoutMinutes));
            var output = ReadBoundedAsync(process.StandardOutput, 64 * 1024, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, 256 * 1024, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, error);
            }
            catch (OperationCanceledException)
            {
                await KillAndWaitAsync(process);
                if (cancellationToken.IsCancellationRequested) throw;
                throw new ProcessorUnavailableException("FFmpeg timed out while preparing media.");
            }
            catch
            {
                await KillAndWaitAsync(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                throw new FileContentInvalidException("FFmpeg could not decode the source media.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new ProcessorUnavailableException("FFmpeg is unavailable. Configure MediaProcessing:FfmpegPath.", exception);
        }
    }

    internal static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<char>.Shared.Rent(4_096);
        try
        {
            var output = new StringBuilder();
            while (true)
            {
                var read = await reader.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0) return output.ToString();
                if (output.Length + read > maximumCharacters)
                {
                    throw new ProcessorUnavailableException("The media processor produced more output than allowed.");
                }

                output.Append(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(rented, clearArray: true);
        }
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static async Task KillAndWaitAsync(Process process)
    {
        TryKill(process);
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }
    }
}
