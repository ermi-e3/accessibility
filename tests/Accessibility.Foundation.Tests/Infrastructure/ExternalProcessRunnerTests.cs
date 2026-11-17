using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Accessibility.Infrastructure.Documents;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class ExternalProcessRunnerTests
{
    [Fact]
    public async Task BoundedReaderCountsBytesAndHonorsCancellation()
    {
        await using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65)));
        await Assert.ThrowsAnyAsync<IOException>(() =>
            ExternalProcessRunner.ReadBoundedAsync(oversized, 64, CancellationToken.None));

        await using var cancelled = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExternalProcessRunner.ReadBoundedAsync(cancelled, 64, cancellation.Token));
    }

    [Fact]
    public async Task CallerCancellationKillsSafeLocalHelperPromptly()
    {
        var (executable, arguments) = GetDelayCommand();
        var runner = new ExternalProcessRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            CreateRequest(executable, arguments, TimeSpan.FromSeconds(15)),
            cancellation.Token);

        Assert.Equal(ExternalProcessCompletion.Cancelled, result.Completion);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TimeoutKillsSafeLocalHelperPromptly()
    {
        var (executable, arguments) = GetDelayCommand();
        var runner = new ExternalProcessRunner();
        var stopwatch = Stopwatch.StartNew();

        var result = await runner.RunAsync(
            CreateRequest(executable, arguments, TimeSpan.FromMilliseconds(150)),
            CancellationToken.None);

        Assert.Equal(ExternalProcessCompletion.TimedOut, result.Completion);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunningProcessIsStoppedWhenCapturedOutputExceedsByteCap()
    {
        var dotnet = GetDotnetHostPath();
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!
        };
        AddEnvironmentIfPresent(environment, "SystemRoot");
        AddEnvironmentIfPresent(environment, "WINDIR");
        var request = new ExternalProcessRequest(
            dotnet,
            ["--info"],
            Path.GetFullPath(Path.GetTempPath()),
            TimeSpan.FromSeconds(15),
            1,
            16 * 1024,
            environment);

        var result = await new ExternalProcessRunner().RunAsync(request, CancellationToken.None);

        Assert.Equal(ExternalProcessCompletion.OutputLimitExceeded, result.Completion);
        Assert.Equal(ExternalProcessStream.StandardOutput, result.LimitedStream);
    }

    [Fact]
    public async Task RejectsRelativeExecutableAndWorkspacePaths()
    {
        var runner = new ExternalProcessRunner();
        var request = new ExternalProcessRequest(
            "relative-tool",
            [],
            ".",
            TimeSpan.FromSeconds(1),
            64,
            64,
            new Dictionary<string, string>());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            runner.RunAsync(request, CancellationToken.None));
    }

    private static ExternalProcessRequest CreateRequest(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var environment = new Dictionary<string, string>();
        AddEnvironmentIfPresent(environment, "SystemRoot");
        AddEnvironmentIfPresent(environment, "WINDIR");
        return new ExternalProcessRequest(
            executable,
            arguments,
            Path.GetFullPath(Path.GetTempPath()),
            timeout,
            16 * 1024,
            16 * 1024,
            environment);
    }

    private static (string Executable, IReadOnlyList<string> Arguments) GetDelayCommand()
    {
        if (OperatingSystem.IsWindows())
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return (Path.Combine(system, "ping.exe"), ["-n", "30", "127.0.0.1"]);
        }

        var sleep = File.Exists("/bin/sleep") ? "/bin/sleep" : "/usr/bin/sleep";
        return (sleep, ["30"]);
    }

    private static string GetDotnetHostPath()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured) && File.Exists(configured))
        {
            return configured;
        }

        var runtime = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
        var dotnetRoot = runtime.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("The local dotnet host could not be located.");
        var host = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(host)
            ? host
            : throw new InvalidOperationException("The local dotnet host could not be located.");
    }

    private static void AddEnvironmentIfPresent(IDictionary<string, string> environment, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            environment[name] = value;
        }
    }
}
