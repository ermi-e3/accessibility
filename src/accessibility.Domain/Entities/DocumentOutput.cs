using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class DocumentOutput : AuditableEntity
{
    private DocumentOutput()
    {
    }

    public DocumentOutput(Guid documentId, long sourceRevision, DocumentOutputKind kind, Guid storedFileId, string generatorVersion)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRevision, 1);
        DocumentId = documentId != Guid.Empty ? documentId : throw new ArgumentException("Document is required.", nameof(documentId));
        StoredFileId = storedFileId != Guid.Empty ? storedFileId : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        SourceRevision = sourceRevision;
        Kind = kind;
        GeneratorVersion = DomainValidation.RequiredText(generatorVersion, nameof(generatorVersion), 100);
    }

    public Guid DocumentId { get; private set; }

    public long SourceRevision { get; private set; }

    public DocumentOutputKind Kind { get; private set; }

    public Guid StoredFileId { get; private set; }

    public Guid? ValidationReportFileId { get; private set; }

    public string GeneratorVersion { get; private set; } = string.Empty;

    public DocumentOutputStatus Status { get; private set; } = DocumentOutputStatus.Generated;

    public string? ValidatorName { get; private set; }

    public string? ValidatorVersion { get; private set; }

    public string? ValidationProfile { get; private set; }

    public string? ValidationSummaryJson { get; private set; }

    public DateTimeOffset? ValidatedAt { get; private set; }

    public void RecordValidation(
        Guid validationReportFileId,
        bool passed,
        string validatorName,
        string validatorVersion,
        string validationProfile,
        string validationSummaryJson,
        DateTimeOffset validatedAt)
    {
        ValidationReportFileId = validationReportFileId != Guid.Empty
            ? validationReportFileId
            : throw new ArgumentException("Validation report is required.", nameof(validationReportFileId));
        ValidatorName = DomainValidation.RequiredText(validatorName, nameof(validatorName), 100);
        ValidatorVersion = DomainValidation.RequiredText(validatorVersion, nameof(validatorVersion), 100);
        ValidationProfile = DomainValidation.RequiredText(validationProfile, nameof(validationProfile), 100);
        ValidationSummaryJson = DomainValidation.RequiredText(validationSummaryJson, nameof(validationSummaryJson), 64_000);
        ValidatedAt = validatedAt.ToUniversalTime();
        Status = passed ? DocumentOutputStatus.Validated : DocumentOutputStatus.ValidationFailed;
    }

    public void MarkStale()
    {
        Status = DocumentOutputStatus.Stale;
    }

    public void RecordFailure(
        Guid validationReportFileId,
        string validatorName,
        string validatorVersion,
        string validationProfile,
        string validationSummaryJson,
        DateTimeOffset failedAt)
    {
        ValidationReportFileId = validationReportFileId != Guid.Empty
            ? validationReportFileId
            : throw new ArgumentException("Failure report file is required.", nameof(validationReportFileId));
        ValidatorName = DomainValidation.RequiredText(validatorName, nameof(validatorName), 100);
        ValidatorVersion = DomainValidation.RequiredText(validatorVersion, nameof(validatorVersion), 100);
        ValidationProfile = DomainValidation.RequiredText(validationProfile, nameof(validationProfile), 100);
        ValidationSummaryJson = DomainValidation.RequiredText(validationSummaryJson, nameof(validationSummaryJson), 64_000);
        ValidatedAt = failedAt.ToUniversalTime();
        Status = DocumentOutputStatus.Failed;
    }

    public void RetryGeneration(Guid storedFileId, string generatorVersion)
    {
        if (Status != DocumentOutputStatus.Failed)
        {
            throw new InvalidOperationException("Only a failed output can be regenerated in place.");
        }

        StoredFileId = storedFileId != Guid.Empty
            ? storedFileId
            : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        GeneratorVersion = DomainValidation.RequiredText(generatorVersion, nameof(generatorVersion), 100);
        ValidationReportFileId = null;
        ValidatorName = null;
        ValidatorVersion = null;
        ValidationProfile = null;
        ValidationSummaryJson = null;
        ValidatedAt = null;
        Status = DocumentOutputStatus.Generated;
    }
}
