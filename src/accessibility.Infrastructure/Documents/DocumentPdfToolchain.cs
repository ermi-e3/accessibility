using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Accessibility.Application.Features.Documents;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Documents;

public sealed class DocumentPdfToolchain : IDocumentPdfToolchain
{
    private const string PdfUaVariant = "pdf/ua-1";
    private const string PdfUaProfileName = "PDF/UA-1 validation profile";
    private const long MaximumPinnedJarBytes = 512L * 1024 * 1024;
    private const long MaximumPinnedFontBytes = 64L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly Regex WeasyPrintVersionPattern = new(
        @"^\s*WeasyPrint\s+version\s+(?<version>\d+(?:\.\d+)*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex JavaVersionPattern = new(
        @"\b(?:openjdk|java)\s+version\s+""(?<version>\d+(?:\.\d+)*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex VeraPdfVersionPattern = new(
        @"^\s*veraPDF\s+(?<version>\d+(?:\.\d+)*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex CssCommentPattern = new(
        @"/\*.*?\*/",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex CssUrlPattern = new(
        @"\burl\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly DocumentProcessingOptions _options;
    private readonly IExternalProcessRunner _processRunner;

    public DocumentPdfToolchain(IOptions<DocumentProcessingOptions> options)
        : this(options, new ExternalProcessRunner())
    {
    }

    public DocumentPdfToolchain(
        IOptions<DocumentProcessingOptions> options,
        IExternalProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(processRunner);
        _options = options.Value;
        _options.Validate();
        _processRunner = processRunner;
    }

    public string RendererName => "WeasyPrint";

    public string RequiredRendererVersion => _options.ExpectedWeasyPrintVersion;

    public string ValidatorName => "veraPDF CLI";

    public string RequiredValidatorVersion => _options.ExpectedVeraPdfVersion;

    public string RequiredValidationProfile => _options.VeraPdfProfile;

    public async Task<DocumentPdfResult> GenerateAndValidateAsync(
        DocumentPdfRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var paths = ValidateRequest(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(DocumentPdfOutcome.Cancelled, "PDF generation was cancelled.");
        }

        string? operationPath = null;
        try
        {
            operationPath = Path.Combine(paths.WorkspacePath, $".pdf-toolchain-{Guid.NewGuid():N}");
            var homePath = Path.Combine(operationPath, "home");
            var temporaryPath = Path.Combine(operationPath, "temp");
            var cachePath = Path.Combine(operationPath, "cache");
            var fontPath = Path.Combine(operationPath, "fonts");
            var fontCachePath = Path.Combine(operationPath, "font-cache");
            Directory.CreateDirectory(homePath);
            Directory.CreateDirectory(temporaryPath);
            Directory.CreateDirectory(cachePath);
            Directory.CreateDirectory(fontPath);
            Directory.CreateDirectory(fontCachePath);

            var xhtml = await ReadBoundedFileAsync(
                paths.XhtmlPath,
                _options.MaximumXhtmlBytes,
                cancellationToken);
            if (!TryValidateSelfContainedXhtml(xhtml, out var xhtmlFailure))
            {
                return Failure(DocumentPdfOutcome.OperationalFailure, xhtmlFailure);
            }

            var stagedXhtmlPath = Path.Combine(operationPath, "source.xhtml");
            await File.WriteAllBytesAsync(stagedXhtmlPath, xhtml, cancellationToken);

            if (!File.Exists(_options.WeasyPrintExecutablePath) ||
                !File.Exists(_options.JavaExecutablePath) ||
                !File.Exists(_options.VeraPdfJarPath) ||
                !File.Exists(_options.NotoEthiopicFontPath))
            {
                return Failure(
                    DocumentPdfOutcome.OperationalFailure,
                    "A configured PDF tool or pinned resource is unavailable.");
            }

            var stagedJarPath = Path.Combine(operationPath, "verapdf-cli-1.30.4.jar");
            var jarHash = await CopyAndHashAsync(
                _options.VeraPdfJarPath,
                stagedJarPath,
                MaximumPinnedJarBytes,
                cancellationToken);
            if (!HashesMatch(jarHash, _options.VeraPdfJarSha256))
            {
                return Failure(
                    DocumentPdfOutcome.OperationalFailure,
                    "The veraPDF CLI JAR does not match its configured SHA-256.");
            }

            var fontExtension = Path.GetExtension(_options.NotoEthiopicFontPath).ToLowerInvariant();
            var stagedFontPath = Path.Combine(fontPath, $"NotoSansEthiopic{fontExtension}");
            var fontHash = await CopyAndHashAsync(
                _options.NotoEthiopicFontPath,
                stagedFontPath,
                MaximumPinnedFontBytes,
                cancellationToken);
            if (!HashesMatch(fontHash, _options.NotoEthiopicFontSha256))
            {
                return Failure(
                    DocumentPdfOutcome.OperationalFailure,
                    "The Noto Ethiopic font does not match its configured SHA-256.");
            }

            var fontConfigPath = Path.Combine(operationPath, "fonts.conf");
            await WriteFontConfigAsync(fontConfigPath, fontPath, fontCachePath, cancellationToken);
            var stylesheetPath = Path.Combine(operationPath, "pinned-font.css");
            await WriteFontStylesheetAsync(
                stylesheetPath,
                stagedFontPath,
                fontExtension == ".otf" ? "opentype" : "truetype",
                cancellationToken);
            var environment = BuildEnvironment(
                operationPath,
                homePath,
                temporaryPath,
                cachePath,
                fontConfigPath);

            var versionFailure = await VerifyToolVersionsAsync(
                operationPath,
                environment,
                stagedJarPath,
                cancellationToken);
            if (versionFailure is not null)
            {
                return versionFailure;
            }

            var candidatePdfPath = Path.Combine(operationPath, "candidate.pdf");
            var renderResult = await _processRunner.RunAsync(
                CreateProcessRequest(
                    _options.WeasyPrintExecutablePath,
                    [
                        "--encoding", "utf-8",
                        "--media-type", "print",
                        "--pdf-variant", PdfUaVariant,
                        "--pdf-tags",
                        "--full-fonts",
                        "--custom-metadata",
                        "--allowed-protocols", "file,data",
                        "--no-http-redirects",
                        "--fail-on-http-errors",
                        "--timeout", "1",
                        "--stylesheet", stylesheetPath,
                        "--cache-folder", cachePath,
                        stagedXhtmlPath,
                        candidatePdfPath
                    ],
                    operationPath,
                    TimeSpan.FromSeconds(_options.GenerationTimeoutSeconds),
                    _options.MaximumStandardOutputBytes,
                    _options.MaximumStandardErrorBytes,
                    environment),
                cancellationToken);
            var renderFailure = ClassifyRequiredSuccess(renderResult, "WeasyPrint");
            if (renderFailure is not null)
            {
                return renderFailure;
            }

            var pdfCheck = await InspectPdfAsync(candidatePdfPath, cancellationToken);
            if (pdfCheck != PdfCheck.Valid)
            {
                return pdfCheck == PdfCheck.TooLarge
                    ? Failure(DocumentPdfOutcome.PdfOutputTooLarge, "WeasyPrint produced a PDF larger than allowed.")
                    : Failure(DocumentPdfOutcome.MalformedPdfOutput, "WeasyPrint did not produce a complete PDF file.");
            }

            var validationResult = await _processRunner.RunAsync(
                CreateProcessRequest(
                    _options.JavaExecutablePath,
                    [
                        "-Djava.awt.headless=true",
                        $"-Duser.home={homePath}",
                        $"-Djava.io.tmpdir={temporaryPath}",
                        "-Dfile.encoding=UTF-8",
                        "-jar", stagedJarPath,
                        "--format", "json",
                        "--flavour", _options.VeraPdfProfile,
                        "--maxfailuresdisplayed", "20",
                        "--loglevel", "0",
                        candidatePdfPath
                    ],
                    operationPath,
                    TimeSpan.FromSeconds(_options.ValidationTimeoutSeconds),
                    _options.MaximumValidationReportBytes,
                    _options.MaximumStandardErrorBytes,
                    environment),
                cancellationToken);

            var validationProcessFailure = ClassifyValidationProcess(validationResult);
            if (validationProcessFailure is not null)
            {
                return validationProcessFailure;
            }

            if (Encoding.UTF8.GetByteCount(validationResult.StandardOutput) >
                _options.MaximumValidationReportBytes)
            {
                return Failure(
                    DocumentPdfOutcome.ValidationReportTooLarge,
                    "veraPDF produced a validation report larger than allowed.");
            }

            var reportOutcome = ParseValidationReport(
                validationResult.StandardOutput,
                out var validation,
                out var reportFailure);
            if (reportOutcome != VeraPdfReportOutcome.Valid)
            {
                return Failure(
                    reportOutcome == VeraPdfReportOutcome.OperationalFailure
                        ? DocumentPdfOutcome.OperationalFailure
                        : DocumentPdfOutcome.MalformedValidationReport,
                    reportFailure);
            }

            var expectedExitCode = validation.IsCompliant ? 0 : 1;
            if (validationResult.ExitCode != expectedExitCode)
            {
                return Failure(
                    DocumentPdfOutcome.OperationalFailure,
                    "veraPDF returned an exit code inconsistent with its validation report.");
            }

            var stagedReportPath = Path.Combine(operationPath, "validation-report.json");
            await File.WriteAllTextAsync(
                stagedReportPath,
                validationResult.StandardOutput,
                Utf8WithoutBom,
                cancellationToken);
            PublishArtifacts(
                candidatePdfPath,
                stagedReportPath,
                paths.PdfOutputPath,
                paths.ValidationReportPath);

            return new DocumentPdfResult(
                validation.IsCompliant
                    ? DocumentPdfOutcome.PassedProfile
                    : DocumentPdfOutcome.TaggedDraft,
                paths.PdfOutputPath,
                paths.ValidationReportPath,
                RequiredRendererVersion,
                RequiredValidatorVersion,
                RequiredValidationProfile,
                validation.PassedRules,
                validation.FailedRules,
                validation.PassedChecks,
                validation.FailedChecks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(DocumentPdfOutcome.Cancelled, "PDF generation was cancelled.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or XmlException)
        {
            return Failure(
                DocumentPdfOutcome.OperationalFailure,
                "The PDF toolchain could not complete its local workspace operation.");
        }
        finally
        {
            TryDeleteOperationDirectory(operationPath);
        }
    }

    internal static VeraPdfReportOutcome ParseValidationReport(
        string json,
        out VeraPdfValidationSummary summary,
        out string failure)
    {
        summary = default;
        failure = "veraPDF returned a malformed validation report.";
        if (string.IsNullOrWhiteSpace(json))
        {
            return VeraPdfReportOutcome.Malformed;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
            if (FindNamedObjects(document.RootElement, "taskException").Count != 0)
            {
                failure = "veraPDF reported an operational validation task failure.";
                return VeraPdfReportOutcome.OperationalFailure;
            }

            var reports = FindNamedObjects(document.RootElement, "validationResult");
            if (reports.Count == 0)
            {
                reports = FindNamedObjects(document.RootElement, "validationReport");
            }
            if (reports.Count != 1)
            {
                failure = "veraPDF must return exactly one validation report.";
                return VeraPdfReportOutcome.Malformed;
            }

            var report = reports[0];
            if (!TryReadString(report, "profileName", out var profileName) ||
                !string.Equals(profileName, PdfUaProfileName, StringComparison.OrdinalIgnoreCase))
            {
                failure = "veraPDF did not report the required PDF/UA-1 profile.";
                return VeraPdfReportOutcome.Malformed;
            }

            if (!TryReadBoolean(report, ["compliant", "isCompliant"], out var isCompliant) ||
                !TryReadObject(report, "details", out var details) ||
                !TryReadInt32(details, "passedRules", out var passedRules) ||
                !TryReadInt32(details, "failedRules", out var failedRules) ||
                !TryReadInt32(details, "passedChecks", out var passedChecks) ||
                !TryReadInt32(details, "failedChecks", out var failedChecks) ||
                passedRules < 0 || failedRules < 0 || passedChecks < 0 || failedChecks < 0)
            {
                failure = "veraPDF returned incomplete validation counts.";
                return VeraPdfReportOutcome.Malformed;
            }

            if (!TryReadString(report, "jobEndStatus", out var jobEndStatus) ||
                !string.Equals(jobEndStatus, "normal", StringComparison.OrdinalIgnoreCase))
            {
                failure = "veraPDF did not complete the validation job normally.";
                return VeraPdfReportOutcome.OperationalFailure;
            }

            if (isCompliant && (failedRules != 0 || failedChecks != 0) ||
                !isCompliant && (failedRules == 0 || failedChecks == 0))
            {
                failure = "veraPDF returned internally inconsistent compliance counts.";
                return VeraPdfReportOutcome.Malformed;
            }

            var releaseDetails = FindNamedObjects(document.RootElement, "releaseDetails");
            var hasPinnedCoreVersion = releaseDetails.Any(detail =>
                TryReadString(detail, "id", out var id) &&
                string.Equals(id, "core", StringComparison.OrdinalIgnoreCase) &&
                TryReadString(detail, "version", out var version) &&
                string.Equals(
                    version,
                    DocumentProcessingOptions.RequiredVeraPdfVersion,
                    StringComparison.Ordinal));
            if (!hasPinnedCoreVersion)
            {
                failure = "veraPDF did not identify the pinned validator core version.";
                return VeraPdfReportOutcome.Malformed;
            }

            var batchSummaries = FindNamedObjects(document.RootElement, "batchSummary");
            if (batchSummaries.Count != 1)
            {
                failure = "veraPDF returned an invalid batch summary.";
                return VeraPdfReportOutcome.Malformed;
            }

            var batch = batchSummaries[0];
            if (!TryReadInt32(batch, ["totalJobs"], out var totalJobs) || totalJobs != 1 ||
                !TryReadInt32(batch, ["failedParsingJobs", "failedToParse"], out var failedToParse) ||
                !TryReadInt32(batch, ["failedEncryptedJobs", "encrypted"], out var encrypted) ||
                !TryReadInt32(batch, ["outOfMemory"], out var outOfMemory) ||
                !TryReadInt32(batch, ["veraExceptions"], out var veraExceptions) ||
                !TryReadObject(batch, ["validationSummary", "validationReports"], out var validationReports) ||
                !TryReadInt32(validationReports, ["compliantPdfaCount", "compliant"], out var compliant) ||
                !TryReadInt32(validationReports, ["nonCompliantPdfaCount", "nonCompliant"], out var nonCompliant) ||
                !TryReadInt32(validationReports, ["failedJobCount", "failedJobs"], out var failedJobs))
            {
                failure = "veraPDF returned an incomplete validation batch summary.";
                return VeraPdfReportOutcome.Malformed;
            }

            if (failedToParse != 0 || encrypted != 0 || outOfMemory != 0 ||
                veraExceptions != 0 || failedJobs != 0)
            {
                failure = "veraPDF reported an operational validation batch failure.";
                return VeraPdfReportOutcome.OperationalFailure;
            }

            if (!TryReadInt32(
                    validationReports,
                    ["totalJobCount", "totalJobs"],
                    out var validationJobs) || validationJobs != 1 ||
                compliant != (isCompliant ? 1 : 0) ||
                nonCompliant != (isCompliant ? 0 : 1))
            {
                failure = "veraPDF returned an inconsistent validation batch summary.";
                return VeraPdfReportOutcome.Malformed;
            }

            summary = new VeraPdfValidationSummary(
                isCompliant,
                passedRules,
                failedRules,
                passedChecks,
                failedChecks);
            failure = string.Empty;
            return VeraPdfReportOutcome.Valid;
        }
        catch (JsonException)
        {
            return VeraPdfReportOutcome.Malformed;
        }
    }

    private async Task<DocumentPdfResult?> VerifyToolVersionsAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        string veraPdfJarPath,
        CancellationToken cancellationToken)
    {
        var weasyPrint = await _processRunner.RunAsync(
            CreateProcessRequest(
                _options.WeasyPrintExecutablePath,
                ["--version"],
                workingDirectory,
                TimeSpan.FromSeconds(_options.VersionCheckTimeoutSeconds),
                16 * 1024,
                16 * 1024,
                environment),
            cancellationToken);
        var failure = ClassifyRequiredSuccess(weasyPrint, "WeasyPrint version check");
        if (failure is not null)
        {
            return failure;
        }

        var weasyVersion = ReadVersion(weasyPrint, WeasyPrintVersionPattern);
        if (!string.Equals(weasyVersion, _options.ExpectedWeasyPrintVersion, StringComparison.Ordinal))
        {
            return Failure(
                DocumentPdfOutcome.OperationalFailure,
                $"WeasyPrint {_options.ExpectedWeasyPrintVersion} is required.");
        }

        var java = await _processRunner.RunAsync(
            CreateProcessRequest(
                _options.JavaExecutablePath,
                ["-version"],
                workingDirectory,
                TimeSpan.FromSeconds(_options.VersionCheckTimeoutSeconds),
                16 * 1024,
                16 * 1024,
                environment),
            cancellationToken);
        failure = ClassifyRequiredSuccess(java, "Java version check");
        if (failure is not null)
        {
            return failure;
        }

        var javaVersion = ReadVersion(java, JavaVersionPattern);
        if (!TryReadJavaMajor(javaVersion, out var javaMajor) ||
            javaMajor != _options.ExpectedJavaMajorVersion)
        {
            return Failure(
                DocumentPdfOutcome.OperationalFailure,
                $"Java major version {_options.ExpectedJavaMajorVersion} is required.");
        }

        var veraPdf = await _processRunner.RunAsync(
            CreateProcessRequest(
                _options.JavaExecutablePath,
                [
                    "-Djava.awt.headless=true",
                    "-Dfile.encoding=UTF-8",
                    "-jar", veraPdfJarPath,
                    "--version"
                ],
                workingDirectory,
                TimeSpan.FromSeconds(_options.VersionCheckTimeoutSeconds),
                16 * 1024,
                16 * 1024,
                environment),
            cancellationToken);
        failure = ClassifyRequiredSuccess(veraPdf, "veraPDF version check");
        if (failure is not null)
        {
            return failure;
        }

        var veraPdfVersion = ReadVersion(veraPdf, VeraPdfVersionPattern);
        if (!string.Equals(veraPdfVersion, _options.ExpectedVeraPdfVersion, StringComparison.Ordinal))
        {
            return Failure(
                DocumentPdfOutcome.OperationalFailure,
                $"veraPDF CLI {_options.ExpectedVeraPdfVersion} is required.");
        }

        return null;
    }

    private DocumentPdfResult? ClassifyRequiredSuccess(
        ExternalProcessResult process,
        string operation)
    {
        return process.Completion switch
        {
            ExternalProcessCompletion.Exited when process.ExitCode == 0 => null,
            ExternalProcessCompletion.Cancelled =>
                Failure(DocumentPdfOutcome.Cancelled, $"{operation} was cancelled."),
            ExternalProcessCompletion.OutputLimitExceeded =>
                Failure(
                    DocumentPdfOutcome.ExternalProcessOutputTooLarge,
                    $"{operation} produced more console output than allowed."),
            ExternalProcessCompletion.TimedOut =>
                Failure(DocumentPdfOutcome.OperationalFailure, $"{operation} timed out."),
            _ => Failure(DocumentPdfOutcome.OperationalFailure, $"{operation} failed operationally.")
        };
    }

    private DocumentPdfResult? ClassifyValidationProcess(ExternalProcessResult process)
    {
        return process.Completion switch
        {
            ExternalProcessCompletion.Exited when process.ExitCode is 0 or 1 => null,
            ExternalProcessCompletion.Cancelled =>
                Failure(DocumentPdfOutcome.Cancelled, "PDF/UA validation was cancelled."),
            ExternalProcessCompletion.OutputLimitExceeded
                when process.LimitedStream == ExternalProcessStream.StandardOutput =>
                Failure(
                    DocumentPdfOutcome.ValidationReportTooLarge,
                    "veraPDF produced a validation report larger than allowed."),
            ExternalProcessCompletion.OutputLimitExceeded =>
                Failure(
                    DocumentPdfOutcome.ExternalProcessOutputTooLarge,
                    "veraPDF produced more diagnostic output than allowed."),
            ExternalProcessCompletion.TimedOut =>
                Failure(DocumentPdfOutcome.OperationalFailure, "PDF/UA validation timed out."),
            _ => Failure(DocumentPdfOutcome.OperationalFailure, "PDF/UA validation failed operationally.")
        };
    }

    private DocumentPdfResult Failure(DocumentPdfOutcome outcome, string reason)
    {
        return new DocumentPdfResult(
            outcome,
            null,
            null,
            null,
            null,
            null,
            FailureReason: reason);
    }

    private ExternalProcessRequest CreateProcessRequest(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        int maximumStandardOutputBytes,
        int maximumStandardErrorBytes,
        IReadOnlyDictionary<string, string> environment)
    {
        return new ExternalProcessRequest(
            executablePath,
            arguments.ToArray(),
            workingDirectory,
            timeout,
            maximumStandardOutputBytes,
            maximumStandardErrorBytes,
            new Dictionary<string, string>(environment, EnvironmentKeyComparer));
    }

    private static IReadOnlyDictionary<string, string> BuildEnvironment(
        string operationPath,
        string homePath,
        string temporaryPath,
        string cachePath,
        string fontConfigPath)
    {
        var environment = new Dictionary<string, string>(EnvironmentKeyComparer)
        {
            ["HOME"] = homePath,
            ["USERPROFILE"] = homePath,
            ["TMP"] = temporaryPath,
            ["TEMP"] = temporaryPath,
            ["TMPDIR"] = temporaryPath,
            ["XDG_CACHE_HOME"] = cachePath,
            ["FONTCONFIG_FILE"] = fontConfigPath,
            ["FONTCONFIG_PATH"] = operationPath,
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
            ["PYTHONHASHSEED"] = "0",
            ["TZ"] = "UTC"
        };

        AddHostEnvironmentIfPresent(environment, "SystemRoot");
        AddHostEnvironmentIfPresent(environment, "WINDIR");
        return environment;
    }

    private static void AddHostEnvironmentIfPresent(IDictionary<string, string> target, string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[name] = value;
        }
    }

    private static StringComparer EnvironmentKeyComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > maximumBytes || input.Length > int.MaxValue)
        {
            throw new IOException("The XHTML input exceeds its configured size limit.");
        }

        using var content = new MemoryStream(checked((int)input.Length));
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return content.ToArray();
            }

            if (content.Length + read > maximumBytes)
            {
                throw new IOException("The XHTML input exceeds its configured size limit.");
            }

            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<string> CopyAndHashAsync(
        string sourcePath,
        string destinationPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > maximumBytes)
        {
            throw new IOException("The configured font exceeds its allowed size.");
        }

        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                await destination.FlushAsync(cancellationToken);
                return Convert.ToHexString(hash.GetHashAndReset());
            }

            total += read;
            if (total > maximumBytes)
            {
                throw new IOException("The configured font exceeds its allowed size.");
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool HashesMatch(string actual, string expected)
    {
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(expected));
    }

    private static async Task WriteFontConfigAsync(
        string configPath,
        string fontPath,
        string cachePath,
        CancellationToken cancellationToken)
    {
        var config = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(
                "fontconfig",
                new XElement("dir", fontPath),
                new XElement("cachedir", cachePath)));
        await File.WriteAllTextAsync(
            configPath,
            config.ToString(SaveOptions.DisableFormatting),
            Utf8WithoutBom,
            cancellationToken);
    }

    private static async Task WriteFontStylesheetAsync(
        string stylesheetPath,
        string fontPath,
        string fontFormat,
        CancellationToken cancellationToken)
    {
        var fontUri = new Uri(fontPath).AbsoluteUri;
        var css = $$"""
            @font-face {
              font-family: "AccessLearn Noto Ethiopic";
              src: url("{{fontUri}}") format("{{fontFormat}}");
              font-style: normal;
              font-weight: 100 900;
            }
            html, body {
              font-family: "AccessLearn Noto Ethiopic", sans-serif !important;
            }
            """;
        await File.WriteAllTextAsync(stylesheetPath, css, Utf8WithoutBom, cancellationToken);
    }

    private async Task<PdfCheck> InspectPdfAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return PdfCheck.Malformed;
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length > _options.MaximumPdfBytes)
        {
            return PdfCheck.TooLarge;
        }

        if (stream.Length < 12)
        {
            return PdfCheck.Malformed;
        }

        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan().SequenceEqual("%PDF-"u8))
        {
            return PdfCheck.Malformed;
        }

        var tailLength = checked((int)Math.Min(stream.Length, 2 * 1024));
        var tail = new byte[tailLength];
        stream.Seek(-tailLength, SeekOrigin.End);
        await stream.ReadExactlyAsync(tail, cancellationToken);
        return Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal)
            ? PdfCheck.Valid
            : PdfCheck.Malformed;
    }

    private static bool TryValidateSelfContainedXhtml(byte[] content, out string failure)
    {
        failure = "The XHTML input is not a self-contained local document.";
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = Math.Max(1024, (long)content.Length * 2)
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null ||
                !string.Equals(root.Name.LocalName, "html", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(root.Name.NamespaceName, "http://www.w3.org/1999/xhtml", StringComparison.Ordinal))
            {
                failure = "The renderer input must be XHTML in the XHTML namespace.";
                return false;
            }

            foreach (var element in root.DescendantsAndSelf())
            {
                var elementName = element.Name.LocalName.ToLowerInvariant();
                if (elementName is "base" or "link" or "iframe" or "frame" or "embed" or "object")
                {
                    return false;
                }

                if (elementName == "meta" &&
                    element.Attributes().Any(attribute =>
                        string.Equals(attribute.Name.LocalName, "http-equiv", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(attribute.Value.Trim(), "refresh", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                foreach (var attribute in element.Attributes())
                {
                    var attributeName = attribute.Name.LocalName.ToLowerInvariant();
                    if (CssUrlPattern.IsMatch(attribute.Value) && !IsSelfContainedCss(attribute.Value))
                    {
                        return false;
                    }

                    if (attributeName == "style" && !IsSelfContainedCss(attribute.Value))
                    {
                        return false;
                    }

                    if (attributeName == "srcset" && !string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        return false;
                    }

                    if (attributeName is "src" or "poster" or "data" or "background" or "manifest" &&
                        !IsEmbeddedResource(attribute.Value))
                    {
                        return false;
                    }

                    if (attributeName == "href")
                    {
                        if (elementName is "a" or "area")
                        {
                            if (!IsSafeHyperlink(attribute.Value))
                            {
                                return false;
                            }
                        }
                        else if (!IsEmbeddedResource(attribute.Value))
                        {
                            return false;
                        }
                    }
                }

                if (elementName == "style" && !IsSelfContainedCss(element.Value))
                {
                    return false;
                }
            }

            failure = string.Empty;
            return true;
        }
        catch (XmlException exception)
        {
            failure = $"The renderer input is malformed XHTML: {exception.Message}";
            return false;
        }
    }

    private static bool IsEmbeddedResource(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith('#') ||
               string.IsNullOrEmpty(trimmed);
    }

    private static bool IsSafeHyperlink(string value)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
        {
            return true;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
               uri.Scheme is "https" or "http" or "mailto" or "tel";
    }

    private static bool IsSelfContainedCss(string css)
    {
        if (css.Contains('\\'))
        {
            return false;
        }

        var withoutComments = CssCommentPattern.Replace(css, string.Empty);
        if (withoutComments.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
            withoutComments.Contains("image-set", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var searchFrom = 0;
        while (true)
        {
            var match = CssUrlPattern.Match(withoutComments, searchFrom);
            if (!match.Success)
            {
                return true;
            }

            var position = match.Index + match.Length;
            while (position < withoutComments.Length && char.IsWhiteSpace(withoutComments[position]))
            {
                position++;
            }

            if (position >= withoutComments.Length)
            {
                return false;
            }

            var quote = withoutComments[position] is '\'' or '"' ? withoutComments[position++] : '\0';
            var valueStart = position;
            var valueEnd = quote == '\0'
                ? withoutComments.IndexOf(')', valueStart)
                : withoutComments.IndexOf(quote, valueStart);
            if (valueEnd < 0)
            {
                return false;
            }

            var resource = withoutComments[valueStart..valueEnd].Trim();
            position = valueEnd + (quote == '\0' ? 0 : 1);
            while (position < withoutComments.Length && char.IsWhiteSpace(withoutComments[position]))
            {
                position++;
            }

            if (position >= withoutComments.Length || withoutComments[position] != ')' ||
                !IsEmbeddedResource(resource))
            {
                return false;
            }

            searchFrom = position + 1;
        }
    }

    private static string? ReadVersion(ExternalProcessResult result, Regex pattern)
    {
        var output = string.Concat(result.StandardOutput, "\n", result.StandardError);
        var match = pattern.Match(output);
        return match.Success ? match.Groups["version"].Value : null;
    }

    private static bool TryReadJavaMajor(string? version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        var parts = version.Split('.');
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out major))
        {
            return false;
        }

        if (major == 1 && parts.Length > 1)
        {
            return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out major);
        }

        return true;
    }

    private static void PublishArtifacts(
        string stagedPdfPath,
        string stagedReportPath,
        string pdfOutputPath,
        string validationReportPath)
    {
        File.Move(stagedReportPath, validationReportPath);
        try
        {
            File.Move(stagedPdfPath, pdfOutputPath);
        }
        catch
        {
            File.Delete(validationReportPath);
            throw;
        }
    }

    private RequestPaths ValidateRequest(DocumentPdfRequest request)
    {
        var configuredRoot = Path.GetFullPath(_options.WorkspaceRootPath);
        if (!Directory.Exists(configuredRoot))
        {
            throw new ArgumentException("The configured document workspace root does not exist.", nameof(request));
        }

        var workspace = RequireContainedPath(request.WorkspacePath, configuredRoot, nameof(request.WorkspacePath));
        if (!Directory.Exists(workspace))
        {
            throw new ArgumentException("The document workspace does not exist.", nameof(request));
        }

        var xhtml = RequireContainedPath(request.XhtmlPath, workspace, nameof(request.XhtmlPath));
        var pdf = RequireContainedPath(request.PdfOutputPath, workspace, nameof(request.PdfOutputPath));
        var report = RequireContainedPath(
            request.ValidationReportPath,
            workspace,
            nameof(request.ValidationReportPath));
        if (!File.Exists(xhtml) ||
            !string.Equals(Path.GetExtension(xhtml), ".xhtml", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("An existing local .xhtml input is required.", nameof(request));
        }

        if (!string.Equals(Path.GetExtension(pdf), ".pdf", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(report), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("PDF and JSON output paths are required.", nameof(request));
        }

        if (File.Exists(pdf) || File.Exists(report))
        {
            throw new ArgumentException("Document toolchain output paths must not already exist.", nameof(request));
        }

        if (!Directory.Exists(Path.GetDirectoryName(pdf)) ||
            !Directory.Exists(Path.GetDirectoryName(report)))
        {
            throw new ArgumentException("Document output directories must already exist.", nameof(request));
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (comparer.Equals(xhtml, pdf) || comparer.Equals(xhtml, report) || comparer.Equals(pdf, report))
        {
            throw new ArgumentException("Document input and output paths must be distinct.", nameof(request));
        }

        return new RequestPaths(workspace, xhtml, pdf, report);
    }

    private static string RequireContainedPath(string path, string root, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute path is required.", propertyName);
        }

        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException("The path must remain inside the controlled workspace.", propertyName);
        }

        EnsureNoReparsePoints(root, relative, propertyName);

        return fullPath;
    }

    private static void EnsureNoReparsePoints(string root, string relativePath, string propertyName)
    {
        var current = root;
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ArgumentException(
                    "Symbolic links and other reparse points are not allowed inside the controlled workspace.",
                    propertyName);
            }
        }
    }

    private static List<JsonElement> FindNamedObjects(JsonElement root, string propertyName)
    {
        var objects = new List<JsonElement>();
        FindNamedObjects(root, propertyName, objects);
        return objects;
    }

    private static void FindNamedObjects(
        JsonElement element,
        string propertyName,
        ICollection<JsonElement> objects)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (PropertyNameEquals(property.Name, propertyName))
                {
                    AddObjects(property.Value, objects);
                }

                FindNamedObjects(property.Value, propertyName, objects);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                FindNamedObjects(item, propertyName, objects);
            }
        }
    }

    private static void AddObjects(JsonElement value, ICollection<JsonElement> objects)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            objects.Add(value);
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    objects.Add(item);
                }
            }
        }
    }

    private static bool TryReadObject(JsonElement element, string name, out JsonElement value)
    {
        return TryReadObject(element, [name], out value);
    }

    private static bool TryReadObject(
        JsonElement element,
        IReadOnlyList<string> names,
        out JsonElement value)
    {
        if (TryReadProperty(element, names, out value) && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryReadProperty(element, name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadBoolean(
        JsonElement element,
        IReadOnlyList<string> names,
        out bool value)
    {
        value = false;
        if (!TryReadProperty(element, names, out var property))
        {
            return false;
        }

        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }

        return property.ValueKind == JsonValueKind.String &&
               bool.TryParse(property.GetString(), out value);
    }

    private static bool TryReadInt32(JsonElement element, string name, out int value)
    {
        return TryReadInt32(element, [name], out value);
    }

    private static bool TryReadInt32(
        JsonElement element,
        IReadOnlyList<string> names,
        out int value)
    {
        value = 0;
        if (!TryReadProperty(element, names, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetInt32(out value);
        }

        return property.ValueKind == JsonValueKind.String &&
               int.TryParse(
                   property.GetString(),
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryReadProperty(JsonElement element, string name, out JsonElement value)
    {
        return TryReadProperty(element, [name], out value);
    }

    private static bool TryReadProperty(
        JsonElement element,
        IReadOnlyList<string> names,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Any(name => PropertyNameEquals(property.Name, name)))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool PropertyNameEquals(string actual, string expected)
    {
        return actual.TrimStart('@').Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteOperationDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal readonly record struct VeraPdfValidationSummary(
        bool IsCompliant,
        int PassedRules,
        int FailedRules,
        int PassedChecks,
        int FailedChecks);

    internal enum VeraPdfReportOutcome
    {
        Valid,
        Malformed,
        OperationalFailure
    }

    private sealed record RequestPaths(
        string WorkspacePath,
        string XhtmlPath,
        string PdfOutputPath,
        string ValidationReportPath);

    private enum PdfCheck
    {
        Valid,
        Malformed,
        TooLarge
    }
}
