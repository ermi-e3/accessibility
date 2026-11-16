using Accessibility.Domain.Common;
using Accessibility.Domain.Enums;

namespace Accessibility.Domain.Entities;

public sealed class MediaAsset : AuditableEntity
{
    private MediaAsset()
    {
    }

    public MediaAsset(
        Guid lessonId,
        Guid storedFileId,
        MediaAssetRole role,
        string? language = null,
        long syncOffsetMilliseconds = 0,
        Guid? sourceMediaAssetId = null,
        long? sourceMediaVersion = null,
        Guid? processingJobId = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(syncOffsetMilliseconds, 0);
        LessonId = lessonId != Guid.Empty ? lessonId : throw new ArgumentException("Lesson is required.", nameof(lessonId));
        StoredFileId = storedFileId != Guid.Empty ? storedFileId : throw new ArgumentException("Stored file is required.", nameof(storedFileId));
        Role = role;
        Language = language is null ? null : DomainValidation.LanguageTag(language, nameof(language));
        SyncOffsetMilliseconds = syncOffsetMilliseconds;
        SourceMediaAssetId = sourceMediaAssetId;
        SourceMediaVersion = sourceMediaVersion;
        ProcessingJobId = processingJobId;

        if (role == MediaAssetRole.SignLanguage && Language != LanguageTags.EthiopianSignLanguage)
        {
            throw new ArgumentException("Ethiopian Sign Language media must use the 'eth' language tag.", nameof(language));
        }
    }

    public Guid LessonId { get; private set; }

    public Guid StoredFileId { get; private set; }

    public MediaAssetRole Role { get; private set; }

    public string? Language { get; private set; }

    public long? DurationMilliseconds { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public string? Codec { get; private set; }

    public long SyncOffsetMilliseconds { get; private set; }

    public ResourceStatus Status { get; private set; } = ResourceStatus.Pending;

    public bool IsActive { get; private set; } = true;

    public bool IsHumanReviewed { get; private set; }

    public string? AccessibilityMode { get; private set; }

    public Guid? SourceMediaAssetId { get; private set; }

    public long? SourceMediaVersion { get; private set; }

    public Guid? ProcessingJobId { get; private set; }

    public void MarkReady(long? durationMilliseconds, int? width, int? height, string? codec)
    {
        if (durationMilliseconds is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationMilliseconds));
        }

        DurationMilliseconds = durationMilliseconds;
        Width = width;
        Height = height;
        Codec = DomainValidation.OptionalText(codec, nameof(codec), 100);
        Status = ResourceStatus.Ready;
    }

    public void MarkStale()
    {
        Status = ResourceStatus.Stale;
    }

    public void ConfirmAccessibilityReview(string mode)
    {
        if (Role is not (MediaAssetRole.SignLanguage or MediaAssetRole.AudioDescription))
        {
            throw new InvalidOperationException("Only accessibility tracks require this review metadata.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        IsHumanReviewed = true;
        AccessibilityMode = mode.Trim().ToLowerInvariant();
    }

    public void StageAsReplacement()
    {
        if (Status != ResourceStatus.Pending)
        {
            throw new InvalidOperationException("Only a pending asset can be staged as a replacement.");
        }

        IsActive = false;
    }

    public void Activate()
    {
        if (Status != ResourceStatus.Ready)
        {
            throw new InvalidOperationException("Only a ready asset can become active.");
        }

        IsActive = true;
    }

    public void Reject()
    {
        Status = ResourceStatus.Rejected;
        IsActive = false;
    }

    public void Retry()
    {
        if (Status != ResourceStatus.Rejected)
        {
            throw new InvalidOperationException("Only a rejected asset can be retried.");
        }
        Status = ResourceStatus.Pending;
        IsActive = true;
    }

    public void Supersede()
    {
        Status = ResourceStatus.Superseded;
        IsActive = false;
    }
}
