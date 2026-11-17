using Accessibility.Application.Abstractions;
using Accessibility.Application.Features.Processing;

namespace Accessibility.Application.Features.Documents;

public interface IDocumentService
{
    Task<DocumentUploadResult> UploadAsync(
        Guid lessonId,
        UploadDocumentCommand command,
        QuarantinedUpload upload,
        CancellationToken cancellationToken);

    Task<DocumentDetailModel> GetAsync(Guid documentId, CancellationToken cancellationToken);

    Task<DocumentDetailModel> ReviewImageAsync(
        Guid documentId,
        Guid imageId,
        long expectedVersion,
        ReviewDocumentImageCommand command,
        CancellationToken cancellationToken);

    Task<DocumentDownloadModel> OpenImageAsync(
        Guid documentId,
        Guid imageId,
        CancellationToken cancellationToken);

    Task<DocumentDownloadModel> OpenNormalizedModelAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<DocumentPreviewModel> GetPreviewAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<ProcessingJobModel> GenerateOutputsAsync(
        Guid documentId,
        long expectedVersion,
        GenerateDocumentOutputsCommand command,
        string? requestIdempotencyKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentOutputModel>> ListOutputsAsync(
        Guid documentId,
        CancellationToken cancellationToken);

    Task<DocumentDownloadModel> OpenOutputAsync(
        Guid documentId,
        Guid outputId,
        bool validationReport,
        CancellationToken cancellationToken);
}

public sealed record UploadDocumentCommand(string Title, string Language);

public sealed record ReviewDocumentImageCommand(string? AltText, bool IsDecorative);

public sealed record GenerateDocumentOutputsCommand(IReadOnlyList<string> Formats);

public sealed record DocumentUploadResult(
    DocumentDetailModel Document,
    ProcessingJobModel Job);

public sealed record DocumentDetailModel(
    Guid Id,
    Guid LessonId,
    string Title,
    string Language,
    string Status,
    string FileName,
    long ByteLength,
    string Sha256,
    string? NormalizerVersion,
    DateTimeOffset? NormalizedAt,
    IReadOnlyList<DocumentIssue> Issues,
    IReadOnlyList<DocumentImageModel> Images,
    IReadOnlyList<DocumentOutputModel> Outputs,
    bool CanGenerateOutputs,
    long Version);

public sealed record DocumentImageModel(
    Guid Id,
    int Sequence,
    string RelationshipId,
    string SourceLocation,
    string? SuggestedAltText,
    bool SuggestedDecorative,
    string? AltText,
    bool IsDecorative,
    string ReviewStatus,
    Guid? ReviewedByUserId,
    DateTimeOffset? ReviewedAt,
    long Version);

public sealed record DocumentOutputModel(
    Guid Id,
    string Kind,
    string Status,
    string Label,
    long SourceRevision,
    string GeneratorVersion,
    string? ValidatorName,
    string? ValidatorVersion,
    string? ValidationProfile,
    DateTimeOffset? ValidatedAt,
    bool CanDownload,
    bool HasValidationReport,
    long Version);

public sealed record DocumentDownloadModel(
    ObjectStorageReadResult Resource,
    string FileName) : IAsyncDisposable
{
    public Stream Content => Resource.Content;

    public string ContentType => Resource.ContentType;

    public long ByteLength => Resource.ByteLength;

    public string? ETag => Resource.ETag;

    public ValueTask DisposeAsync() => Resource.DisposeAsync();
}

public sealed record DocumentPreviewModel(
    string Content,
    string ContentType,
    IReadOnlyList<DocumentIssue> Issues);

public sealed record NormalizeDocumentPayload(
    Guid DocumentId,
    Guid SourceFileId,
    string SourceSha256,
    string NormalizerVersion);

public sealed record GenerateDocumentOutputsPayload(
    Guid DocumentId,
    long SourceRevision,
    Guid NormalizedModelFileId,
    string PipelineVersion,
    IReadOnlyList<string> Formats);
