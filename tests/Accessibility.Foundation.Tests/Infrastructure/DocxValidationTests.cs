using System.IO.Compression;
using Accessibility.Application.Features.Documents;
using Accessibility.Infrastructure.Documents;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class DocxValidationTests
{
    [Fact]
    public void AcceptsRequiredOpenXmlPartsAndRejectsTraversal()
    {
        var valid = Path.Combine(Path.GetTempPath(), $"accesslearn-{Guid.NewGuid():N}.docx");
        var unsafeFile = Path.Combine(Path.GetTempPath(), $"accesslearn-{Guid.NewGuid():N}.docx");
        try
        {
            using (var archive = ZipFile.Open(valid, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml", ContentTypes);
                WriteEntry(archive, "_rels/.rels", RootRelationships);
                WriteEntry(archive, "word/document.xml", DocumentXml);
            }
            using (var stream = File.OpenRead(valid))
            {
                new DocxPackageSafetyValidator().Validate(stream);
            }

            using (var archive = ZipFile.Open(unsafeFile, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml", ContentTypes);
                WriteEntry(archive, "_rels/.rels", RootRelationships);
                WriteEntry(archive, "word/document.xml", DocumentXml);
                archive.CreateEntry("../escape.xml");
            }
            using var unsafeStream = File.OpenRead(unsafeFile);
            Assert.Throws<DocxPackageRejectedException>(() => new DocxPackageSafetyValidator().Validate(unsafeStream));
        }
        finally
        {
            File.Delete(valid);
            File.Delete(unsafeFile);
        }
    }

    [Fact]
    public void AllowsSafeExternalHyperlinksAndRejectsExternalImagesAndActiveContent()
    {
        using var safe = BuildPackage("""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.test/guidance" TargetMode="External" />
            </Relationships>
            """);
        var report = new DocxPackageSafetyValidator().Validate(safe);
        Assert.Equal(["https://example.test/guidance"], report.ExternalHyperlinks);

        using var unsafeImage = BuildPackage("""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="https://example.test/tracker.png" TargetMode="External" />
            </Relationships>
            """);
        Assert.Equal(
            "DOCX_EXTERNAL_RELATIONSHIP_UNSAFE",
            Assert.Throws<DocxPackageRejectedException>(() => new DocxPackageSafetyValidator().Validate(unsafeImage)).Code);

        using var active = BuildPackage(null, archive => WriteEntry(archive, "word/vbaProject.bin", "active"));
        Assert.Equal(
            "DOCX_ACTIVE_CONTENT",
            Assert.Throws<DocxPackageRejectedException>(() => new DocxPackageSafetyValidator().Validate(active)).Code);
    }

    private const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" />
        </Types>
        """;

    private const string DocumentXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body /></w:document>
        """;

    private const string RootRelationships = """
        <?xml version="1.0" encoding="UTF-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml" />
        </Relationships>
        """;

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static MemoryStream BuildPackage(string? documentRelationships, Action<ZipArchive>? customize = null)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "[Content_Types].xml", ContentTypes);
            WriteEntry(archive, "_rels/.rels", RootRelationships);
            WriteEntry(archive, "word/document.xml", DocumentXml);
            if (documentRelationships is not null)
            {
                WriteEntry(archive, "word/_rels/document.xml.rels", documentRelationships);
            }
            customize?.Invoke(archive);
        }
        stream.Position = 0;
        return stream;
    }
}
