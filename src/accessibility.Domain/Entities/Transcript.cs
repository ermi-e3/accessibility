using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class Transcript : AuditableEntity
{
    private Transcript()
    {
    }

    public Transcript(
        Guid lessonId,
        string language,
        int revision,
        TranscriptOrigin origin,
        long sourceLessonRevision,
        Guid? sourceTranscriptId = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceLessonRevision, 1);
        LessonId = lessonId != Guid.Empty ? lessonId : throw new ArgumentException("Lesson is required.", nameof(lessonId));
        Language = DomainValidation.LanguageTag(language, nameof(language));
        Revision = revision;
        Origin = origin;
        SourceLessonRevision = sourceLessonRevision;
        SourceTranscriptId = sourceTranscriptId;
    }

    public Guid LessonId { get; private set; }

    public string Language { get; private set; } = LanguageTags.English;

    public int Revision { get; private set; }

    public TranscriptOrigin Origin { get; private set; }

    public Guid? SourceTranscriptId { get; private set; }

    public long SourceLessonRevision { get; private set; }

    public ReviewStatus ReviewStatus { get; private set; } = ReviewStatus.Draft;

    public Guid? ApprovedByUserId { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public void Approve(Guid userId, DateTimeOffset approvedAt)
    {
        if (ReviewStatus != ReviewStatus.Draft)
        {
            throw new InvalidOperationException("Only a draft transcript can be approved.");
        }

        ApprovedByUserId = userId != Guid.Empty ? userId : throw new ArgumentException("Approver is required.", nameof(userId));
        ApprovedAt = approvedAt.ToUniversalTime();
        ReviewStatus = ReviewStatus.Approved;
    }

    public void MarkStale()
    {
        ReviewStatus = ReviewStatus.Stale;
    }

    public void Supersede()
    {
        ReviewStatus = ReviewStatus.Superseded;
    }
}
