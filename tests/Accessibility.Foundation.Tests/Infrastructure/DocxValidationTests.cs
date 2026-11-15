using System.IO.Compression;
using Accessibility.Application.Common;
using Accessibility.Infrastructure.Processing;
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
                WriteEntry(archive, "word/document.xml", DocumentXml);
            }
            ProcessingService.ValidateDocxPackage(valid);

            using (var archive = ZipFile.Open(unsafeFile, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "[Content_Types].xml", ContentTypes);
                WriteEntry(archive, "word/document.xml", DocumentXml);
                archive.CreateEntry("../escape.xml");
            }
            Assert.Throws<FileContentInvalidException>(() => ProcessingService.ValidateDocxPackage(unsafeFile));
        }
        finally
        {
            File.Delete(valid);
            File.Delete(unsafeFile);
        }
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

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
