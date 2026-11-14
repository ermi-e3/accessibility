using System.Net;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Storage;

public sealed class S3ObjectStorage(IAmazonS3 s3, IOptions<ObjectStorageOptions> options) : IObjectStorage
{
    private readonly ObjectStorageOptions _options = options.Value;

    public async Task<ObjectStorageWriteResult> PutAsync(
        string objectKey,
        Stream content,
        string contentType,
        long byteLength,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfLessThan(byteLength, 1);

        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false,
            AutoResetStreamPosition = false
        };
        request.Headers.ContentLength = byteLength;

        if (metadata is not null)
        {
            foreach (var pair in metadata)
            {
                request.Metadata.Add(pair.Key, pair.Value);
            }
        }

        try
        {
            var response = await s3.PutObjectAsync(request, cancellationToken);
            return new ObjectStorageWriteResult(objectKey, response.ETag, byteLength);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception)
        {
            throw new StorageUnavailableException("The object could not be stored.", exception);
        }
    }

    public async Task<ObjectStorageReadResult> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);

        try
        {
            var response = await s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey
                },
                cancellationToken);

            return new ObjectStorageReadResult(
                response.ResponseStream,
                response.Headers.ContentType ?? "application/octet-stream",
                response.ContentLength,
                response.ETag,
                response);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception) when (
            exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode == "NoSuchKey")
        {
            throw new ResourceNotFoundException("Stored object", objectKey);
        }
        catch (AmazonS3Exception exception)
        {
            throw new StorageUnavailableException("The object could not be read.", exception);
        }
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);

        try
        {
            await s3.DeleteObjectAsync(
                new DeleteObjectRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception exception)
        {
            throw new StorageUnavailableException("The object could not be deleted.", exception);
        }
    }

    public async Task<Uri> CreateReadUriAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Signed URL lifetime must be between zero and seven days.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var url = await s3.GetPreSignedURLAsync(
            new GetPreSignedUrlRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(lifetime),
                Protocol = new Uri(_options.ServiceUrl).Scheme == Uri.UriSchemeHttp ? Protocol.HTTP : Protocol.HTTPS
            });

        return new Uri(url, UriKind.Absolute);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await s3.ListObjectsV2Async(
                new ListObjectsV2Request
                {
                    BucketName = _options.BucketName,
                    MaxKeys = 1
                },
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AmazonS3Exception)
        {
            return false;
        }
    }

    private static void ValidateObjectKey(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        var segments = objectKey.Split('/', StringSplitOptions.None);
        if (objectKey.Contains('\\') ||
            segments.Any(segment => segment is "" or "." or ".."))
        {
            throw new ArgumentException("Object key must be a relative, normalized key.", nameof(objectKey));
        }
    }
}
