using System.Text.RegularExpressions;

namespace Accessibility.Domain.Common;

internal static partial class DomainValidation
{
    [GeneratedRegex("^[A-Za-z]{2,8}(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagPattern();

    public static string RequiredText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value cannot exceed {maximumLength} characters.");
        }

        return normalized;
    }

    public static string? OptionalText(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RequiredText(value, parameterName, maximumLength);
    }

    public static string LanguageTag(string value, string parameterName)
    {
        var normalized = RequiredText(value, parameterName, 35).ToLowerInvariant();
        if (!LanguageTagPattern().IsMatch(normalized))
        {
            throw new ArgumentException("Value must be a valid BCP 47 language tag.", parameterName);
        }

        return normalized;
    }

    public static string Sha256(string value, string parameterName)
    {
        var normalized = RequiredText(value, parameterName, 64).ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Value must be a lowercase SHA-256 hex digest.", parameterName);
        }

        return normalized;
    }
}
