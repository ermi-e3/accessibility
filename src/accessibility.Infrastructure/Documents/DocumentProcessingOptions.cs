namespace Accessibility.Infrastructure.Documents;

public sealed class DocumentProcessingOptions
{
    public const string SectionName = "DocumentProcessing";
    public const string RequiredWeasyPrintVersion = "70.0";
    public const string RequiredVeraPdfVersion = "1.30.4";
    public const string RequiredVeraPdfProfile = "ua1";

    public string WorkspaceRootPath { get; init; } =
        Path.Combine(Path.GetTempPath(), "AccessLearn", "processing");

    public string WeasyPrintExecutablePath { get; init; } = string.Empty;

    public string ExpectedWeasyPrintVersion { get; init; } = RequiredWeasyPrintVersion;

    public string JavaExecutablePath { get; init; } = string.Empty;

    public int ExpectedJavaMajorVersion { get; init; }

    public string VeraPdfJarPath { get; init; } = string.Empty;

    public string VeraPdfJarSha256 { get; init; } = string.Empty;

    public string ExpectedVeraPdfVersion { get; init; } = RequiredVeraPdfVersion;

    public string VeraPdfProfile { get; init; } = RequiredVeraPdfProfile;

    public string NotoEthiopicFontPath { get; init; } = string.Empty;

    public string NotoEthiopicFontSha256 { get; init; } = string.Empty;

    public int VersionCheckTimeoutSeconds { get; init; } = 15;

    public int GenerationTimeoutSeconds { get; init; } = 120;

    public int ValidationTimeoutSeconds { get; init; } = 120;

    public int MaximumStandardOutputBytes { get; init; } = 256 * 1024;

    public int MaximumStandardErrorBytes { get; init; } = 512 * 1024;

    public int MaximumValidationReportBytes { get; init; } = 8 * 1024 * 1024;

    public long MaximumXhtmlBytes { get; init; } = 8 * 1024 * 1024;

    public long MaximumPdfBytes { get; init; } = 128 * 1024 * 1024;

    public void Validate()
    {
        RequireAbsolutePath(WorkspaceRootPath, nameof(WorkspaceRootPath));
        RequireAbsolutePath(WeasyPrintExecutablePath, nameof(WeasyPrintExecutablePath));
        RequireAbsolutePath(JavaExecutablePath, nameof(JavaExecutablePath));
        RequireAbsolutePath(VeraPdfJarPath, nameof(VeraPdfJarPath));
        RequireAbsolutePath(NotoEthiopicFontPath, nameof(NotoEthiopicFontPath));
        if (!string.Equals(Path.GetExtension(VeraPdfJarPath), ".jar", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The veraPDF CLI resource must be a JAR file.", nameof(VeraPdfJarPath));
        }

        var fontExtension = Path.GetExtension(NotoEthiopicFontPath);
        if (!string.Equals(fontExtension, ".ttf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fontExtension, ".otf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The Noto Ethiopic resource must be a TTF or OTF font.", nameof(NotoEthiopicFontPath));
        }

        if (!string.Equals(ExpectedWeasyPrintVersion, RequiredWeasyPrintVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Only WeasyPrint {RequiredWeasyPrintVersion} is supported.",
                nameof(ExpectedWeasyPrintVersion));
        }

        if (ExpectedJavaMajorVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ExpectedJavaMajorVersion),
                "An exact Java major version is required.");
        }

        if (!string.Equals(ExpectedVeraPdfVersion, RequiredVeraPdfVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Only veraPDF CLI {RequiredVeraPdfVersion} is supported.",
                nameof(ExpectedVeraPdfVersion));
        }

        if (!string.Equals(VeraPdfProfile, RequiredVeraPdfProfile, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The validation profile must be exactly '{RequiredVeraPdfProfile}'.",
                nameof(VeraPdfProfile));
        }

        RequireSha256(VeraPdfJarSha256, nameof(VeraPdfJarSha256));
        RequireSha256(NotoEthiopicFontSha256, nameof(NotoEthiopicFontSha256));
        RequireRange(VersionCheckTimeoutSeconds, 1, 86_400, nameof(VersionCheckTimeoutSeconds));
        RequireRange(GenerationTimeoutSeconds, 1, 86_400, nameof(GenerationTimeoutSeconds));
        RequireRange(ValidationTimeoutSeconds, 1, 86_400, nameof(ValidationTimeoutSeconds));
        RequireRange(MaximumStandardOutputBytes, 1, 16L * 1024 * 1024, nameof(MaximumStandardOutputBytes));
        RequireRange(MaximumStandardErrorBytes, 1, 16L * 1024 * 1024, nameof(MaximumStandardErrorBytes));
        RequireRange(
            MaximumValidationReportBytes,
            1,
            64L * 1024 * 1024,
            nameof(MaximumValidationReportBytes));
        RequireRange(MaximumXhtmlBytes, 1, 64L * 1024 * 1024, nameof(MaximumXhtmlBytes));
        RequireRange(MaximumPdfBytes, 1, 1024L * 1024 * 1024, nameof(MaximumPdfBytes));
    }

    private static void RequireAbsolutePath(string path, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("An absolute path is required.", propertyName);
        }
    }

    private static void RequireSha256(string hash, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(hash) ||
            hash.Length != 64 ||
            hash.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("A 64-character SHA-256 value is required.", propertyName);
        }
    }

    private static void RequireRange(long value, long minimum, long maximum, string propertyName)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                propertyName,
                $"The value must be between {minimum} and {maximum}.");
        }
    }
}
