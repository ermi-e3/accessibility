using Accessibility.Application.Abstractions;
using Accessibility.Domain.Enums;

namespace Accessibility.Application.Features.Lessons;

public interface ILessonIngestService
{
    Task<LessonModel> CreateAsync(CreateLessonCommand command, CancellationToken cancellationToken);

    Task<LessonPageModel> ListAsync(LessonListQuery query, CancellationToken cancellationToken);

    Task<LessonDetailModel> GetAsync(Guid lessonId, CancellationToken cancellationToken);

    Task EnsureCanManageAsync(Guid lessonId, CancellationToken cancellationToken);

    Task<AssetModel> IngestAssetAsync(
        Guid lessonId,
        AssetIngestCommand command,
        QuarantinedUpload upload,
        CancellationToken cancellationToken);
}

public sealed record CreateLessonCommand(string Title, string? Description, string SourceLanguage);

public sealed record LessonListQuery(LessonStatus? Status, string? Cursor, int Limit = 20);

public sealed record AssetIngestCommand(
    MediaAssetRole Role,
    string? Language,
    long SyncOffsetMilliseconds,
    bool IsHumanReviewed = false,
    string? AccessibilityMode = null,
    bool ReplaceExisting = false,
    long? ExpectedLessonVersion = null);

public sealed record LessonModel(
    Guid Id,
    Guid OwnerUserId,
    string Title,
    string? Description,
    string SourceLanguage,
    string Status,
    long DraftRevision,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LessonDetailModel(LessonModel Lesson, IReadOnlyList<AssetModel> Assets);

public sealed record LessonPageModel(IReadOnlyList<LessonModel> Items, string? NextCursor);

public sealed record AssetModel(
    Guid Id,
    string Role,
    string? Language,
    long SyncOffsetMilliseconds,
    string Status,
    long? DurationMilliseconds,
    int? Width,
    int? Height,
    string? Codec,
    string FileName,
    string ContentType,
    long ByteLength,
    string Sha256,
    DateTimeOffset CreatedAt);
