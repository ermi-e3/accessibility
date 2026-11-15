using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Accessibility.Application.Common;
using Accessibility.Infrastructure.Storage;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Options;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class S3ObjectStorageTests
{
    [S3IntegrationFact]
    public async Task StorageRoundTripSignedRangeAndNotFoundBehaviorWork()
    {
        var serviceUrl = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_S3_URL")!;
        var bucketPrefix = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_S3_BUCKET") ?? "accesslearn-test";
        var bucketName = $"{bucketPrefix}-{Guid.CreateVersion7():N}";
        var options = new ObjectStorageOptions
        {
            ServiceUrl = serviceUrl,
            PublicServiceUrl = serviceUrl,
            AccessKey = "test",
            SecretKey = "test",
            BucketName = bucketName,
            ForcePathStyle = true,
            ProvisionBucketCors = true
        };
        var provisioner = new ObjectStorageProvisioner(
            Options.Create(options),
            Options.Create(new ObjectStorageProvisioningOptions
            {
                AccessKey = "test",
                SecretKey = "test"
            }));
        await provisioner.InitializeAsync(CancellationToken.None);

        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(options.AccessKey, options.SecretKey),
            S3ClientConfiguration.Create(options));
        var storage = new S3ObjectStorage(s3, Options.Create(options));
        var objectKey = $"integration/{Guid.CreateVersion7()}.txt";
        var payload = Encoding.UTF8.GetBytes("accesslearn-storage-round-trip");

        try
        {
            await using var upload = new MemoryStream(payload, writable: false);
            var writeResult = await storage.PutAsync(
                objectKey,
                upload,
                "text/plain",
                payload.Length,
                new Dictionary<string, string> { ["test"] = "foundation" },
                CancellationToken.None);

            Assert.Equal(payload.Length, writeResult.ByteLength);
            Assert.False(string.IsNullOrWhiteSpace(writeResult.ETag));
            Assert.True(upload.CanRead);

            await using var stored = await storage.OpenReadAsync(objectKey, CancellationToken.None);
            using var copied = new MemoryStream();
            await stored.Content.CopyToAsync(copied, CancellationToken.None);
            Assert.Equal(payload, copied.ToArray());
            Assert.Equal("text/plain", stored.ContentType);

            var signedUri = await storage.CreateReadUriAsync(
                objectKey,
                TimeSpan.FromMinutes(5),
                CancellationToken.None);
            using var http = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, signedUri);
            request.Headers.Range = new RangeHeaderValue(0, 3);
            using var response = await http.SendAsync(request, CancellationToken.None);
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal(payload[..4], await response.Content.ReadAsByteArrayAsync(CancellationToken.None));

            var cors = await s3.GetCORSConfigurationAsync(bucketName, CancellationToken.None);
            Assert.Contains(cors.Configuration.Rules, rule =>
                rule.AllowedMethods.Contains("GET") && rule.AllowedOrigins.Contains("*"));
        }
        finally
        {
            await storage.DeleteAsync(objectKey, CancellationToken.None);
            await s3.DeleteBucketAsync(bucketName, CancellationToken.None);
        }

        await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
            storage.OpenReadAsync(objectKey, CancellationToken.None));
    }
}
