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

    public void RecordValidation(Guid validationReportFileId, bool passed)
    {
        ValidationReportFileId = validationReportFileId != Guid.Empty
            ? validationReportFileId
            : throw new ArgumentException("Validation report is required.", nameof(validationReportFileId));
        Status = passed ? DocumentOutputStatus.Validated : DocumentOutputStatus.Failed;
    }
}
