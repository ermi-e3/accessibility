using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;
using Accessibility.Application.Features.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using W = DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;

namespace Accessibility.Infrastructure.Documents;

public sealed partial class OpenXmlDocumentNormalizer : IOpenXmlDocumentNormalizer
{
    private const int MaximumSchemaErrors = 50;
    private const int MaximumImageBytes = 10 * 1024 * 1024;
    private const int MaximumImageOccurrences = 100;
    private const long MaximumAggregateImagePayloadBytes = 25L * 1024 * 1024;
    private const long MaximumImagePixels = 100_000_000;
    private static readonly Regex HeadingStylePattern = HeadingRegex();

    public string Version => "openxml-3.5.1-normalizer-v1";

    public NormalizedDocumentModel Normalize(
        Stream package,
        DocumentNormalizationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(options);
        if (!package.CanRead || !package.CanSeek)
        {
            throw new DocumentTransformationException("DOCX_STREAM_INVALID", "The DOCX source must be readable and seekable.");
        }

        package.Position = 0;
        try
        {
            using var document = WordprocessingDocument.Open(package, false, new OpenSettings
            {
                AutoSave = false,
                MaxCharactersInPart = 10L * 1024 * 1024
            });
            var main = document.MainDocumentPart
                ?? throw new DocumentTransformationException("DOCX_MAIN_PART_MISSING", "The DOCX main document part is missing.");
            var body = main.Document?.Body
                ?? throw new DocumentTransformationException("DOCX_BODY_MISSING", "The DOCX document body is missing.");
            var issues = ValidateSchema(document, cancellationToken);
            var language = ResolveDocumentLanguage(document, options.DefaultLanguage, issues);
            var state = new NormalizationState(main, language, issues, cancellationToken);
            AddUnsupportedPartIssues(main, state);
            var blocks = ParseBlocks(body.ChildElements, state, "body");
            CheckHeadingHierarchy(blocks, issues);
            if (blocks.Count == 0)
            {
                issues.Add(Issue("DOCUMENT_EMPTY", DocumentIssueSeverity.Blocking,
                    "The document does not contain supported body content.", "body"));
            }

            var title = string.IsNullOrWhiteSpace(options.Title)
                ? document.PackageProperties.Title?.Trim()
                : options.Title.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "Untitled document";
                issues.Add(Issue("TITLE_MISSING", DocumentIssueSeverity.RequiresReview,
                    "A document title was not supplied.", "document/title"));
            }

            return new NormalizedDocumentModel(
                DocumentModelVersions.Current,
                Version,
                title,
                language,
                blocks,
                state.Images,
                issues.OrderBy(issue => issue.Order).ToArray());
        }
        catch (DocumentTransformationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException or IOException)
        {
            throw new DocumentTransformationException(
                "DOCX_NORMALIZATION_FAILED",
                "The DOCX could not be normalized safely.",
                exception);
        }
        finally
        {
            package.Position = 0;
        }
    }

    private static List<DocumentBlock> ParseBlocks(
        OpenXmlElementList elements,
        NormalizationState state,
        string location)
    {
        var blocks = new List<DocumentBlock>();
        for (var index = 0; index < elements.Count; index++)
        {
            var element = elements[index];
            state.CancellationToken.ThrowIfCancellationRequested();
            var source = $"{location}/{element.LocalName}[{blocks.Count + 1}]";
            switch (element)
            {
                case W.Paragraph paragraph when TryListMetadata(paragraph, state.Main, out _):
                    blocks.Add(ParseList(elements, index, blocks.Count, source, state, out var consumed));
                    index += consumed - 1;
                    break;
                case W.Paragraph paragraph:
                    blocks.Add(ParseParagraph(paragraph, blocks.Count, source, state));
                    break;
                case W.Table table:
                    blocks.Add(ParseTable(table, blocks.Count, source, state));
                    break;
                case W.AltChunk:
                    state.AddIssue("ALT_CHUNK_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        "Imported external HTML or alternative chunks are not supported.", source);
                    break;
                case W.SdtBlock:
                    state.AddIssue("CONTENT_CONTROL_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        "Block content controls must be removed before processing.", source);
                    break;
                case W.SectionProperties or W.TableCellProperties:
                    break;
                case W.BookmarkStart or W.BookmarkEnd or W.ProofError or W.PermStart or W.PermEnd:
                    break;
                default:
                    state.AddIssue("BLOCK_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        $"The document contains an unsupported block '{element.LocalName}'.", source);
                    break;
            }
        }

        return blocks;
    }

    private static DocumentBlock ParseParagraph(
        W.Paragraph paragraph,
        int order,
        string location,
        NormalizationState state)
    {
        if (paragraph.Descendants<W.TextBoxContent>().Any() ||
            paragraph.Descendants<DocumentFormat.OpenXml.Math.OfficeMath>().Any())
        {
            state.AddIssue("PARAGRAPH_COMPLEX_CONTENT", DocumentIssueSeverity.Blocking,
                "Text boxes and equations are outside the supported DOCX subset.", location);
        }
        if (paragraph.Descendants<W.InsertedRun>().Any() || paragraph.Descendants<W.DeletedRun>().Any())
        {
            state.AddIssue("TRACKED_CHANGES_PRESENT", DocumentIssueSeverity.Blocking,
                "Tracked changes must be accepted or rejected before processing.", location);
        }

        var content = ParseInlines(paragraph.ChildElements, state, location);
        var headingLevel = ResolveHeadingLevel(paragraph, state.Main);
        return headingLevel is >= 1 and <= 6
            ? new DocumentHeadingBlock(order, location, headingLevel.Value, content)
            : new DocumentParagraphBlock(order, location, content);
    }

    private static DocumentListBlock ParseList(
        OpenXmlElementList elements,
        int startIndex,
        int order,
        string location,
        NormalizationState state,
        out int consumed)
    {
        var first = (W.Paragraph)elements[startIndex];
        _ = TryListMetadata(first, state.Main, out var firstMetadata);
        var root = new ListBuilder(
            firstMetadata.Marker,
            firstMetadata.Start,
            order,
            location,
            firstMetadata.Level,
            firstMetadata.NumberId);
        var stack = new Stack<ListBuilder>();
        stack.Push(root);
        consumed = 0;
        for (var index = startIndex; index < elements.Count; index++)
        {
            if (elements[index] is not W.Paragraph paragraph ||
                !TryListMetadata(paragraph, state.Main, out var metadata) ||
                metadata.Level < root.Level ||
                metadata.Level == root.Level && metadata.NumberId != root.NumberId)
            {
                break;
            }
            if (!metadata.MarkerSupported)
            {
                state.AddIssue("LIST_MARKER_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                    "The list uses an unsupported or unresolved numbering format.",
                    $"{location}/item[{consumed + 1}]");
            }

            while (stack.Count > 1 && metadata.Level < stack.Peek().Level)
            {
                stack.Pop();
            }
            if (metadata.Level > stack.Peek().Level)
            {
                var parent = stack.Peek();
                if (parent.Items.Count == 0 || metadata.Level != parent.Level + 1)
                {
                    state.AddIssue("LIST_NESTING_INVALID", DocumentIssueSeverity.Blocking,
                        "List nesting skips a level or has no parent item.",
                        $"{location}/item[{consumed + 1}]");
                    break;
                }
                var nested = new ListBuilder(
                    metadata.Marker,
                    metadata.Start,
                    parent.Items[^1].Nested.Count,
                    $"{location}/item[{parent.Items.Count}]/list[{parent.Items[^1].Nested.Count + 1}]",
                    metadata.Level,
                    metadata.NumberId);
                parent.Items[^1].Nested.Add(nested);
                stack.Push(nested);
            }
            else if (metadata.Level == stack.Peek().Level && metadata.NumberId != stack.Peek().NumberId)
            {
                break;
            }

            var current = stack.Peek();
            current.Items.Add(new ListItemBuilder(
                ParseInlines(paragraph.ChildElements, state, $"{current.Location}/item[{current.Items.Count + 1}]"),
                $"{current.Location}/item[{current.Items.Count + 1}]"));
            consumed++;
        }

        return root.ToModel();
    }

    private static bool TryListMetadata(
        W.Paragraph paragraph,
        MainDocumentPart main,
        out ListMetadata metadata)
    {
        var numbering = paragraph.ParagraphProperties?.NumberingProperties;
        if (numbering?.NumberingId?.Val?.Value is not int numberId)
        {
            metadata = default;
            return false;
        }
        var level = numbering.NumberingLevelReference?.Val?.Value ?? 0;
        var marker = ResolveListMarker(main, numberId, level, out var markerSupported);
        metadata = new ListMetadata(
            numberId,
            level,
            marker,
            ResolveListStart(main, numberId, level),
            markerSupported);
        return true;
    }

    private static IReadOnlyList<DocumentInline> ParseInlines(
        OpenXmlElementList elements,
        NormalizationState state,
        string location)
    {
        var result = new List<DocumentInline>();
        foreach (var element in elements)
        {
            switch (element)
            {
                case W.Run run:
                    ParseRun(run, result, state, $"{location}/run[{result.Count + 1}]");
                    break;
                case W.Hyperlink hyperlink:
                    ParseHyperlink(hyperlink, result, state, $"{location}/link[{result.Count + 1}]");
                    break;
                case W.SimpleField field:
                    state.AddIssue("FIELD_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        "Dynamic fields must be replaced with static reviewed text.", location);
                    ParseRunContent(field.Descendants<W.Run>(), result, state, location);
                    break;
                case W.ParagraphProperties or W.BookmarkStart or W.BookmarkEnd or W.ProofError:
                    break;
                default:
                    if (element.Descendants<W.Run>().Any())
                    {
                        state.AddIssue("INLINE_CONTAINER_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                            $"The inline container '{element.LocalName}' is not supported.", location);
                        ParseRunContent(element.Descendants<W.Run>(), result, state, location);
                    }
                    break;
            }
        }

        return result;
    }

    private static void ParseRun(
        W.Run run,
        ICollection<DocumentInline> output,
        NormalizationState state,
        string location)
    {
        var language = ResolveRunLanguage(run, state.DefaultLanguage);
        var bold = run.RunProperties?.Bold is not null && run.RunProperties.Bold.Val?.Value is not false;
        var italic = run.RunProperties?.Italic is not null && run.RunProperties.Italic.Val?.Value is not false;
        foreach (var child in run.ChildElements)
        {
            switch (child)
            {
                case W.Text text:
                    if (!string.IsNullOrEmpty(text.Text))
                    {
                        output.Add(new DocumentTextInline(output.Count, text.Text, language, bold, italic));
                    }
                    break;
                case W.TabChar:
                    output.Add(new DocumentTextInline(output.Count, "\t", language, bold, italic));
                    break;
                case W.Break:
                case W.CarriageReturn:
                    output.Add(new DocumentTextInline(output.Count, "\n", language, bold, italic));
                    break;
                case W.Drawing drawing:
                    ParseDrawing(drawing, output, state, location);
                    break;
                case W.RunProperties or W.LastRenderedPageBreak:
                    break;
                case W.FootnoteReference or W.EndnoteReference:
                    state.AddIssue("NOTE_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        "Footnotes and endnotes are outside the supported DOCX subset.", location);
                    break;
                default:
                    state.AddIssue("RUN_CONTENT_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        $"The run contains unsupported content '{child.LocalName}'.", location);
                    foreach (var descendantText in child.Descendants<W.Text>())
                    {
                        if (!string.IsNullOrEmpty(descendantText.Text))
                        {
                            output.Add(new DocumentTextInline(
                                output.Count,
                                descendantText.Text,
                                language,
                                bold,
                                italic));
                        }
                    }
                    break;
            }
        }
    }

    private static void ParseRunContent(
        IEnumerable<W.Run> runs,
        ICollection<DocumentInline> output,
        NormalizationState state,
        string location)
    {
        foreach (var run in runs)
        {
            ParseRun(run, output, state, location);
        }
    }

    private static void ParseHyperlink(
        W.Hyperlink hyperlink,
        ICollection<DocumentInline> output,
        NormalizationState state,
        string location)
    {
        var content = new List<DocumentInline>();
        if (hyperlink.ChildElements.Any(child => child is not W.Run))
        {
            state.AddIssue("LINK_CONTAINER_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "The hyperlink contains an unsupported nested container.", location);
        }
        ParseRunContent(hyperlink.Descendants<W.Run>(), content, state, location);
        var text = content.OfType<DocumentTextInline>().ToArray();
        if (text.Length == 0)
        {
            state.AddIssue("LINK_TEXT_MISSING", DocumentIssueSeverity.Blocking,
                "A hyperlink must contain meaningful text.", location);
            return;
        }
        if (content.Any(item => item is not DocumentTextInline))
        {
            state.AddIssue("LINK_COMPLEX_CONTENT_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Images and other complex content inside hyperlinks require source remediation.", location);
            AppendPlainInlines(content, output);
            return;
        }

        string? href = null;
        if (!string.IsNullOrWhiteSpace(hyperlink.Anchor?.Value))
        {
            state.AddIssue("INTERNAL_LINK_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Internal bookmark hyperlinks are not supported in the v1 normalized model.", location);
            AppendPlainInlines(content, output);
            return;
        }
        else if (!string.IsNullOrWhiteSpace(hyperlink.Id?.Value))
        {
            var relationship = state.Main.HyperlinkRelationships.SingleOrDefault(item => item.Id == hyperlink.Id.Value);
            if (relationship is not null && DocumentLinkPolicy.TryNormalizeExternalHref(relationship.Uri.AbsoluteUri, out var normalized))
            {
                href = normalized;
            }
        }

        if (href is null)
        {
            state.AddIssue("LINK_UNSAFE_OR_BROKEN", DocumentIssueSeverity.Blocking,
                "A hyperlink is broken or uses an unsafe destination.", location);
            AppendPlainInlines(content, output);
            return;
        }

        output.Add(new DocumentLinkInline(output.Count, href, text));
    }

    private static void AppendPlainInlines(
        IEnumerable<DocumentInline> content,
        ICollection<DocumentInline> output)
    {
        foreach (var item in content.OrderBy(item => item.Order))
        {
            output.Add(item switch
            {
                DocumentTextInline text => text with { Order = output.Count },
                DocumentImageInline image => image with { Order = output.Count },
                DocumentLinkInline link => link with { Order = output.Count },
                _ => throw new DocumentTransformationException(
                    "INLINE_KIND_UNSUPPORTED",
                    "The normalized inline kind is unsupported.")
            });
        }
    }

    private static void ParseDrawing(
        W.Drawing drawing,
        ICollection<DocumentInline> output,
        NormalizationState state,
        string location)
    {
        if (drawing.Descendants<DW.Anchor>().Any())
        {
            state.AddIssue("FLOATING_IMAGE_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Floating images must be converted to inline images.", location);
            return;
        }

        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        var relationshipId = blip?.Embed?.Value;
        if (string.IsNullOrWhiteSpace(relationshipId))
        {
            state.AddIssue("IMAGE_RELATIONSHIP_MISSING", DocumentIssueSeverity.Blocking,
                "An image is missing its embedded relationship.", location);
            return;
        }

        if (state.Images.Count >= MaximumImageOccurrences)
        {
            state.AddIssue("IMAGE_OCCURRENCE_LIMIT", DocumentIssueSeverity.Blocking,
                "The document contains more image occurrences than supported.", location);
            return;
        }

        if (state.Main.GetPartById(relationshipId) is not ImagePart imagePart ||
            imagePart.ContentType is not ("image/png" or "image/jpeg"))
        {
            state.AddIssue("IMAGE_FORMAT_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Only embedded PNG and JPEG images are supported.", location);
            return;
        }

        if (!state.ImageCache.TryGetValue(relationshipId, out var cached))
        {
            using var stream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            byte[] payload;
            try
            {
                payload = ReadBoundedImage(stream);
            }
            catch (DocumentTransformationException exception)
            {
                state.AddIssue(exception.Code, DocumentIssueSeverity.Blocking, exception.Message, location);
                return;
            }
            if (!ValidateImage(payload, imagePart.ContentType, out var decodedPixels) || decodedPixels > MaximumImagePixels)
            {
                state.AddIssue("IMAGE_CONTENT_INVALID", DocumentIssueSeverity.Blocking,
                    "An embedded image is malformed or has unsafe dimensions.", location);
                return;
            }
            cached = new CachedImage(imagePart.ContentType, payload, decodedPixels);
            state.ImageCache[relationshipId] = cached;
        }
        if (state.TotalImagePixels > MaximumImagePixels - cached.Pixels ||
            state.TotalImagePayloadBytes > MaximumAggregateImagePayloadBytes - cached.Payload.LongLength)
        {
            state.AddIssue("IMAGE_CONTENT_INVALID", DocumentIssueSeverity.Blocking,
                "The aggregate image occurrences exceed the supported resource limits.", location);
            return;
        }
        state.TotalImagePixels += cached.Pixels;
        state.TotalImagePayloadBytes += cached.Payload.LongLength;

        var properties = drawing.Descendants<DW.DocProperties>().FirstOrDefault();
        var suggestion = string.IsNullOrWhiteSpace(properties?.Description?.Value)
            ? properties?.Title?.Value
            : properties.Description.Value;
        var id = $"image-{state.Images.Count + 1}";
        state.Images.Add(new NormalizedDocumentImage(
            state.Images.Count,
            id,
            relationshipId,
            location,
            cached.MediaType,
            cached.Payload,
            suggestion,
            SuggestedDecorative: false));
        output.Add(new DocumentImageInline(output.Count, id));
    }

    private static DocumentTableBlock ParseTable(
        W.Table table,
        int order,
        string location,
        NormalizationState state)
    {
        if (table.Descendants<W.Table>().Any(nested => !ReferenceEquals(nested, table)))
        {
            state.AddIssue("NESTED_TABLE_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Nested tables are outside the supported DOCX subset.", location);
        }

        var rows = new List<DocumentTableRow>();
        foreach (var row in table.Elements<W.TableRow>())
        {
            var isHeader = row.TableRowProperties?.Elements<W.TableHeader>().Any() == true;
            var cells = new List<DocumentTableCell>();
            foreach (var cell in row.Elements<W.TableCell>())
            {
                if (cell.TableCellProperties?.GridSpan is not null || cell.TableCellProperties?.VerticalMerge is not null)
                {
                    state.AddIssue("MERGED_TABLE_CELL_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                        "Merged table cells require source remediation.", $"{location}/row[{rows.Count + 1}]/cell[{cells.Count + 1}]");
                }
                var cellBlocks = ParseBlocks(
                    cell.ChildElements,
                    state,
                    $"{location}/row[{rows.Count + 1}]/cell[{cells.Count + 1}]");
                cells.Add(new DocumentTableCell(
                    cells.Count,
                    $"{location}/row[{rows.Count + 1}]/cell[{cells.Count + 1}]",
                    isHeader ? DocumentTableHeaderScope.Column : DocumentTableHeaderScope.None,
                    cellBlocks));
            }
            rows.Add(new DocumentTableRow(rows.Count, $"{location}/row[{rows.Count + 1}]", isHeader, cells));
        }
        if (rows.Count == 0 || rows.All(row => !row.IsHeader))
        {
            state.AddIssue("TABLE_HEADER_MISSING", DocumentIssueSeverity.Blocking,
                "A supported table requires an explicitly marked header row.", location);
        }

        var caption = table.TableProperties?.TableCaption?.Val?.Value;
        return new DocumentTableBlock(order, location, caption, rows);
    }

    private static List<DocumentIssue> ValidateSchema(WordprocessingDocument document, CancellationToken cancellationToken)
    {
        var issues = new List<DocumentIssue>();
        try
        {
            var validator = new OpenXmlValidator(FileFormatVersions.Microsoft365);
            foreach (var error in validator.Validate(document).Take(MaximumSchemaErrors + 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (issues.Count == MaximumSchemaErrors)
                {
                    issues.Add(Issue("OPENXML_ERROR_LIMIT", DocumentIssueSeverity.Blocking,
                        "The DOCX contains more schema errors than can be reported safely.", "package"));
                    break;
                }
                issues.Add(Issue("OPENXML_SCHEMA_ERROR", DocumentIssueSeverity.Blocking,
                    error.Description ?? "The DOCX contains invalid Open XML.",
                    error.Path?.XPath ?? error.Part?.Uri.ToString() ?? "package"));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or OpenXmlPackageException)
        {
            issues.Add(Issue("OPENXML_VALIDATION_FAILED", DocumentIssueSeverity.Blocking,
                "The DOCX schema could not be validated.", "package"));
        }
        return issues;
    }

    private static void AddUnsupportedPartIssues(MainDocumentPart main, NormalizationState state)
    {
        if (main.ChartParts.Any())
        {
            state.AddIssue("CHART_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Charts must be replaced with reviewed supported content.", "package/charts");
        }
        if (main.FootnotesPart is not null || main.EndnotesPart is not null)
        {
            state.AddIssue("NOTES_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Footnotes and endnotes are outside the supported DOCX subset.", "package/notes");
        }
        if (main.WordprocessingCommentsPart is not null)
        {
            state.AddIssue("COMMENTS_PRESENT", DocumentIssueSeverity.Blocking,
                "Comments must be resolved before processing.", "package/comments");
        }
        if (main.HeaderParts.Any() || main.FooterParts.Any())
        {
            state.AddIssue("HEADER_FOOTER_UNSUPPORTED", DocumentIssueSeverity.Blocking,
                "Headers and footers require source remediation for the v1 subset.", "package/header-footer");
        }
    }

    private static string ResolveDocumentLanguage(
        WordprocessingDocument document,
        string? requested,
        ICollection<DocumentIssue> issues)
    {
        var candidate = requested;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = document.PackageProperties.Language;
        }
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = document.MainDocumentPart?.StyleDefinitionsPart?.Styles?
                .DocDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle?.Languages?.Val?.Value;
        }
        if (!DocumentLanguagePolicy.IsValid(candidate))
        {
            issues.Add(Issue("DOCUMENT_LANGUAGE_MISSING", DocumentIssueSeverity.Blocking,
                "A valid document language is required.", "document/language"));
            return "und";
        }

        return DocumentLanguagePolicy.Normalize(candidate!);
    }

    private static string ResolveRunLanguage(W.Run run, string defaultLanguage)
    {
        var language = run.RunProperties?.Languages?.Val?.Value ??
                       run.RunProperties?.Languages?.EastAsia?.Value ??
                       run.RunProperties?.Languages?.Bidi?.Value;
        return DocumentLanguagePolicy.IsValid(language)
            ? DocumentLanguagePolicy.Normalize(language!)
            : defaultLanguage;
    }

    private static int? ResolveHeadingLevel(W.Paragraph paragraph, MainDocumentPart main)
    {
        var outline = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value;
        if (outline is >= 0 and <= 5)
        {
            return outline.Value + 1;
        }
        var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!string.IsNullOrWhiteSpace(styleId) && visited.Add(styleId))
        {
            var match = HeadingStylePattern.Match(styleId);
            if (TryHeadingLevel(match, out var level))
            {
                return level;
            }
            var style = main.StyleDefinitionsPart?.Styles?.Elements<W.Style>()
                .SingleOrDefault(item => string.Equals(item.StyleId?.Value, styleId, StringComparison.OrdinalIgnoreCase));
            var nameMatch = HeadingStylePattern.Match(style?.StyleName?.Val?.Value ?? string.Empty);
            if (TryHeadingLevel(nameMatch, out level))
            {
                return level;
            }
            styleId = style?.BasedOn?.Val?.Value;
        }
        return null;
    }

    private static bool TryHeadingLevel(Match match, out int level)
    {
        level = 0;
        var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return match.Success && int.TryParse(value, CultureInfo.InvariantCulture, out level);
    }

    private static DocumentListMarker ResolveListMarker(
        MainDocumentPart main,
        int numberId,
        int level,
        out bool supported)
    {
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        var instance = numbering?.Elements<W.NumberingInstance>()
            .SingleOrDefault(item => item.NumberID?.Value == numberId);
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        var format = numbering?.Elements<W.AbstractNum>()
            .SingleOrDefault(item => item.AbstractNumberId?.Value == abstractId)?
            .Elements<W.Level>().SingleOrDefault(item => item.LevelIndex?.Value == level)?
            .NumberingFormat?.Val?.Value;
        supported = true;
        if (format == W.NumberFormatValues.Bullet) return DocumentListMarker.Bullet;
        if (format == W.NumberFormatValues.Decimal) return DocumentListMarker.Decimal;
        if (format == W.NumberFormatValues.LowerLetter) return DocumentListMarker.LowerLetter;
        if (format == W.NumberFormatValues.UpperLetter) return DocumentListMarker.UpperLetter;
        if (format == W.NumberFormatValues.LowerRoman) return DocumentListMarker.LowerRoman;
        if (format == W.NumberFormatValues.UpperRoman) return DocumentListMarker.UpperRoman;
        supported = false;
        return DocumentListMarker.Decimal;
    }

    private static int ResolveListStart(MainDocumentPart main, int numberId, int level)
    {
        var numbering = main.NumberingDefinitionsPart?.Numbering;
        var instance = numbering?.Elements<W.NumberingInstance>()
            .SingleOrDefault(item => item.NumberID?.Value == numberId);
        var overrideStart = instance?.Elements<W.LevelOverride>()
            .SingleOrDefault(item => item.LevelIndex?.Value == level)?.StartOverrideNumberingValue?.Val?.Value;
        if (overrideStart is > 0)
        {
            return overrideStart.Value;
        }
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        return numbering?.Elements<W.AbstractNum>()
                   .SingleOrDefault(item => item.AbstractNumberId?.Value == abstractId)?
                   .Elements<W.Level>().SingleOrDefault(item => item.LevelIndex?.Value == level)?
                   .StartNumberingValue?.Val?.Value ?? 1;
    }

    private static void CheckHeadingHierarchy(IEnumerable<DocumentBlock> blocks, ICollection<DocumentIssue> issues)
    {
        var previous = 0;
        foreach (var heading in blocks.OfType<DocumentHeadingBlock>())
        {
            if (previous == 0 && heading.Level != 1 || previous > 0 && heading.Level > previous + 1)
            {
                issues.Add(Issue("HEADING_HIERARCHY_GAP", DocumentIssueSeverity.Blocking,
                    "The heading hierarchy contains a skipped level.", heading.SourceLocation));
            }
            previous = heading.Level;
        }
    }

    private static bool ValidateImage(byte[] payload, string contentType, out long pixels)
    {
        pixels = 0;
        if (contentType == "image/png" && payload.Length >= 24 &&
            payload[0] == 137 && payload[1] == 80 && payload[2] == 78 && payload[3] == 71 &&
            payload[4] == 13 && payload[5] == 10 && payload[6] == 26 && payload[7] == 10)
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(20, 4));
            pixels = checked((long)width * height);
            return width > 0 && height > 0;
        }
        if (contentType == "image/jpeg" && payload.Length >= 4 && payload[0] == 0xFF && payload[1] == 0xD8)
        {
            var index = 2;
            while (index + 8 < payload.Length)
            {
                if (payload[index++] != 0xFF)
                {
                    continue;
                }
                var marker = payload[index++];
                if (marker is 0xD8 or 0xD9)
                {
                    continue;
                }
                if (index + 2 > payload.Length)
                {
                    return false;
                }
                var length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(index, 2));
                if (length < 2 || index + length > payload.Length)
                {
                    return false;
                }
                if (marker is >= 0xC0 and <= 0xC3 && length >= 7)
                {
                    var height = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(index + 3, 2));
                    var width = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(index + 5, 2));
                    pixels = checked((long)width * height);
                    return width > 0 && height > 0;
                }
                index += length;
            }
        }
        return false;
    }

    private static byte[] ReadBoundedImage(Stream stream)
    {
        using var content = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (content.Length + read > MaximumImageBytes)
            {
                throw new DocumentTransformationException(
                    "IMAGE_SIZE_INVALID",
                    "An embedded image exceeds the supported size.");
            }
            content.Write(buffer, 0, read);
        }
        if (content.Length == 0)
        {
            throw new DocumentTransformationException("IMAGE_SIZE_INVALID", "An embedded image is empty.");
        }
        return content.ToArray();
    }

    private static DocumentIssue Issue(string code, DocumentIssueSeverity severity, string message, string source) =>
        new(0, code, severity, message, source);

    [GeneratedRegex(@"(?:^|\s)heading\s*([1-6])$|^Heading([1-6])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadingRegex();

    private sealed class NormalizationState(
        MainDocumentPart main,
        string defaultLanguage,
        List<DocumentIssue> issues,
        CancellationToken cancellationToken)
    {
        public MainDocumentPart Main { get; } = main;

        public string DefaultLanguage { get; } = defaultLanguage;

        public List<NormalizedDocumentImage> Images { get; } = [];

        public Dictionary<string, CachedImage> ImageCache { get; } = new(StringComparer.Ordinal);

        public long TotalImagePixels { get; set; }

        public long TotalImagePayloadBytes { get; set; }

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public void AddIssue(string code, DocumentIssueSeverity severity, string message, string location)
        {
            issues.Add(new DocumentIssue(issues.Count, code, severity, message, location));
        }
    }

    private readonly record struct ListMetadata(
        int NumberId,
        int Level,
        DocumentListMarker Marker,
        int Start,
        bool MarkerSupported);

    private sealed record CachedImage(string MediaType, byte[] Payload, long Pixels);

    private sealed class ListBuilder(
        DocumentListMarker marker,
        int start,
        int order,
        string location,
        int level,
        int numberId)
    {
        public DocumentListMarker Marker { get; } = marker;
        public int Start { get; } = start;
        public int Order { get; } = order;
        public string Location { get; } = location;
        public int Level { get; } = level;
        public int NumberId { get; } = numberId;
        public List<ListItemBuilder> Items { get; } = [];

        public DocumentListBlock ToModel() => new(
            Order,
            Location,
            Marker,
            Start,
            Items.Select((item, index) => new DocumentListItem(
                index,
                item.Location,
                item.Content,
                item.Nested.Select(nested => nested.ToModel()).ToArray())).ToArray());
    }

    private sealed class ListItemBuilder(
        IReadOnlyList<DocumentInline> content,
        string location)
    {
        public IReadOnlyList<DocumentInline> Content { get; } = content;
        public string Location { get; } = location;
        public List<ListBuilder> Nested { get; } = [];
    }
}
