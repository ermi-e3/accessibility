using System.Text.Json;
using System.Text.Json.Serialization;

namespace Accessibility.Application.Features.Documents;

public static class DocumentModelVersions
{
    public const string Current = "1.0";
}

public enum DocumentIssueSeverity
{
    Information,
    Warning,
    RequiresReview,
    Blocking
}

public sealed record DocumentIssue(
    int Order,
    string Code,
    DocumentIssueSeverity Severity,
    string Message,
    string SourceLocation);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DocumentHeadingBlock), "heading")]
[JsonDerivedType(typeof(DocumentParagraphBlock), "paragraph")]
[JsonDerivedType(typeof(DocumentListBlock), "list")]
[JsonDerivedType(typeof(DocumentTableBlock), "table")]
public abstract record DocumentBlock(int Order, string SourceLocation);

public sealed record DocumentHeadingBlock(
    int Order,
    string SourceLocation,
    int Level,
    IReadOnlyList<DocumentInline> Content)
    : DocumentBlock(Order, SourceLocation);

public sealed record DocumentParagraphBlock(
    int Order,
    string SourceLocation,
    IReadOnlyList<DocumentInline> Content)
    : DocumentBlock(Order, SourceLocation);

public enum DocumentListMarker
{
    Bullet,
    Decimal,
    LowerLetter,
    UpperLetter,
    LowerRoman,
    UpperRoman
}

public sealed record DocumentListBlock(
    int Order,
    string SourceLocation,
    DocumentListMarker Marker,
    int Start,
    IReadOnlyList<DocumentListItem> Items)
    : DocumentBlock(Order, SourceLocation)
{
    [JsonIgnore]
    public bool IsOrdered => Marker is not DocumentListMarker.Bullet;
}

public sealed record DocumentListItem(
    int Order,
    string SourceLocation,
    IReadOnlyList<DocumentInline> Content,
    IReadOnlyList<DocumentListBlock> NestedLists);

public sealed record DocumentTableBlock(
    int Order,
    string SourceLocation,
    string? Caption,
    IReadOnlyList<DocumentTableRow> Rows)
    : DocumentBlock(Order, SourceLocation);

public sealed record DocumentTableRow(
    int Order,
    string SourceLocation,
    bool IsHeader,
    IReadOnlyList<DocumentTableCell> Cells);

public enum DocumentTableHeaderScope
{
    None,
    Column,
    Row
}

public sealed record DocumentTableCell(
    int Order,
    string SourceLocation,
    DocumentTableHeaderScope HeaderScope,
    IReadOnlyList<DocumentBlock> Content);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DocumentTextInline), "text")]
[JsonDerivedType(typeof(DocumentLinkInline), "link")]
[JsonDerivedType(typeof(DocumentImageInline), "image")]
public abstract record DocumentInline(int Order);

public sealed record DocumentTextInline(
    int Order,
    string Text,
    string Language,
    bool Bold = false,
    bool Italic = false)
    : DocumentInline(Order);

public sealed record DocumentLinkInline(
    int Order,
    string Href,
    IReadOnlyList<DocumentTextInline> Content)
    : DocumentInline(Order);

public sealed record DocumentImageInline(
    int Order,
    string ImageId)
    : DocumentInline(Order);

public sealed record NormalizedDocumentImage(
    int Order,
    string Id,
    string RelationshipId,
    string SourceLocation,
    string MediaType,
    ReadOnlyMemory<byte> Payload,
    string? SuggestedAltText,
    bool SuggestedDecorative)
{
    // Source metadata is never equivalent to a human review decision.
    [JsonIgnore]
    public bool IsAltTextApproved => false;
}

public sealed record NormalizedDocumentModel(
    string SchemaVersion,
    string NormalizerVersion,
    string Title,
    string Language,
    IReadOnlyList<DocumentBlock> Blocks,
    IReadOnlyList<NormalizedDocumentImage> Images,
    IReadOnlyList<DocumentIssue> Issues)
{
    [JsonIgnore]
    public bool HasBlockingIssues => Issues.Any(issue => issue.Severity == DocumentIssueSeverity.Blocking);

    [JsonIgnore]
    public bool RequiresImageReview => Images.Count > 0;
}

public static class NormalizedDocumentJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize(NormalizedDocumentModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, Options);
    }

    public static NormalizedDocumentModel Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var document = JsonSerializer.Deserialize<NormalizedDocumentModel>(json, Options)
            ?? throw new JsonException("The normalized document is empty.");
        if (!string.Equals(document.SchemaVersion, DocumentModelVersions.Current, StringComparison.Ordinal))
        {
            throw new JsonException($"Unsupported normalized document schema '{document.SchemaVersion}'.");
        }

        return document;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
