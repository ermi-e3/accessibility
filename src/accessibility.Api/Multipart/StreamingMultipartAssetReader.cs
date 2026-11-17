using System.Text;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Accessibility.Api.Multipart;

public interface IStreamingMultipartAssetReader
{
    Task<QuarantinedUpload> ReadAsync(
        HttpRequest request,
        long maximumFileBytes,
        CancellationToken cancellationToken);

    Task<StreamingMultipartUpload> ReadWithFieldsAsync(
        HttpRequest request,
        long maximumFileBytes,
        IReadOnlySet<string> allowedFields,
        CancellationToken cancellationToken);
}

public sealed record StreamingMultipartUpload(
    QuarantinedUpload File,
    IReadOnlyDictionary<string, string> Fields) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => File.DisposeAsync();
}

public sealed class StreamingMultipartAssetReader(IUploadQuarantine quarantine) : IStreamingMultipartAssetReader
{
    private const int MaximumSections = 8;
    private const int MaximumBoundaryLength = 128;

    public async Task<QuarantinedUpload> ReadAsync(
        HttpRequest request,
        long maximumFileBytes,
        CancellationToken cancellationToken)
    {
        var upload = await ReadCoreAsync(
            request,
            maximumFileBytes,
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken);
        return upload.File;
    }

    public Task<StreamingMultipartUpload> ReadWithFieldsAsync(
        HttpRequest request,
        long maximumFileBytes,
        IReadOnlySet<string> allowedFields,
        CancellationToken cancellationToken) =>
        ReadCoreAsync(request, maximumFileBytes, allowedFields, cancellationToken);

    private async Task<StreamingMultipartUpload> ReadCoreAsync(
        HttpRequest request,
        long maximumFileBytes,
        IReadOnlySet<string> allowedFields,
        CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw new UnsupportedFileTypeException("Content-Type must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaximumBoundaryLength)
        {
            throw Validation("file", "The multipart boundary is missing or too long.");
        }

        var reader = new MultipartReader(boundary, request.Body)
        {
            BodyLengthLimit = maximumFileBytes,
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024
        };
        QuarantinedUpload? upload = null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = 0;
        try
        {
            while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
            {
                sections++;
                if (sections > MaximumSections)
                {
                    throw Validation("file", "The multipart request contains too many sections.");
                }

                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                    !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw Validation("file", "Every multipart section must use form-data content disposition.");
                }

                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? string.Empty;
                var hasFileName = !string.IsNullOrWhiteSpace(disposition.FileName.Value) ||
                                  !string.IsNullOrWhiteSpace(disposition.FileNameStar.Value);
                if (!hasFileName)
                {
                    if (!allowedFields.Contains(name) || fields.ContainsKey(name))
                    {
                        throw Validation(name.Length == 0 ? "file" : name, "The multipart field is unsupported or duplicated.");
                    }
                    fields[name] = await ReadFieldAsync(section.Body, cancellationToken);
                    continue;
                }
                if (!string.Equals(name, "file", StringComparison.Ordinal))
                {
                    throw Validation("file", "Exactly one multipart file section named 'file' is required.");
                }

                if (upload is not null)
                {
                    throw Validation("file", "Only one file can be uploaded at a time.");
                }

                var fileName = HeaderUtilities.RemoveQuotes(
                    !string.IsNullOrWhiteSpace(disposition.FileNameStar.Value)
                        ? disposition.FileNameStar
                        : disposition.FileName).Value ?? string.Empty;
                upload = await quarantine.WriteAsync(
                    section.Body,
                    fileName,
                    section.ContentType ?? "application/octet-stream",
                    maximumFileBytes,
                    cancellationToken);
            }

            return new StreamingMultipartUpload(
                upload ?? throw Validation("file", "A file section is required."),
                fields);
        }
        catch (InvalidDataException)
        {
            if (upload is not null)
            {
                await upload.DisposeAsync();
            }

            throw new FileTooLargeException(maximumFileBytes);
        }
        catch
        {
            if (upload is not null)
            {
                await upload.DisposeAsync();
            }

            throw;
        }
    }

    private static async Task<string> ReadFieldAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 2_000;
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1_024,
            leaveOpen: true);
        var buffer = new char[maximumCharacters + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        if (total > maximumCharacters)
        {
            throw Validation("field", "A multipart text field is too long.");
        }
        return new string(buffer, 0, total).Trim();
    }

    private static RequestValidationException Validation(string field, string message)
    {
        return new RequestValidationException(new Dictionary<string, string[]> { [field] = [message] });
    }
}
