using System.Text;
using System.Xml;
using System.Xml.Linq;
using Accessibility.Application.Features.Documents;

namespace Accessibility.Infrastructure.Documents;

public sealed class SemanticHtmlDocumentRenderer : ISemanticHtmlDocumentRenderer
{
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private const string ContentSecurityPolicy =
        "default-src 'none'; img-src data:; font-src data:; style-src 'unsafe-inline'; " +
        "base-uri 'none'; form-action 'none'; object-src 'none'; frame-src 'none'";
    private const string Styles =
        "html{font-family:system-ui,sans-serif;line-height:1.6;color:#111;background:#fff}" +
        "body{margin:0}main{max-width:70rem;margin:auto;padding:1rem}" +
        "img{max-width:100%;height:auto}table{border-collapse:collapse;display:block;overflow-x:auto}" +
        "th,td{border:1px solid #666;padding:.45rem;text-align:start}th{background:#eee}" +
        "a{overflow-wrap:anywhere}.document-issues{border:2px solid #9b2226;padding:1rem;background:#fff4f4}" +
        "@media print{main{max-width:none;padding:0}a{color:inherit}}";

    public string Version => "semantic-xhtml-v1";

    public SemanticHtmlArtifact Render(
        NormalizedDocumentModel document,
        IReadOnlyCollection<ReviewedImageDecision>? reviewedImages = null) =>
        RenderCore(document, reviewedImages, preview: false);

    public SemanticHtmlArtifact RenderPreview(
        NormalizedDocumentModel document,
        IReadOnlyCollection<ReviewedImageDecision>? reviewedImages = null) =>
        RenderCore(document, reviewedImages, preview: true);

    private SemanticHtmlArtifact RenderCore(
        NormalizedDocumentModel document,
        IReadOnlyCollection<ReviewedImageDecision>? reviewedImages,
        bool preview)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!preview && document.HasBlockingIssues)
        {
            throw new DocumentTransformationException(
                "DOCUMENT_HAS_BLOCKING_ISSUES",
                "Blocking source issues must be resolved before semantic HTML is generated.");
        }
        if (!DocumentLanguagePolicy.IsValid(document.Language))
        {
            throw new DocumentTransformationException("DOCUMENT_LANGUAGE_INVALID", "The document language is invalid.");
        }

        var reviews = (reviewedImages ?? [])
            .GroupBy(review => review.ImageId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        var missingReviewImages = document.Images.Where(image => !reviews.ContainsKey(image.Id)).ToArray();
        if (preview)
        {
            foreach (var image in missingReviewImages)
            {
                reviews[image.Id] = new ReviewedImageDecision(
                    image.Id,
                    "Unreviewed image; alternative text is required before publication.",
                    false);
            }
        }
        else if (reviews.Count != document.Images.Count || document.Images.Any(image => !reviews.ContainsKey(image.Id)))
        {
            throw new DocumentTransformationException(
                "IMAGE_REVIEW_INCOMPLETE",
                "Every image occurrence requires one reviewed alternative or decorative decision.");
        }

        XNamespace html = XhtmlNamespace;
        var orderedBlocks = document.Blocks.OrderBy(block => block.Order).ToArray();
        var main = new XElement(html + "main");
        if (orderedBlocks.FirstOrDefault() is not DocumentHeadingBlock firstHeading ||
            firstHeading.Level != 1 ||
            !string.Equals(HeadingText(firstHeading), document.Title, StringComparison.OrdinalIgnoreCase))
        {
            main.Add(new XElement(html + "h1", document.Title));
        }
        main.Add(orderedBlocks.Select(block => RenderBlock(block, document, reviews, html)));
        if (preview && (document.Issues.Count > 0 || missingReviewImages.Length > 0))
        {
            var issueItems = document.Issues.OrderBy(issue => issue.Order)
                .Select(issue => $"{issue.Code}: {issue.Message} ({issue.SourceLocation})")
                .Concat(missingReviewImages.Select(image =>
                    $"IMAGE_REVIEW_REQUIRED: Image at {image.SourceLocation} requires a reviewed alternative or decorative decision."));
            main.Add(new XElement(html + "section",
                new XAttribute("class", "document-issues"),
                new XAttribute("role", "alert"),
                new XElement(html + "h2", "Document review issues"),
                new XElement(html + "ul",
                    issueItems.Select(issue => new XElement(html + "li", issue)))));
        }
        var root = new XElement(html + "html",
            new XAttribute("lang", document.Language),
            new XElement(html + "head",
                new XElement(html + "meta", new XAttribute("charset", "utf-8")),
                new XElement(html + "meta",
                    new XAttribute("http-equiv", "Content-Security-Policy"),
                    new XAttribute("content", ContentSecurityPolicy)),
                new XElement(html + "meta",
                    new XAttribute("name", "viewport"),
                    new XAttribute("content", "width=device-width, initial-scale=1")),
                new XElement(html + "title", document.Title),
                new XElement(html + "style", Styles)),
            new XElement(html + "body", main));
        var output = new XDocument(new XDeclaration("1.0", "utf-8", null), root)
            .ToString(SaveOptions.DisableFormatting);
        var validation = Validate(output);
        if (!validation.IsValid)
        {
            throw new DocumentTransformationException(
                "SEMANTIC_HTML_INVALID",
                "Generated semantic HTML failed artifact validation.");
        }

        return new SemanticHtmlArtifact("text/html; charset=utf-8", ".html", output, validation);
    }

    public DocumentArtifactValidationResult Validate(string xhtml)
    {
        var issues = new List<DocumentIssue>();
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xhtml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16L * 1024 * 1024
            });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (Exception exception) when (exception is XmlException or ArgumentException)
        {
            return new DocumentArtifactValidationResult(
                [Issue(0, "HTML_NOT_WELL_FORMED", "Generated HTML is not well formed.", "html")]);
        }

        XNamespace html = XhtmlNamespace;
        var root = document.Root;
        if (root?.Name != html + "html" || !DocumentLanguagePolicy.IsValid((string?)root.Attribute("lang")))
        {
            issues.Add(Issue(issues.Count, "HTML_LANGUAGE_INVALID", "The HTML root requires a valid language.", "html"));
        }
        if (root?.Descendants(html + "main").Count() != 1)
        {
            issues.Add(Issue(issues.Count, "HTML_MAIN_INVALID", "The HTML requires exactly one main landmark.", "html/body"));
        }

        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "script", "iframe", "object", "embed", "form", "input", "button", "base", "svg"
        };
        foreach (var element in root?.DescendantsAndSelf() ?? [])
        {
            if (forbidden.Contains(element.Name.LocalName) ||
                element.Attributes().Any(attribute => attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(Issue(issues.Count, "HTML_FORBIDDEN_NODE", "The HTML contains a forbidden node or attribute.", ElementPath(element)));
            }
            if (element.Attribute("lang") is { } language && !DocumentLanguagePolicy.IsValid(language.Value))
            {
                issues.Add(Issue(issues.Count, "HTML_REGION_LANGUAGE_INVALID", "A language region is invalid.", ElementPath(element)));
            }
        }

        foreach (var link in root?.Descendants(html + "a") ?? [])
        {
            var href = (string?)link.Attribute("href");
            if (string.IsNullOrWhiteSpace(href) ||
                !(href.StartsWith('#') && href.Length > 1) &&
                !DocumentLinkPolicy.TryNormalizeExternalHref(href, out _))
            {
                issues.Add(Issue(issues.Count, "HTML_LINK_UNSAFE", "The HTML contains an unsafe link.", ElementPath(link)));
            }
        }
        foreach (var image in root?.Descendants(html + "img") ?? [])
        {
            var source = (string?)image.Attribute("src");
            if (image.Attribute("alt") is null || string.IsNullOrWhiteSpace(source) ||
                !source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Issue(issues.Count, "HTML_IMAGE_INVALID", "An HTML image is missing a safe source or alternative decision.", ElementPath(image)));
            }
        }
        foreach (var table in root?.Descendants(html + "table") ?? [])
        {
            if (!table.Descendants(html + "th").Any() || table.Descendants(html + "th").Any(header => header.Attribute("scope") is null))
            {
                issues.Add(Issue(issues.Count, "HTML_TABLE_HEADER_INVALID", "Every table requires scoped header cells.", ElementPath(table)));
            }
        }

        var previousHeading = 0;
        foreach (var heading in root?.Descendants().Where(element =>
                     element.Name.Namespace == html && element.Name.LocalName.Length == 2 &&
                     element.Name.LocalName[0] == 'h' && char.IsDigit(element.Name.LocalName[1])) ?? [])
        {
            var level = heading.Name.LocalName[1] - '0';
            if (previousHeading > 0 && level > previousHeading + 1)
            {
                issues.Add(Issue(issues.Count, "HTML_HEADING_GAP", "The generated heading hierarchy skips a level.", ElementPath(heading)));
            }
            previousHeading = level;
        }

        return new DocumentArtifactValidationResult(issues);
    }

    private static XElement RenderBlock(
        DocumentBlock block,
        NormalizedDocumentModel document,
        IReadOnlyDictionary<string, ReviewedImageDecision> reviews,
        XNamespace html) => block switch
        {
            DocumentHeadingBlock heading => new XElement(
                html + $"h{heading.Level}",
                heading.Content.OrderBy(item => item.Order).Select(item => RenderInline(item, document, reviews, html))),
            DocumentParagraphBlock paragraph => new XElement(
                html + "p",
                paragraph.Content.OrderBy(item => item.Order).Select(item => RenderInline(item, document, reviews, html))),
            DocumentListBlock list => RenderList(list, document, reviews, html),
            DocumentTableBlock table => RenderTable(table, document, reviews, html),
            _ => throw new DocumentTransformationException("BLOCK_KIND_UNSUPPORTED", "The normalized block kind is unsupported.")
        };

    private static string HeadingText(DocumentHeadingBlock heading) => string.Concat(
        heading.Content.OrderBy(item => item.Order).Select(item => item switch
        {
            DocumentTextInline text => text.Text,
            DocumentLinkInline link => string.Concat(link.Content.OrderBy(text => text.Order).Select(text => text.Text)),
            _ => string.Empty
        })).Trim();

    private static XElement RenderList(
        DocumentListBlock list,
        NormalizedDocumentModel document,
        IReadOnlyDictionary<string, ReviewedImageDecision> reviews,
        XNamespace html)
    {
        var element = new XElement(html + (list.IsOrdered ? "ol" : "ul"));
        if (list.IsOrdered && list.Start != 1)
        {
            element.SetAttributeValue("start", list.Start);
        }
        foreach (var item in list.Items.OrderBy(item => item.Order))
        {
            element.Add(new XElement(html + "li",
                item.Content.OrderBy(inline => inline.Order).Select(inline => RenderInline(inline, document, reviews, html)),
                item.NestedLists.OrderBy(nested => nested.Order).Select(nested => RenderList(nested, document, reviews, html))));
        }
        return element;
    }

    private static XElement RenderTable(
        DocumentTableBlock table,
        NormalizedDocumentModel document,
        IReadOnlyDictionary<string, ReviewedImageDecision> reviews,
        XNamespace html)
    {
        var element = new XElement(html + "table");
        if (!string.IsNullOrWhiteSpace(table.Caption))
        {
            element.Add(new XElement(html + "caption", table.Caption));
        }
        foreach (var row in table.Rows.OrderBy(row => row.Order))
        {
            element.Add(new XElement(html + "tr",
                row.Cells.OrderBy(cell => cell.Order).Select(cell =>
                {
                    var name = cell.HeaderScope == DocumentTableHeaderScope.None ? "td" : "th";
                    var result = new XElement(html + name,
                        cell.Content.OrderBy(block => block.Order).Select(block => RenderBlock(block, document, reviews, html)));
                    if (cell.HeaderScope != DocumentTableHeaderScope.None)
                    {
                        result.SetAttributeValue("scope", cell.HeaderScope == DocumentTableHeaderScope.Row ? "row" : "col");
                    }
                    return result;
                })));
        }
        return element;
    }

    private static object RenderInline(
        DocumentInline inline,
        NormalizedDocumentModel document,
        IReadOnlyDictionary<string, ReviewedImageDecision> reviews,
        XNamespace html) => inline switch
        {
            DocumentTextInline text => RenderText(text, document.Language, html),
            DocumentLinkInline link => RenderLink(link, document.Language, html),
            DocumentImageInline image => RenderImage(image, document, reviews, html),
            _ => throw new DocumentTransformationException("INLINE_KIND_UNSUPPORTED", "The normalized inline kind is unsupported.")
        };

    private static object RenderText(DocumentTextInline text, string documentLanguage, XNamespace html)
    {
        object content = new XText(text.Text);
        if (text.Bold) content = new XElement(html + "strong", content);
        if (text.Italic) content = new XElement(html + "em", content);
        return string.Equals(text.Language, documentLanguage, StringComparison.OrdinalIgnoreCase)
            ? content
            : new XElement(html + "span", new XAttribute("lang", text.Language), content);
    }

    private static XElement RenderLink(DocumentLinkInline link, string documentLanguage, XNamespace html)
    {
        var element = new XElement(html + "a", new XAttribute("href", link.Href),
            link.Content.OrderBy(text => text.Order).Select(text => RenderText(text, documentLanguage, html)));
        if (!link.Href.StartsWith('#'))
        {
            element.SetAttributeValue("rel", "noopener noreferrer");
        }
        return element;
    }

    private static XElement RenderImage(
        DocumentImageInline inline,
        NormalizedDocumentModel document,
        IReadOnlyDictionary<string, ReviewedImageDecision> reviews,
        XNamespace html)
    {
        var image = document.Images.SingleOrDefault(item => item.Id == inline.ImageId)
            ?? throw new DocumentTransformationException("IMAGE_MODEL_MISSING", "A normalized image reference is missing.");
        if (!reviews.TryGetValue(image.Id, out var review))
        {
            throw new DocumentTransformationException("IMAGE_REVIEW_MISSING", "An image review decision is missing.");
        }
        var data = Convert.ToBase64String(image.Payload.Span);
        var element = new XElement(html + "img",
            new XAttribute("src", $"data:{image.MediaType};base64,{data}"),
            new XAttribute("alt", review.IsDecorative ? string.Empty : review.AltText!));
        if (review.IsDecorative)
        {
            element.SetAttributeValue("role", "presentation");
        }
        return element;
    }

    private static string ElementPath(XElement element) =>
        string.Join('/', element.AncestorsAndSelf().Reverse().Select(item => item.Name.LocalName));

    private static DocumentIssue Issue(int order, string code, string message, string source) =>
        new(order, code, DocumentIssueSeverity.Blocking, message, source);
}
