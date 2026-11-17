using System.Security.Cryptography;
using System.Text;
using Accessibility.Application.Features.Documents;
using Accessibility.Infrastructure.Documents;
using Microsoft.Extensions.Options;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class DocumentPdfToolchainTests
{
    [Fact]
    public async Task BuildsPinnedNoNetworkCommandsAndPublishesOnlyPassingArtifacts()
    {
        using var fixture = new ToolchainFixture();
        var runner = fixture.CreateRunner();
        var toolchain = fixture.CreateToolchain(runner);

        var result = await toolchain.GenerateAndValidateAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.Equal(DocumentPdfOutcome.PassedProfile, result.Outcome);
        Assert.True(result.PassedRequiredProfile);
        Assert.Equal("veraPDF PDF/UA-1 profile passed", result.ValidationLabel);
        Assert.Equal(fixture.Request.PdfOutputPath, result.PdfPath);
        Assert.Equal(fixture.Request.ValidationReportPath, result.ValidationReportPath);
        Assert.True(File.Exists(fixture.Request.PdfOutputPath));
        Assert.True(File.Exists(fixture.Request.ValidationReportPath));
        Assert.Equal(5, runner.Requests.Count);

        var render = Assert.Single(runner.Requests, request =>
            request.ExecutablePath == fixture.Options.WeasyPrintExecutablePath &&
            !request.Arguments.Contains("--version"));
        AssertArgumentPair(render.Arguments, "--pdf-variant", "pdf/ua-1");
        AssertArgumentPair(render.Arguments, "--allowed-protocols", "file,data");
        Assert.Contains("--pdf-tags", render.Arguments);
        Assert.Contains("--full-fonts", render.Arguments);
        Assert.Contains("--no-http-redirects", render.Arguments);
        Assert.Contains("--fail-on-http-errors", render.Arguments);
        Assert.EndsWith("source.xhtml", render.Arguments[^2], StringComparison.Ordinal);
        Assert.EndsWith("candidate.pdf", render.Arguments[^1], StringComparison.Ordinal);
        Assert.DoesNotContain(render.Arguments, argument => argument.StartsWith('"'));
        Assert.Equal(render.WorkingDirectory, Path.GetDirectoryName(render.Environment["HOME"]));
        Assert.Contains("FONTCONFIG_FILE", render.Environment.Keys);
        Assert.DoesNotContain("COMSPEC", render.Environment.Keys);

        var validation = Assert.Single(runner.Requests, request =>
            request.ExecutablePath == fixture.Options.JavaExecutablePath &&
            request.Arguments.Contains("--flavour"));
        AssertArgumentPair(validation.Arguments, "--format", "json");
        AssertArgumentPair(validation.Arguments, "--flavour", "ua1");
        var jarIndex = validation.Arguments.ToList().IndexOf("-jar");
        Assert.True(jarIndex >= 0 && jarIndex + 1 < validation.Arguments.Count);
        Assert.EndsWith("verapdf-cli-1.30.4.jar", validation.Arguments[jarIndex + 1], StringComparison.Ordinal);
        Assert.StartsWith(validation.WorkingDirectory, validation.Arguments[jarIndex + 1], StringComparison.Ordinal);
        Assert.EndsWith("candidate.pdf", validation.Arguments[^1], StringComparison.Ordinal);
        Assert.Empty(Directory.GetDirectories(fixture.WorkspacePath, ".pdf-toolchain-*"));
    }

    [Fact]
    public async Task NonCompliantUa1ReportProducesTaggedDraftWithReport()
    {
        using var fixture = new ToolchainFixture
        {
            ValidationReport = CreateValidationReport(isCompliant: false),
            ValidationExitCode = 1
        };

        var result = await fixture.CreateToolchain(fixture.CreateRunner()).GenerateAndValidateAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.Equal(DocumentPdfOutcome.TaggedDraft, result.Outcome);
        Assert.False(result.PassedRequiredProfile);
        Assert.Equal("tagged draft", result.ValidationLabel);
        Assert.Equal(2, result.FailedRules);
        Assert.Equal(3, result.FailedChecks);
        Assert.True(File.Exists(fixture.Request.PdfOutputPath));
        Assert.True(File.Exists(fixture.Request.ValidationReportPath));
    }

    [Fact]
    public async Task ValidatorOperationalFailureIsNeverCalledTaggedDraft()
    {
        using var fixture = new ToolchainFixture { ValidationExitCode = 7 };

        var result = await fixture.CreateToolchain(fixture.CreateRunner()).GenerateAndValidateAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.Equal(DocumentPdfOutcome.OperationalFailure, result.Outcome);
        Assert.Equal("unavailable", result.ValidationLabel);
        Assert.False(File.Exists(fixture.Request.PdfOutputPath));
        Assert.False(File.Exists(fixture.Request.ValidationReportPath));
    }

    [Fact]
    public async Task CancellationIsDistinctAndCleansWorkspace()
    {
        using var fixture = new ToolchainFixture();
        var runner = fixture.CreateRunner(request =>
            request.Arguments.Contains("--version")
                ? new ExternalProcessResult(ExternalProcessCompletion.Cancelled)
                : null);

        var result = await fixture.CreateToolchain(runner).GenerateAndValidateAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.Equal(DocumentPdfOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetDirectories(fixture.WorkspacePath, ".pdf-toolchain-*"));
    }

    [Fact]
    public async Task MalformedAndOversizedReportsHaveDistinctOutcomes()
    {
        using var malformed = new ToolchainFixture { ValidationReport = "{}" };
        var malformedResult = await malformed.CreateToolchain(malformed.CreateRunner()).GenerateAndValidateAsync(
            malformed.Request,
            CancellationToken.None);
        Assert.Equal(DocumentPdfOutcome.MalformedValidationReport, malformedResult.Outcome);

        using var oversized = new ToolchainFixture(maximumValidationReportBytes: 128)
        {
            ValidationReport = new string('x', 129)
        };
        var oversizedResult = await oversized.CreateToolchain(oversized.CreateRunner()).GenerateAndValidateAsync(
            oversized.Request,
            CancellationToken.None);
        Assert.Equal(DocumentPdfOutcome.ValidationReportTooLarge, oversizedResult.Outcome);
    }

    [Fact]
    public async Task MalformedAndOversizedPdfsHaveDistinctOutcomes()
    {
        using var malformed = new ToolchainFixture { CandidatePdf = "not-a-pdf"u8.ToArray() };
        var malformedResult = await malformed.CreateToolchain(malformed.CreateRunner()).GenerateAndValidateAsync(
            malformed.Request,
            CancellationToken.None);
        Assert.Equal(DocumentPdfOutcome.MalformedPdfOutput, malformedResult.Outcome);

        using var oversized = new ToolchainFixture(maximumPdfBytes: 16)
        {
            CandidatePdf = "%PDF-1.7\n0123456789\n%%EOF\n"u8.ToArray()
        };
        var oversizedResult = await oversized.CreateToolchain(oversized.CreateRunner()).GenerateAndValidateAsync(
            oversized.Request,
            CancellationToken.None);
        Assert.Equal(DocumentPdfOutcome.PdfOutputTooLarge, oversizedResult.Outcome);
    }

    [Fact]
    public async Task RejectsFetchableXhtmlBeforeStartingTools()
    {
        using var fixture = new ToolchainFixture();
        File.WriteAllText(
            fixture.Request.XhtmlPath,
            """
            <html xmlns="http://www.w3.org/1999/xhtml" lang="en">
              <head><title>Remote</title></head>
              <body><img src="https://example.invalid/tracker.png" alt="" /></body>
            </html>
            """);
        var runner = fixture.CreateRunner();

        var result = await fixture.CreateToolchain(runner).GenerateAndValidateAsync(
            fixture.Request,
            CancellationToken.None);

        Assert.Equal(DocumentPdfOutcome.OperationalFailure, result.Outcome);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public void ParsesOnlyConsistentPinnedUa1Json()
    {
        Assert.Equal(DocumentPdfToolchain.VeraPdfReportOutcome.Valid, DocumentPdfToolchain.ParseValidationReport(
            CreateValidationReport(isCompliant: true),
            out var passing,
            out _));
        Assert.True(passing.IsCompliant);
        Assert.Equal(50, passing.PassedChecks);

        Assert.Equal(DocumentPdfToolchain.VeraPdfReportOutcome.Valid, DocumentPdfToolchain.ParseValidationReport(
            CreateValidationReport(isCompliant: false),
            out var failing,
            out _));
        Assert.False(failing.IsCompliant);
        Assert.Equal(3, failing.FailedChecks);

        var wrongProfile = CreateValidationReport(isCompliant: true)
            .Replace("PDF/UA-1 validation profile", "PDF/A-1b validation profile", StringComparison.Ordinal);
        Assert.Equal(
            DocumentPdfToolchain.VeraPdfReportOutcome.Malformed,
            DocumentPdfToolchain.ParseValidationReport(wrongProfile, out _, out _));

        var operationalBatch = CreateValidationReport(isCompliant: true)
            .Replace("\"failedParsingJobs\": 0", "\"failedParsingJobs\": 1", StringComparison.Ordinal);
        Assert.Equal(
            DocumentPdfToolchain.VeraPdfReportOutcome.OperationalFailure,
            DocumentPdfToolchain.ParseValidationReport(operationalBatch, out _, out _));
    }

    [Fact]
    public void OptionsRequireAbsolutePathsHashesAndExactPins()
    {
        var absolute = Path.GetFullPath(Path.GetTempPath());
        var hash = new string('a', 64);
        var valid = new DocumentProcessingOptions
        {
            WorkspaceRootPath = absolute,
            WeasyPrintExecutablePath = Path.Combine(absolute, "weasyprint"),
            JavaExecutablePath = Path.Combine(absolute, "java"),
            ExpectedJavaMajorVersion = 21,
            VeraPdfJarPath = Path.Combine(absolute, "verapdf.jar"),
            VeraPdfJarSha256 = hash,
            NotoEthiopicFontPath = Path.Combine(absolute, "font.ttf"),
            NotoEthiopicFontSha256 = hash
        };
        valid.Validate();

        var relative = new DocumentProcessingOptions
        {
            WorkspaceRootPath = "relative",
            WeasyPrintExecutablePath = valid.WeasyPrintExecutablePath,
            JavaExecutablePath = valid.JavaExecutablePath,
            ExpectedJavaMajorVersion = 21,
            VeraPdfJarPath = valid.VeraPdfJarPath,
            VeraPdfJarSha256 = hash,
            NotoEthiopicFontPath = valid.NotoEthiopicFontPath,
            NotoEthiopicFontSha256 = hash
        };
        Assert.Throws<ArgumentException>(relative.Validate);

        var wrongVersion = new DocumentProcessingOptions
        {
            WorkspaceRootPath = absolute,
            WeasyPrintExecutablePath = valid.WeasyPrintExecutablePath,
            ExpectedWeasyPrintVersion = "71.0",
            JavaExecutablePath = valid.JavaExecutablePath,
            ExpectedJavaMajorVersion = 21,
            VeraPdfJarPath = valid.VeraPdfJarPath,
            VeraPdfJarSha256 = hash,
            NotoEthiopicFontPath = valid.NotoEthiopicFontPath,
            NotoEthiopicFontSha256 = hash
        };
        Assert.Throws<ArgumentException>(wrongVersion.Validate);

        var invalidHash = new DocumentProcessingOptions
        {
            WorkspaceRootPath = absolute,
            WeasyPrintExecutablePath = valid.WeasyPrintExecutablePath,
            JavaExecutablePath = valid.JavaExecutablePath,
            ExpectedJavaMajorVersion = 21,
            VeraPdfJarPath = valid.VeraPdfJarPath,
            VeraPdfJarSha256 = "not-a-sha256",
            NotoEthiopicFontPath = valid.NotoEthiopicFontPath,
            NotoEthiopicFontSha256 = hash
        };
        Assert.Throws<ArgumentException>(invalidHash.Validate);
    }

    private static void AssertArgumentPair(
        IReadOnlyList<string> arguments,
        string option,
        string expectedValue)
    {
        var index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count);
        Assert.Equal(expectedValue, arguments[index + 1]);
    }

    private static string CreateValidationReport(bool isCompliant)
    {
        var passedRules = isCompliant ? 10 : 8;
        var failedRules = isCompliant ? 0 : 2;
        var passedChecks = isCompliant ? 50 : 47;
        var failedChecks = isCompliant ? 0 : 3;
        var compliant = isCompliant ? 1 : 0;
        var nonCompliant = isCompliant ? 0 : 1;
        return $$"""
            {
              "report": {
                "buildInformation": {
                  "releaseDetails": [
                    { "id": "core", "version": "1.30.4" },
                    { "id": "validation-model", "version": "1.30.4" }
                  ]
                },
                "jobs": [{
                    "validationResult": [{
                      "profileName": "PDF/UA-1 validation profile",
                      "compliant": {{isCompliant.ToString().ToLowerInvariant()}},
                      "jobEndStatus": "normal",
                      "details": {
                        "passedRules": {{passedRules}},
                        "failedRules": {{failedRules}},
                        "passedChecks": {{passedChecks}},
                        "failedChecks": {{failedChecks}}
                      }
                    }]
                  }],
                "batchSummary": {
                  "totalJobs": 1,
                  "failedParsingJobs": 0,
                  "failedEncryptedJobs": 0,
                  "outOfMemory": 0,
                  "veraExceptions": 0,
                  "validationSummary": {
                    "compliantPdfaCount": {{compliant}},
                    "nonCompliantPdfaCount": {{nonCompliant}},
                    "failedJobCount": 0,
                    "totalJobCount": 1
                  }
                }
              }
            }
            """;
    }

    private sealed class ToolchainFixture : IDisposable
    {
        public ToolchainFixture(
            int maximumValidationReportBytes = 1024 * 1024,
            long maximumPdfBytes = 1024 * 1024)
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"access learn pdf {Guid.NewGuid():N}");
            WorkspacePath = Path.Combine(RootPath, "job workspace");
            Directory.CreateDirectory(WorkspacePath);
            var weasyPrintPath = Path.Combine(RootPath, "weasy print.exe");
            var javaPath = Path.Combine(RootPath, "java runtime.exe");
            var veraPdfPath = Path.Combine(RootPath, "vera pdf 1.30.4.jar");
            var fontPath = Path.Combine(RootPath, "Noto Sans Ethiopic.ttf");
            File.WriteAllBytes(weasyPrintPath, "weasyprint"u8.ToArray());
            File.WriteAllBytes(javaPath, "java"u8.ToArray());
            File.WriteAllBytes(veraPdfPath, "verapdf-jar"u8.ToArray());
            File.WriteAllBytes(fontPath, "noto-font"u8.ToArray());

            Options = new DocumentProcessingOptions
            {
                WorkspaceRootPath = RootPath,
                WeasyPrintExecutablePath = weasyPrintPath,
                JavaExecutablePath = javaPath,
                ExpectedJavaMajorVersion = 21,
                VeraPdfJarPath = veraPdfPath,
                VeraPdfJarSha256 = HashFile(veraPdfPath),
                NotoEthiopicFontPath = fontPath,
                NotoEthiopicFontSha256 = HashFile(fontPath),
                MaximumValidationReportBytes = maximumValidationReportBytes,
                MaximumPdfBytes = maximumPdfBytes
            };
            var xhtmlPath = Path.Combine(WorkspacePath, "reviewed document.xhtml");
            File.WriteAllText(xhtmlPath, """
                <html xmlns="http://www.w3.org/1999/xhtml" lang="am">
                  <head><title>Reviewed document</title><style>p { color: #111; }</style></head>
                  <body><main><h1>ሰላም</h1><p>Reviewed text.</p></main></body>
                </html>
                """);
            Request = new DocumentPdfRequest(
                WorkspacePath,
                xhtmlPath,
                Path.Combine(WorkspacePath, "result document.pdf"),
                Path.Combine(WorkspacePath, "validation report.json"));
        }

        public string RootPath { get; }

        public string WorkspacePath { get; }

        public DocumentProcessingOptions Options { get; }

        public DocumentPdfRequest Request { get; }

        public string ValidationReport { get; set; } = CreateValidationReport(isCompliant: true);

        public int ValidationExitCode { get; set; }

        public byte[] CandidatePdf { get; set; } = "%PDF-1.7\n1 0 obj\nendobj\n%%EOF\n"u8.ToArray();

        public DocumentPdfToolchain CreateToolchain(IExternalProcessRunner runner)
        {
            return new DocumentPdfToolchain(Microsoft.Extensions.Options.Options.Create(Options), runner);
        }

        public FakeProcessRunner CreateRunner(
            Func<ExternalProcessRequest, ExternalProcessResult?>? overrideResult = null)
        {
            return new FakeProcessRunner(request =>
            {
                var overridden = overrideResult?.Invoke(request);
                if (overridden is not null)
                {
                    return overridden;
                }

                if (request.ExecutablePath == Options.WeasyPrintExecutablePath &&
                    request.Arguments.SequenceEqual(["--version"]))
                {
                    return Exited(0, "WeasyPrint version 70.0\n");
                }

                if (request.ExecutablePath == Options.JavaExecutablePath &&
                    request.Arguments.SequenceEqual(["-version"]))
                {
                    return Exited(0, standardError: "openjdk version \"21.0.5\" 2026-10-15\n");
                }

                if (request.ExecutablePath == Options.JavaExecutablePath &&
                    request.Arguments.Contains("--version"))
                {
                    return Exited(0, "veraPDF 1.30.4\n");
                }

                if (request.ExecutablePath == Options.WeasyPrintExecutablePath)
                {
                    File.WriteAllBytes(request.Arguments[^1], CandidatePdf);
                    return Exited(0);
                }

                return Exited(ValidationExitCode, ValidationReport);
            });
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }

        private static string HashFile(string path)
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
    }

    private sealed class FakeProcessRunner(
        Func<ExternalProcessRequest, ExternalProcessResult> execute) : IExternalProcessRunner
    {
        public List<ExternalProcessRequest> Requests { get; } = [];

        public Task<ExternalProcessResult> RunAsync(
            ExternalProcessRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(execute(request));
        }
    }

    private static ExternalProcessResult Exited(
        int exitCode,
        string standardOutput = "",
        string standardError = "")
    {
        return new ExternalProcessResult(
            ExternalProcessCompletion.Exited,
            exitCode,
            standardOutput,
            standardError);
    }
}
