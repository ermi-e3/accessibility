namespace Accessibility.Domain.Enums;

public enum LessonStatus
{
    Draft,
    Published,
    Archived
}

public enum StoredFileStatus
{
    Quarantined,
    Ready,
    Rejected,
    Deleting,
    Deleted
}

public enum MediaAssetRole
{
    PrimaryVideo,
    PrimaryAudio,
    PlaybackRendition,
    OfflineAudio,
    SignLanguage,
    AudioDescription,
    Poster
}

public enum ResourceStatus
{
    Pending,
    Ready,
    Rejected,
    Stale,
    Superseded,
    Failed
}

public enum TranscriptOrigin
{
    Generated,
    Imported,
    Manual
}

public enum ReviewStatus
{
    Draft,
    Approved,
    Stale,
    Superseded
}

public enum CaptionFormat
{
    WebVtt,
    Srt,
    Text
}

public enum DocumentStatus
{
    Uploaded,
    Normalizing,
    NeedsReview,
    Ready,
    Stale,
    Failed
}

public enum DocumentOutputKind
{
    Html,
    Pdf
}

public enum DocumentOutputStatus
{
    Generated,
    Validated,
    ValidationFailed,
    Failed,
    Stale
}

public enum OfflinePackageProfile
{
    Lightweight
}

public enum OfflinePackageStatus
{
    Queued,
    Building,
    Ready,
    Failed,
    Stale
}

public enum ProcessingJobType
{
    PrepareMedia,
    Transcribe,
    Translate,
    GenerateCaptions,
    NormalizeDocument,
    GenerateDocumentOutputs,
    GenerateOfflinePackage
}

public enum ProcessingJobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum IntegrationClientStatus
{
    Active,
    Revoked
}
