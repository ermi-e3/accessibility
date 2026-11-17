using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Accessibility.Infrastructure.Documents;

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken);
}

public sealed record ExternalProcessRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumStandardOutputBytes,
    int MaximumStandardErrorBytes,
    IReadOnlyDictionary<string, string> Environment);

public enum ExternalProcessCompletion
{
    Exited,
    TimedOut,
    Cancelled,
    OutputLimitExceeded,
    StartFailed,
    CaptureFailed
}

public enum ExternalProcessStream
{
    StandardOutput,
    StandardError
}

public sealed record ExternalProcessResult(
    ExternalProcessCompletion Completion,
    int? ExitCode = null,
    string StandardOutput = "",
    string StandardError = "",
    ExternalProcessStream? LimitedStream = null,
    string? FailureMessage = null);

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<ExternalProcessResult> RunAsync(
        ExternalProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return new ExternalProcessResult(ExternalProcessCompletion.Cancelled);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(request.ExecutablePath),
            WorkingDirectory = Path.GetFullPath(request.WorkingDirectory),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8
        };
        startInfo.Environment.Clear();
        foreach (var pair in request.Environment)
        {
            startInfo.Environment.Add(pair.Key, pair.Value);
        }
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ExternalProcessResult(
                    ExternalProcessCompletion.StartFailed,
                    FailureMessage: "The external process did not start.");
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException)
        {
            return new ExternalProcessResult(
                ExternalProcessCompletion.StartFailed,
                FailureMessage: exception.Message);
        }

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var outputLimit = new CancellationTokenSource();
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token,
            outputLimit.Token);
        var limitSignal = new TaskCompletionSource<ExternalProcessStream>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var stdoutTask = ReadBoundedAsync(
            process.StandardOutput.BaseStream,
            request.MaximumStandardOutputBytes,
            ExternalProcessStream.StandardOutput,
            limitSignal,
            outputLimit,
            execution.Token);
        var stderrTask = ReadBoundedAsync(
            process.StandardError.BaseStream,
            request.MaximumStandardErrorBytes,
            ExternalProcessStream.StandardError,
            limitSignal,
            outputLimit,
            execution.Token);
        var exitTask = process.WaitForExitAsync(execution.Token);

        try
        {
            await Task.WhenAll(exitTask, stdoutTask, stderrTask);
        }
        catch
        {
            await KillAndWaitAsync(process);

            if (cancellationToken.IsCancellationRequested)
            {
                return new ExternalProcessResult(ExternalProcessCompletion.Cancelled);
            }

            if (limitSignal.Task.IsCompletedSuccessfully)
            {
                return new ExternalProcessResult(
                    ExternalProcessCompletion.OutputLimitExceeded,
                    LimitedStream: await limitSignal.Task);
            }

            if (timeout.IsCancellationRequested)
            {
                return new ExternalProcessResult(ExternalProcessCompletion.TimedOut);
            }

            return new ExternalProcessResult(
                ExternalProcessCompletion.CaptureFailed,
                FailureMessage: "The external process output could not be captured.");
        }

        return new ExternalProcessResult(
            ExternalProcessCompletion.Exited,
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    internal static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var outputLimit = new CancellationTokenSource();
        return await ReadBoundedAsync(
            stream,
            maximumBytes,
            ExternalProcessStream.StandardOutput,
            new TaskCompletionSource<ExternalProcessStream>(
                TaskCreationOptions.RunContinuationsAsynchronously),
            outputLimit,
            cancellationToken);
    }

    private static async Task<string> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        ExternalProcessStream streamName,
        TaskCompletionSource<ExternalProcessStream> limitSignal,
        CancellationTokenSource outputLimit,
        CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            using var content = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
            while (true)
            {
                var read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0)
                {
                    return Utf8.GetString(content.GetBuffer(), 0, checked((int)content.Length));
                }

                if (content.Length + read > maximumBytes)
                {
                    limitSignal.TrySetResult(streamName);
                    await outputLimit.CancelAsync();
                    throw new ExternalProcessOutputLimitException(streamName, maximumBytes);
                }

                content.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void Validate(ExternalProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExecutablePath) ||
            !Path.IsPathFullyQualified(request.ExecutablePath))
        {
            throw new ArgumentException("The executable path must be absolute.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) ||
            !Path.IsPathFullyQualified(request.WorkingDirectory) ||
            !Directory.Exists(request.WorkingDirectory))
        {
            throw new ArgumentException("The working directory must be an existing absolute path.", nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The timeout must be between zero and 24 hours.");
        }

        if (request.MaximumStandardOutputBytes is <= 0 or > 64 * 1024 * 1024 ||
            request.MaximumStandardErrorBytes is <= 0 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Output limits must be positive and no larger than 64 MiB.");
        }

        if (request.Arguments is null ||
            request.Arguments.Count > 256 ||
            request.Arguments.Any(argument => argument is null || argument.Length > 32_768 || argument.Contains('\0')))
        {
            throw new ArgumentException("The argument list is invalid or too large.", nameof(request));
        }

        if (request.Environment is null ||
            request.Environment.Count > 64 || request.Environment.Any(pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                pair.Key.Length > 256 ||
                pair.Key.Contains('=') ||
                pair.Key.Contains('\0') ||
                pair.Value is null ||
                pair.Value.Length > 32_768 ||
                pair.Value.Contains('\0')))
        {
            throw new ArgumentException("The controlled environment is invalid or too large.", nameof(request));
        }
    }

    private static async Task KillAndWaitAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The process either exited concurrently or the platform already reaped it.
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // The runner remains bounded even when the platform cannot confirm process exit.
        }
    }

    private sealed class ExternalProcessOutputLimitException(
        ExternalProcessStream stream,
        int maximumBytes)
        : IOException($"{stream} exceeded the {maximumBytes}-byte limit.");
}
