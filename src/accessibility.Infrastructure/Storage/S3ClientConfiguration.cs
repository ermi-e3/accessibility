using Amazon.Runtime;
using Amazon.S3;

namespace Accessibility.Infrastructure.Storage;

internal static class S3ClientConfiguration
{
    public static AmazonS3Config Create(ObjectStorageOptions options)
    {
        return new AmazonS3Config
        {
            ServiceURL = options.ServiceUrl.TrimEnd('/'),
            AuthenticationRegion = options.Region,
            ForcePathStyle = options.ForcePathStyle,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };
    }
}
