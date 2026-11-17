using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class AccessibleDocument : AuditableEntity
{
    private AccessibleDocument()
    {
    }

    public AccessibleDocument(Guid lessonId, Guid sourceFileId, string title, string language)
    {
        LessonId = lessonId != Guid.Empty ? lessonId : throw new ArgumentException("Lesson is required.", nameof(lessonId));
        SourceFileId = sourceFileId != Guid.Empty ? sourceFileId : throw new ArgumentException("Source file is required.", nameof(sourceFileId));
        Title = DomainValidation.RequiredText(title, nameof(title), 200);
        Language = DomainValidation.LanguageTag(language, nameof(language));
    }

    public Guid LessonId { get; private set; }

    public Guid SourceFileId { get; private set; }

    public Guid? NormalizedModelFileId { get; private set; }

    public string? NormalizerVersion { get; private set; }

    public string? IssueSummaryJson { get; private set; }

    public DateTimeOffset? NormalizedAt { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Language { get; private set; } = LanguageTags.English;

    public DocumentStatus Status { get; private set; } = DocumentStatus.Uploaded;

    public void MarkNormalizing()
    {
        if (Status is not (DocumentStatus.Uploaded or DocumentStatus.Failed or DocumentStatus.Stale))
        {
            throw new InvalidOperationException("Only an uploaded, failed, or stale document can be normalized.");
        }

        Status = DocumentStatus.Normalizing;
    }

    public void MarkNormalized(
        Guid normalizedModelFileId,
        string normalizerVersion,
        string issueSummaryJson,
        bool needsReview,
        DateTimeOffset normalizedAt)
    {
        NormalizedModelFileId = normalizedModelFileId != Guid.Empty
            ? normalizedModelFileId
            : throw new ArgumentException("Normalized model file is required.", nameof(normalizedModelFileId));
        NormalizerVersion = DomainValidation.RequiredText(normalizerVersion, nameof(normalizerVersion), 100);
        IssueSummaryJson = DomainValidation.RequiredText(issueSummaryJson, nameof(issueSummaryJson), 64_000);
        NormalizedAt = normalizedAt.ToUniversalTime();
        Status = needsReview ? DocumentStatus.NeedsReview : DocumentStatus.Ready;
    }

    public void SetReviewReadiness(bool ready)
    {
        if (NormalizedModelFileId is null)
        {
            throw new InvalidOperationException("The document must be normalized before review can complete.");
        }

        Status = ready ? DocumentStatus.Ready : DocumentStatus.NeedsReview;
    }

    public void MarkStale()
    {
        Status = DocumentStatus.Stale;
    }

    public void Fail()
    {
        Status = DocumentStatus.Failed;
    }
}
