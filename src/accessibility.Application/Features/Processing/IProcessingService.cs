using Accessibility.Application.Abstractions;

namespace Accessibility.Application.Features.Processing;

public interface IProcessingService
{
    Task EnsureLessonAccessAsync(Guid lessonId, CancellationToken cancellationToken);

    Task<ProcessingJobModel> StartAsync(
        Guid lessonId,
        StartProcessingCommand command,
        string? requestIdempotencyKey,
        CancellationToken cancellationToken);

    Task<ProcessingJobModel> GetAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ProcessingJobModel> RetryAsync(Guid jobId, CancellationToken cancellationToken);

    Task<ProcessingJobModel> CancelAsync(Guid jobId, CancellationToken cancellationToken);

    Task<TranscriptEditorModel> GetTranscriptAsync(Guid transcriptId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TranscriptSummaryModel>> ListTranscriptsAsync(
        Guid lessonId,
        CancellationToken cancellationToken);

    Task<TranscriptEditorModel> UpdateTranscriptAsync(
        Guid transcriptId,
        long expectedVersion,
        UpdateTranscriptCommand command,
        CancellationToken cancellationToken);

    Task<TranscriptEditorModel> CreateDraftRevisionAsync(
        Guid transcriptId,
        CancellationToken cancellationToken);

    Task<TranscriptEditorModel> ImportTranscriptAsync(
        Guid lessonId,
        ImportTranscriptCommand command,
        CancellationToken cancellationToken);

    Task<TranscriptExportModel> ExportTranscriptAsync(
        Guid transcriptId,
        TranscriptExchangeFormat format,
        CancellationToken cancellationToken);

    Task<DocumentSourceModel> UploadDocumentAsync(
        Guid lessonId,
        UploadDocumentCommand command,
        QuarantinedUpload upload,
        CancellationToken cancellationToken);
}

public sealed record StartProcessingCommand(
    Guid SourceAssetId,
    IReadOnlyList<string> TargetLanguages);

public sealed record PrepareAccessibilityPayload(
    Guid SourceAssetId,
    long SourceAssetVersion,
    string SourceFileSha256,
    IReadOnlyList<string> TargetLanguages,
    string ConfigurationVersion,
    Guid? SourceTranscriptId,
    long? SourceTranscriptVersion,
    string? SourceTranscriptFingerprint);

public sealed record UpdateTranscriptCommand(
    IReadOnlyList<TranscriptSegmentCommand> Segments);

public sealed record ImportTranscriptCommand(
    string Language,
    TranscriptExchangeFormat Format,
    string Content);

public sealed record TranscriptSegmentCommand(
    int Sequence,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    string? SpeakerLabel);

public sealed record ProcessingJobModel(
    Guid Id,
    Guid? LessonId,
    Guid? MediaAssetId,
    string Type,
    string Status,
    int ProgressPercentage,
    int Attempts,
    string? Provider,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? CancellationRequestedAt,
    long Version,
    IReadOnlyList<ProcessingAttemptModel>? AttemptHistory = null);

public sealed record ProcessingAttemptModel(
    int AttemptNumber,
    string Status,
    string? Provider,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record TranscriptEditorModel(
    Guid Id,
    Guid LessonId,
    string Language,
    int Revision,
    string Origin,
    string ReviewStatus,
    string? Provider,
    string? ProviderVersion,
    IReadOnlyList<TranscriptSegmentCommand> Segments,
    long Version);

public sealed record TranscriptSummaryModel(
    Guid Id,
    string Language,
    int Revision,
    string Origin,
    string ReviewStatus,
    string? Provider,
    string? ProviderVersion,
    Guid? ProcessingJobId,
    long Version,
    DateTimeOffset UpdatedAt);

public sealed record TranscriptExportModel(
    string FileName,
    string ContentType,
    string Content);

public sealed record UploadDocumentCommand(string Title, string Language);

public sealed record DocumentSourceModel(
    Guid Id,
    Guid LessonId,
    string Title,
    string Language,
    string Status,
    string FileName,
    long ByteLength,
    string Sha256,
    long Version);
