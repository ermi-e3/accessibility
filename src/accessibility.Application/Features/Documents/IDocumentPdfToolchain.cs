namespace Accessibility.Application.Features.Documents;

public interface IDocumentPdfToolchain
{
    string RendererName { get; }

    string RequiredRendererVersion { get; }

    string ValidatorName { get; }

    string RequiredValidatorVersion { get; }

    string RequiredValidationProfile { get; }

    Task<DocumentPdfResult> GenerateAndValidateAsync(
        DocumentPdfRequest request,
        CancellationToken cancellationToken);
}

public sealed record DocumentPdfRequest(
    string WorkspacePath,
    string XhtmlPath,
    string PdfOutputPath,
    string ValidationReportPath);

public enum DocumentPdfOutcome
{
    PassedProfile,
    TaggedDraft,
    OperationalFailure,
    Cancelled,
    MalformedPdfOutput,
    PdfOutputTooLarge,
    MalformedValidationReport,
    ValidationReportTooLarge,
    ExternalProcessOutputTooLarge
}

public sealed record DocumentPdfResult(
    DocumentPdfOutcome Outcome,
    string? PdfPath,
    string? ValidationReportPath,
    string? RendererVersion,
    string? ValidatorVersion,
    string? ValidationProfile,
    int? PassedRules = null,
    int? FailedRules = null,
    int? PassedChecks = null,
    int? FailedChecks = null,
    string? FailureReason = null)
{
    public bool PassedRequiredProfile => Outcome == DocumentPdfOutcome.PassedProfile;

    public string ValidationLabel => Outcome switch
    {
        DocumentPdfOutcome.PassedProfile => "veraPDF PDF/UA-1 profile passed",
        DocumentPdfOutcome.TaggedDraft => "tagged draft",
        _ => "unavailable"
    };
}
