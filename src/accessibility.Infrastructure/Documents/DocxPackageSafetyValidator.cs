using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Accessibility.Application.Features.Documents;

namespace Accessibility.Infrastructure.Documents;

public sealed class DocxPackageSafetyValidator : IDocxPackageSafetyValidator
{
    private const string MainDocumentContentType =
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";
    private const string HyperlinkRelationshipType =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private static readonly string[] ForbiddenPartFragments =
    [
        "/vbaProject.",
        "/activeX/",
        "/embeddings/",
        "/oleObject",
        "/attachedTemplate",
        "/altChunk/"
    ];
    private readonly DocxPackageSafetyLimits _limits;

    public DocxPackageSafetyValidator() : this(DocxPackageSafetyLimits.Default)
    {
    }

    public DocxPackageSafetyValidator(DocxPackageSafetyLimits limits)
    {
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public DocxPackageSafetyReport Validate(Stream package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CanRead || !package.CanSeek)
        {
            throw Rejected("DOCX_STREAM_INVALID", "The DOCX package must be a readable seekable stream.");
        }
        if (package.Length is <= 0 || package.Length > 25L * 1024 * 1024 || package.Length > _limits.MaximumPackageBytes)
        {
            throw Rejected("DOCX_PACKAGE_SIZE_INVALID", "The DOCX package size is invalid.");
        }

        package.Position = 0;
        try
        {
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count is 0 || archive.Entries.Count > _limits.MaximumEntries)
            {
                throw Rejected("DOCX_ENTRY_COUNT_INVALID", "The DOCX package contains an invalid number of entries.");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            var externalLinks = new List<string>();
            long expandedBytes = 0;
            long compressedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateEntryName(entry);
                if (!names.Add(entry.FullName))
                {
                    throw Rejected("DOCX_DUPLICATE_ENTRY", "The DOCX package contains duplicate entry names.");
                }
                if (IsSymbolicLink(entry))
                {
                    throw Rejected("DOCX_SYMBOLIC_LINK", "Symbolic links are not allowed in DOCX packages.");
                }
                if (ForbiddenPartFragments.Any(fragment =>
                    entry.FullName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                {
                    throw Rejected("DOCX_ACTIVE_CONTENT", "The DOCX package contains active or embedded content.");
                }
                if (entry.Length < 0 || entry.Length > _limits.MaximumSingleEntryBytes)
                {
                    throw Rejected("DOCX_ENTRY_SIZE_INVALID", "A DOCX part exceeds the allowed expanded size.");
                }
                expandedBytes = checked(expandedBytes + entry.Length);
                compressedBytes = checked(compressedBytes + entry.CompressedLength);
                if (expandedBytes > _limits.MaximumExpandedBytes)
                {
                    throw Rejected("DOCX_EXPANDED_SIZE_INVALID", "The DOCX package expands beyond the allowed size.");
                }
                if (entry.Length > 0 && entry.CompressedLength == 0 ||
                    entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > _limits.MaximumCompressionRatio)
                {
                    throw Rejected("DOCX_COMPRESSION_RATIO_INVALID", "A DOCX part has an unsafe compression ratio.");
                }

                entries[entry.FullName] = entry;
                if (IsXmlPart(entry.FullName))
                {
                    _ = ReadXml(entry, cancellationToken);
                }
            }

            ValidateContentTypes(entries);
            ValidateRequiredParts(entries);
            foreach (var relationship in entries.Values.Where(entry =>
                entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
            {
                ValidateRelationships(relationship, externalLinks, cancellationToken);
            }

            return new DocxPackageSafetyReport(
                archive.Entries.Count,
                compressedBytes,
                expandedBytes,
                externalLinks);
        }
        catch (DocxPackageRejectedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException or IOException or OverflowException)
        {
            throw Rejected("DOCX_PACKAGE_INVALID", "The DOCX package is malformed or unsafe.", exception);
        }
        finally
        {
            package.Position = 0;
        }
    }

    private void ValidateContentTypes(IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.TryGetValue("[Content_Types].xml", out var entry))
        {
            throw Rejected("DOCX_CONTENT_TYPES_MISSING", "The DOCX content-types manifest is missing.");
        }

        var document = ReadXml(entry, CancellationToken.None);
        XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
        var main = document.Root?.Elements(contentTypes + "Override").SingleOrDefault(element =>
            string.Equals((string?)element.Attribute("PartName"), "/word/document.xml", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals((string?)main?.Attribute("ContentType"), MainDocumentContentType, StringComparison.Ordinal))
        {
            throw Rejected("DOCX_MAIN_PART_INVALID", "Only non-macro WordprocessingML documents are supported.");
        }

        if (document.Descendants().Attributes("ContentType").Any(attribute =>
            attribute.Value.Contains("macroEnabled", StringComparison.OrdinalIgnoreCase) ||
            attribute.Value.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) ||
            attribute.Value.Contains("activeX", StringComparison.OrdinalIgnoreCase) ||
            attribute.Value.Contains("oleObject", StringComparison.OrdinalIgnoreCase)))
        {
            throw Rejected("DOCX_ACTIVE_CONTENT", "Macro, ActiveX, and OLE content is not supported.");
        }
    }

    private static void ValidateRequiredParts(IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (!entries.ContainsKey("_rels/.rels") || !entries.ContainsKey("word/document.xml"))
        {
            throw Rejected("DOCX_REQUIRED_PART_MISSING", "The DOCX package is missing a required document part.");
        }
    }

    private void ValidateRelationships(
        ZipArchiveEntry entry,
        ICollection<string> externalLinks,
        CancellationToken cancellationToken)
    {
        var document = ReadXml(entry, cancellationToken);
        XNamespace relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
        foreach (var relationship in document.Root?.Elements(relationships + "Relationship") ?? [])
        {
            var target = (string?)relationship.Attribute("Target") ?? string.Empty;
            var targetMode = (string?)relationship.Attribute("TargetMode");
            var type = (string?)relationship.Attribute("Type") ?? string.Empty;
            if (string.Equals(targetMode, "External", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(type, HyperlinkRelationshipType, StringComparison.Ordinal) ||
                    !DocumentLinkPolicy.TryNormalizeExternalHref(target, out var normalized))
                {
                    throw Rejected("DOCX_EXTERNAL_RELATIONSHIP_UNSAFE", "Only safe external hyperlinks are supported.");
                }

                externalLinks.Add(normalized);
                continue;
            }

            string decodedTarget;
            try
            {
                decodedTarget = Uri.UnescapeDataString(target);
            }
            catch (UriFormatException exception)
            {
                throw Rejected("DOCX_RELATIONSHIP_TARGET_INVALID", "A DOCX relationship target is malformed.", exception);
            }
            if (decodedTarget.Contains('\\') || decodedTarget.Contains('\0') || Path.IsPathRooted(decodedTarget) ||
                decodedTarget.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment == ".."))
            {
                throw Rejected("DOCX_RELATIONSHIP_TRAVERSAL", "A DOCX relationship escapes its package location.");
            }
        }
    }

    private XDocument ReadXml(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = _limits.MaximumXmlCharacters,
            MaxCharactersFromEntities = 0,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            CloseInput = false
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
    }

    private static bool IsXmlPart(string name) =>
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static void ValidateEntryName(ZipArchiveEntry entry)
    {
        var name = entry.FullName;
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1_024 || name.Contains('\\') || name.Contains('\0') ||
            name.StartsWith('/') || name.Contains(':') ||
            name.Any(character => char.IsControl(character)) ||
            name.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            throw Rejected("DOCX_ENTRY_PATH_UNSAFE", "The DOCX package contains an unsafe entry path.");
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static DocxPackageRejectedException Rejected(
        string code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);
}
