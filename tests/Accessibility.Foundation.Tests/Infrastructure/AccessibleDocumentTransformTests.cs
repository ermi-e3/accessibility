using Accessibility.Application.Features.Documents;
using Accessibility.Infrastructure.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class AccessibleDocumentTransformTests
{
    [Fact]
    public void KnownBilingualFixturePassesNormalizationAndSemanticHtml()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var path = Path.Combine(repositoryRoot, "sample-content", "documents", "structured-en-am.docx");
        using var package = File.OpenRead(path);
        _ = new DocxPackageSafetyValidator().Validate(package);
        package.Position = 0;

        var model = new OpenXmlDocumentNormalizer().Normalize(
            package,
            new DocumentNormalizationOptions("Inclusive communication study guide", "en"));

        Assert.DoesNotContain(model.Issues, issue => issue.Severity == DocumentIssueSeverity.Blocking);
        var image = Assert.Single(model.Images);
        var html = new SemanticHtmlDocumentRenderer().Render(model,
        [
            new ReviewedImageDecision(image.Id, "A reviewed one-pixel accessibility fixture image.", false)
        ]);
        Assert.True(html.Validation.IsValid);
        Assert.Contains("lang=\"am\"", html.Content, StringComparison.Ordinal);
        Assert.Contains("<ol", html.Content, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\"", html.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizerPreservesOrderedHeadingsLanguagesLinksAndTableHeaders()
    {
        using var package = BuildStructuredDocument();
        var normalizer = new OpenXmlDocumentNormalizer();

        var model = normalizer.Normalize(
            package,
            new DocumentNormalizationOptions("Inclusive communication", "en"));

        Assert.Equal(DocumentModelVersions.Current, model.SchemaVersion);
        Assert.Equal("en", model.Language);
        Assert.DoesNotContain(model.Issues, issue => issue.Severity == DocumentIssueSeverity.Blocking);
        var heading = Assert.IsType<DocumentHeadingBlock>(model.Blocks[0]);
        Assert.Equal(1, heading.Level);
        var paragraph = Assert.IsType<DocumentParagraphBlock>(model.Blocks[1]);
        Assert.Contains(paragraph.Content.OfType<DocumentTextInline>(), text => text.Language == "am");
        var link = Assert.Single(paragraph.Content.OfType<DocumentLinkInline>());
        Assert.Equal("https://example.test/accessibility", link.Href);
        var list = Assert.IsType<DocumentListBlock>(model.Blocks[2]);
        Assert.Equal(2, list.Items.Count);
        Assert.Single(list.Items[0].NestedLists);
        Assert.Single(list.Items[0].NestedLists[0].Items);
        var table = Assert.IsType<DocumentTableBlock>(model.Blocks[3]);
        Assert.True(table.Rows[0].IsHeader);
        Assert.All(table.Rows[0].Cells, cell => Assert.Equal(DocumentTableHeaderScope.Column, cell.HeaderScope));
        var serialized = NormalizedDocumentJson.Serialize(model);
        Assert.Equal(serialized, NormalizedDocumentJson.Serialize(NormalizedDocumentJson.Deserialize(serialized)));
    }

    [Fact]
    public void SemanticRendererRequiresReviewedImagesAndEmitsSelfContainedSafeXhtml()
    {
        var image = new NormalizedDocumentImage(
            0,
            "image-1",
            "rId5",
            "body/paragraph[2]/image[1]",
            "image/png",
            OnePixelPng,
            "Source suggestion",
            SuggestedDecorative: false);
        var model = new NormalizedDocumentModel(
            DocumentModelVersions.Current,
            "test-normalizer",
            "Accessible image example",
            "en",
            [new DocumentParagraphBlock(0, "body/p[1]", [
                new DocumentTextInline(0, "Reviewed figure: ", "en"),
                new DocumentImageInline(1, image.Id)
            ])],
            [image],
            []);
        var renderer = new SemanticHtmlDocumentRenderer();

        Assert.Throws<DocumentTransformationException>(() => renderer.Render(model));
        var artifact = renderer.Render(model, [
            new ReviewedImageDecision(image.Id, "A blue accessibility icon.", false)
        ]);

        Assert.True(artifact.Validation.IsValid);
        Assert.Contains("Content-Security-Policy", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,", artifact.Content, StringComparison.Ordinal);
        Assert.Contains("alt=\"A blue accessibility icon.\"", artifact.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", artifact.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", artifact.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SemanticRendererEncodesScriptShapedText()
    {
        var model = new NormalizedDocumentModel(
            DocumentModelVersions.Current,
            "test-normalizer",
            "Encoding test",
            "en",
            [new DocumentParagraphBlock(0, "body/p[1]", [
                new DocumentTextInline(0, "<script>alert('x')</script>", "en")
            ])],
            [],
            []);

        var artifact = new SemanticHtmlDocumentRenderer().Render(model);

        Assert.Contains("&lt;script&gt;", artifact.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", artifact.Content, StringComparison.OrdinalIgnoreCase);
        Assert.True(artifact.Validation.IsValid);
    }

    private static MemoryStream BuildStructuredDocument()
    {
        var stream = new MemoryStream();
        using (var package = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            package.PackageProperties.Title = "Inclusive communication";
            package.PackageProperties.Language = "en";
            var main = package.AddMainDocumentPart();
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new W.Styles(
                new W.Style(
                    new W.StyleName { Val = "Heading 1" },
                    new W.StyleParagraphProperties(new W.OutlineLevel { Val = 0 }))
                {
                    Type = W.StyleValues.Paragraph,
                    StyleId = "Heading1"
                });
            var hyperlink = main.AddHyperlinkRelationship(
                new Uri("https://example.test/accessibility"),
                isExternal: true);
            var numbering = main.AddNewPart<NumberingDefinitionsPart>();
            numbering.Numbering = new W.Numbering(
                new W.AbstractNum(
                    new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel },
                    new W.Level(
                        new W.StartNumberingValue { Val = 1 },
                        new W.NumberingFormat { Val = W.NumberFormatValues.Decimal },
                        new W.LevelText { Val = "%1." })
                    { LevelIndex = 0 },
                    new W.Level(
                        new W.StartNumberingValue { Val = 1 },
                        new W.NumberingFormat { Val = W.NumberFormatValues.Bullet },
                        new W.LevelText { Val = "•" })
                    { LevelIndex = 1 })
                { AbstractNumberId = 1 },
                new W.NumberingInstance(new W.AbstractNumId { Val = 1 }) { NumberID = 1 });
            main.Document = new W.Document(new W.Body(
                new W.Paragraph(
                    new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                    new W.Run(new W.Text("Inclusive communication"))),
                new W.Paragraph(
                    new W.Run(new W.Text("English and ")),
                    new W.Run(
                        new W.RunProperties(new W.Languages { Val = "am" }),
                        new W.Text("አማርኛ")),
                    new W.Run(new W.Text(". ")),
                    new W.Hyperlink(new W.Run(new W.Text("Read guidance"))) { Id = hyperlink.Id }),
                ListParagraph("Prepare", 0),
                ListParagraph("Check access needs", 1),
                ListParagraph("Confirm", 0),
                new W.Table(
                    new W.TableProperties(new W.TableBorders(
                        new W.TopBorder { Val = W.BorderValues.Single },
                        new W.BottomBorder { Val = W.BorderValues.Single })),
                    new W.TableGrid(new W.GridColumn(), new W.GridColumn()),
                    new W.TableRow(
                        new W.TableRowProperties(new W.TableHeader()),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Skill")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Evidence"))))),
                    new W.TableRow(
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Listening")))),
                        new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Reflection"))))))));
        }
        stream.Position = 0;
        return stream;
    }

    private static W.Paragraph ListParagraph(string text, int level) => new(
        new W.ParagraphProperties(new W.NumberingProperties(
            new W.NumberingLevelReference { Val = level },
            new W.NumberingId { Val = 1 })),
        new W.Run(new W.Text(text)));

    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
}
