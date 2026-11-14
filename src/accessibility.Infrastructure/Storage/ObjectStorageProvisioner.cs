using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;

namespace Accessibility.Infrastructure.Storage;

public sealed class ObjectStorageProvisioner(
    IOptions<ObjectStorageOptions> storageOptions,
    IOptions<ObjectStorageProvisioningOptions> provisioningOptions)
{
    private readonly ObjectStorageOptions _storageOptions = storageOptions.Value;
    private readonly ObjectStorageProvisioningOptions _provisioningOptions = provisioningOptions.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_provisioningOptions.AccessKey) ||
            string.IsNullOrWhiteSpace(_provisioningOptions.SecretKey))
        {
            throw new InvalidOperationException(
                "ObjectStorageProvisioning credentials are required only when running --initialize.");
        }

        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(_provisioningOptions.AccessKey, _provisioningOptions.SecretKey),
            S3ClientConfiguration.Create(_storageOptions));

        if (!await AmazonS3Util.DoesS3BucketExistV2Async(s3, _storageOptions.BucketName))
        {
            try
            {
                await s3.PutBucketAsync(
                    new PutBucketRequest { BucketName = _storageOptions.BucketName },
                    cancellationToken);
            }
            catch (AmazonS3Exception exception) when (
                exception.ErrorCode is "BucketAlreadyExists" or "BucketAlreadyOwnedByYou")
            {
                // Another provisioning process won the bucket-creation race.
            }
        }

        if (!_storageOptions.ProvisionBucketCors)
        {
            return;
        }

        await s3.PutCORSConfigurationAsync(
            _storageOptions.BucketName,
            new CORSConfiguration
            {
                Rules =
                [
                    new CORSRule
                    {
                        Id = "accesslearn-public-read",
                        AllowedMethods = ["GET", "HEAD"],
                        AllowedOrigins = _storageOptions.CorsAllowedOrigins.Select(origin => origin.TrimEnd('/')).ToList(),
                        AllowedHeaders = ["Range", "If-None-Match", "If-Modified-Since"],
                        ExposeHeaders = ["ETag", "Accept-Ranges", "Content-Range"],
                        MaxAgeSeconds = 3_600
                    }
                ]
            },
            cancellationToken);
    }
}
