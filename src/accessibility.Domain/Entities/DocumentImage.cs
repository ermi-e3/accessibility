using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class DocumentImage : AuditableEntity
{
    private DocumentImage()
    {
    }

    public DocumentImage(
        Guid documentId,
        Guid storedFileId,
        int sequence,
        string relationshipId,
        string sourceLocation,
        string? suggestedAltText,
        bool suggestedDecorative)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        DocumentId = documentId != Guid.Empty ? documentId : throw new ArgumentException("Document is required.", nameof(documentId));
        StoredFileId = storedFileId != Guid.Empty ? storedFileId : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        Sequence = sequence;
        RelationshipId = DomainValidation.RequiredText(relationshipId, nameof(relationshipId), 200);
        SourceLocation = DomainValidation.RequiredText(sourceLocation, nameof(sourceLocation), 500);
        SuggestedAltText = DomainValidation.OptionalText(suggestedAltText, nameof(suggestedAltText), 2_000);
        SuggestedDecorative = suggestedDecorative;
    }

    public Guid DocumentId { get; private set; }

    public Guid StoredFileId { get; private set; }

    public int Sequence { get; private set; }

    public string RelationshipId { get; private set; } = string.Empty;

    public string SourceLocation { get; private set; } = string.Empty;

    public string? SuggestedAltText { get; private set; }

    public bool SuggestedDecorative { get; private set; }

    public string? AltText { get; private set; }

    public bool IsDecorative { get; private set; }

    public ReviewStatus ReviewStatus { get; private set; } = ReviewStatus.Draft;

    public Guid? ReviewedByUserId { get; private set; }

    public DateTimeOffset? ReviewedAt { get; private set; }

    public void ApproveDescription(
        string? altText,
        bool isDecorative,
        Guid reviewedByUserId,
        DateTimeOffset reviewedAt)
    {
        if (isDecorative == !string.IsNullOrWhiteSpace(altText))
        {
            throw new ArgumentException("Provide alt text or mark the image decorative, but not both.", nameof(altText));
        }

        AltText = isDecorative ? null : DomainValidation.RequiredText(altText!, nameof(altText), 2_000);
        IsDecorative = isDecorative;
        ReviewedByUserId = reviewedByUserId != Guid.Empty
            ? reviewedByUserId
            : throw new ArgumentException("Reviewer is required.", nameof(reviewedByUserId));
        ReviewedAt = reviewedAt.ToUniversalTime();
        ReviewStatus = ReviewStatus.Approved;
    }
}
