using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class DocumentImage : AuditableEntity
{
    private DocumentImage()
    {
    }

    public DocumentImage(Guid documentId, Guid storedFileId, int sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        DocumentId = documentId != Guid.Empty ? documentId : throw new ArgumentException("Document is required.", nameof(documentId));
        StoredFileId = storedFileId != Guid.Empty ? storedFileId : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        Sequence = sequence;
    }

    public Guid DocumentId { get; private set; }

    public Guid StoredFileId { get; private set; }

    public int Sequence { get; private set; }

    public string? AltText { get; private set; }

    public bool IsDecorative { get; private set; }

    public ReviewStatus ReviewStatus { get; private set; } = ReviewStatus.Draft;

    public void ApproveDescription(string? altText, bool isDecorative)
    {
        if (isDecorative == !string.IsNullOrWhiteSpace(altText))
        {
            throw new ArgumentException("Provide alt text or mark the image decorative, but not both.", nameof(altText));
        }

        AltText = isDecorative ? null : DomainValidation.RequiredText(altText!, nameof(altText), 2_000);
        IsDecorative = isDecorative;
        ReviewStatus = ReviewStatus.Approved;
    }
}
