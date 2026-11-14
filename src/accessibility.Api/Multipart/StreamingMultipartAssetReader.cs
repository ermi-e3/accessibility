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
                    !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(HeaderUtilities.RemoveQuotes(disposition.Name).Value, "file", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(disposition.FileName.Value) &&
                    string.IsNullOrWhiteSpace(disposition.FileNameStar.Value))
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

            return upload ?? throw Validation("file", "A file section is required.");
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

    private static RequestValidationException Validation(string field, string message)
    {
        return new RequestValidationException(new Dictionary<string, string[]> { [field] = [message] });
    }
}
