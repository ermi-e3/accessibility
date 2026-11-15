using Accessibility.Domain.Common;

namespace Accessibility.Domain.Entities;

public sealed class LessonPublication : AuditableEntity
{
    private LessonPublication()
    {
    }

    public LessonPublication(
        Guid lessonId,
        int revision,
        Guid manifestFileId,
        Guid publishedByUserId,
        string checksum,
        DateTimeOffset publishedAt,
        Guid? id = null)
        : base(id ?? Guid.CreateVersion7())
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);
        LessonId = lessonId != Guid.Empty ? lessonId : throw new ArgumentException("Lesson is required.", nameof(lessonId));
        ManifestFileId = manifestFileId != Guid.Empty ? manifestFileId : throw new ArgumentException("Manifest file is required.", nameof(manifestFileId));
        PublishedByUserId = publishedByUserId != Guid.Empty ? publishedByUserId : throw new ArgumentException("Publisher is required.", nameof(publishedByUserId));
        Revision = revision;
        Checksum = DomainValidation.Sha256(checksum, nameof(checksum));
        PublishedAt = publishedAt.ToUniversalTime();
    }

    public Guid LessonId { get; private set; }

    public int Revision { get; private set; }

    public Guid ManifestFileId { get; private set; }

    public Guid PublishedByUserId { get; private set; }

    public string Checksum { get; private set; } = string.Empty;

    public DateTimeOffset PublishedAt { get; private set; }

}
