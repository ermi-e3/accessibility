namespace Accessibility.Application.Features.Player;

public interface IPlayerPublicationService
{
    Task<TranscriptModel> CreateManualTranscriptAsync(
        Guid lessonId,
        CreateManualTranscriptCommand command,
        CancellationToken cancellationToken);

    Task<TranscriptModel> ApproveTranscriptAsync(
        Guid transcriptId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<PublicationModel> PublishAsync(
        Guid lessonId,
        long expectedVersion,
        CancellationToken cancellationToken);

    Task<PlayerManifestV1> GetManifestAsync(
        Guid lessonId,
        Uri publicApiBase,
        CancellationToken cancellationToken);

    Task<PublicTranscriptV1> GetPublicTranscriptAsync(
        Guid lessonId,
        Guid publicationId,
        string language,
        CancellationToken cancellationToken);
}

public sealed record CreateManualTranscriptCommand(
    string Language,
    IReadOnlyList<TranscriptSegmentInput> Segments);

public sealed record TranscriptSegmentInput(
    int Sequence,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    string? SpeakerLabel);

public sealed record TranscriptModel(
    Guid Id,
    Guid LessonId,
    string Language,
    int Revision,
    string ReviewStatus,
    IReadOnlyList<TranscriptSegmentModel> Segments,
    long Version);

public sealed record TranscriptSegmentModel(
    int Sequence,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text,
    string? SpeakerLabel);

public sealed record PublicationModel(
    Guid Id,
    Guid LessonId,
    int Revision,
    DateTimeOffset PublishedAt,
    string Checksum,
    IReadOnlyList<string> Warnings);

public sealed record PlayerManifestV1(
    string SchemaVersion,
    Guid LessonId,
    Guid PublicationId,
    int Revision,
    string Title,
    string SourceLanguage,
    DateTimeOffset ExpiresAt,
    PlayerMediaV1 Media,
    IReadOnlyList<PlayerCaptionTrackV1> Captions,
    IReadOnlyList<PlayerTranscriptLinkV1> Transcripts,
    PlayerSignLanguageV1? SignLanguage,
    PlayerAudioDescriptionV1? AudioDescription,
    PlayerOfflinePackageV1? OfflinePackage,
    IReadOnlyList<string> Warnings);

public sealed record PlayerMediaV1(
    string Url,
    string ContentType,
    long DurationMilliseconds,
    string? PosterUrl);

public sealed record PlayerCaptionTrackV1(
    string Language,
    string Label,
    string Url,
    bool IsDefault,
    bool IsHumanReviewed);

public sealed record PlayerTranscriptLinkV1(
    string Language,
    string Label,
    string Url,
    bool IsHumanReviewed);

public sealed record PlayerSignLanguageV1(
    string Language,
    string Label,
    string Url,
    long SyncOffsetMilliseconds,
    bool IsHumanReviewed,
    string CreationMethod);

public sealed record PlayerAudioDescriptionV1(
    string Url,
    long SyncOffsetMilliseconds,
    string Mode);

public sealed record PlayerOfflinePackageV1(
    string Profile,
    string Label,
    string Url,
    string ContentType,
    long ByteLength,
    string Sha256);

public sealed record PublicTranscriptV1(
    Guid LessonId,
    Guid PublicationId,
    string Language,
    int Revision,
    IReadOnlyList<TranscriptSegmentModel> Segments);
