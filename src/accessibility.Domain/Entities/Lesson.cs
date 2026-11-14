using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class Lesson : AuditableEntity
{
    private Lesson()
    {
    }

    public Lesson(Guid ownerUserId, string title, string sourceLanguage, string? description = null)
    {
        if (ownerUserId == Guid.Empty)
        {
            throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        }

        OwnerUserId = ownerUserId;
        Title = DomainValidation.RequiredText(title, nameof(title), 200);
        Description = DomainValidation.OptionalText(description, nameof(description), 2_000);
        SourceLanguage = DomainValidation.LanguageTag(sourceLanguage, nameof(sourceLanguage));
    }

    public Guid OwnerUserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string SourceLanguage { get; private set; } = LanguageTags.English;

    public LessonStatus Status { get; private set; } = LessonStatus.Draft;

    public long DraftRevision { get; private set; } = 1;

    public Guid? CurrentPublicationId { get; private set; }

    public void UpdateDetails(string title, string? description, string sourceLanguage)
    {
        if (Status == LessonStatus.Archived)
        {
            throw new InvalidOperationException("An archived lesson cannot be changed.");
        }

        Title = DomainValidation.RequiredText(title, nameof(title), 200);
        Description = DomainValidation.OptionalText(description, nameof(description), 2_000);
        SourceLanguage = DomainValidation.LanguageTag(sourceLanguage, nameof(sourceLanguage));
        AdvanceDraftRevision();
    }

    public void AdvanceDraftRevision()
    {
        if (Status == LessonStatus.Archived)
        {
            throw new InvalidOperationException("An archived lesson cannot be changed.");
        }

        DraftRevision++;
    }

    public void Publish(LessonPublication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        if (Status == LessonStatus.Archived)
        {
            throw new InvalidOperationException("An archived lesson cannot be published.");
        }

        if (publication.LessonId != Id)
        {
            throw new ArgumentException("Publication must belong to this lesson.", nameof(publication));
        }

        CurrentPublicationId = publication.Id;
        Status = LessonStatus.Published;
    }

    public void Archive()
    {
        Status = LessonStatus.Archived;
    }
}
