using System.Globalization;

namespace Accessibility.Application.Features.Documents;

public sealed record DocxPackageSafetyLimits
{
    public static DocxPackageSafetyLimits Default { get; } = new();

    public long MaximumPackageBytes { get; init; } = 25L * 1024 * 1024;

    public int MaximumEntries { get; init; } = 4_096;

    public long MaximumExpandedBytes { get; init; } = 100L * 1024 * 1024;

    public long MaximumSingleEntryBytes { get; init; } = 25L * 1024 * 1024;

    public double MaximumCompressionRatio { get; init; } = 100;

    public long MaximumXmlCharacters { get; init; } = 10L * 1024 * 1024;
}

public sealed record DocxPackageSafetyReport(
    int EntryCount,
    long CompressedBytes,
    long ExpandedBytes,
    IReadOnlyList<string> ExternalHyperlinks);

public sealed class DocxPackageRejectedException : IOException
{
    public DocxPackageRejectedException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class DocumentTransformationException : InvalidOperationException
{
    public DocumentTransformationException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed record DocumentNormalizationOptions(string Title, string? DefaultLanguage = null);

public sealed record ReviewedImageDecision
{
    public ReviewedImageDecision(string imageId, string? altText, bool isDecorative)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        if (isDecorative)
        {
            if (!string.IsNullOrWhiteSpace(altText))
            {
                throw new ArgumentException("A decorative image cannot also have alternative text.", nameof(altText));
            }
        }
        else if (string.IsNullOrWhiteSpace(altText))
        {
            throw new ArgumentException("A meaningful image requires reviewed alternative text.", nameof(altText));
        }

        var normalizedAlt = isDecorative ? null : altText!.Trim();
        if (normalizedAlt?.Length > 2_000 || normalizedAlt?.Any(character => char.IsControl(character) && !char.IsWhiteSpace(character)) == true)
        {
            throw new ArgumentException("Alternative text is invalid or exceeds 2,000 characters.", nameof(altText));
        }

        ImageId = imageId.Trim();
        AltText = normalizedAlt;
        IsDecorative = isDecorative;
    }

    public string ImageId { get; }

    public string? AltText { get; }

    public bool IsDecorative { get; }
}

public sealed record DocumentArtifactValidationResult(IReadOnlyList<DocumentIssue> Issues)
{
    public bool IsValid => Issues.All(issue => issue.Severity != DocumentIssueSeverity.Blocking);
}

public sealed record SemanticHtmlArtifact(
    string ContentType,
    string FileExtension,
    string Content,
    DocumentArtifactValidationResult Validation);

public interface IDocxPackageSafetyValidator
{
    DocxPackageSafetyReport Validate(Stream package, CancellationToken cancellationToken = default);
}

public interface IOpenXmlDocumentNormalizer
{
    string Version { get; }

    NormalizedDocumentModel Normalize(
        Stream package,
        DocumentNormalizationOptions options,
        CancellationToken cancellationToken = default);
}

public interface ISemanticHtmlDocumentRenderer
{
    string Version { get; }

    SemanticHtmlArtifact Render(
        NormalizedDocumentModel document,
        IReadOnlyCollection<ReviewedImageDecision>? reviewedImages = null);

    SemanticHtmlArtifact RenderPreview(
        NormalizedDocumentModel document,
        IReadOnlyCollection<ReviewedImageDecision>? reviewedImages = null);

    DocumentArtifactValidationResult Validate(string xhtml);
}

public static class DocumentLanguagePolicy
{
    public static bool IsValid(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Length > 63)
        {
            return false;
        }

        var subtags = language.Split('-');
        if (subtags.Length == 0 || subtags[0].Length is < 2 or > 8 || !subtags[0].All(char.IsLetter))
        {
            return false;
        }

        return subtags.Skip(1).All(subtag =>
            subtag.Length is >= 1 and <= 8 && subtag.All(char.IsLetterOrDigit));
    }

    public static string Normalize(string language)
    {
        if (!IsValid(language))
        {
            throw new ArgumentException("A valid BCP 47 language tag is required.", nameof(language));
        }

        var subtags = language.Trim().Split('-');
        subtags[0] = subtags[0].ToLowerInvariant();
        for (var index = 1; index < subtags.Length; index++)
        {
            subtags[index] = subtags[index].Length switch
            {
                2 when subtags[index].All(char.IsLetter) => subtags[index].ToUpperInvariant(),
                4 when subtags[index].All(char.IsLetter) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(subtags[index].ToLowerInvariant()),
                _ => subtags[index].ToLowerInvariant()
            };
        }

        return string.Join('-', subtags);
    }
}

public static class DocumentLinkPolicy
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        Uri.UriSchemeHttp,
        Uri.UriSchemeHttps,
        Uri.UriSchemeMailto
    };

    public static bool TryNormalizeExternalHref(string? href, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(href) ||
            href.Any(character => char.IsControl(character)) ||
            !Uri.TryCreate(href.Trim(), UriKind.Absolute, out var uri) ||
            !AllowedSchemes.Contains(uri.Scheme))
        {
            return false;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(uri.GetComponents(UriComponents.Path, UriFormat.Unescaped)) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        normalized = uri.AbsoluteUri;
        return true;
    }
}
