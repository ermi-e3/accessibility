using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class S3IntegrationFactAttribute : FactAttribute
{
    public S3IntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_S3_URL")))
        {
            Skip = "Set ACCESSLEARN_TEST_S3_URL to run S3 integration tests.";
        }
    }
}

public sealed class DatabaseIntegrationFactAttribute : FactAttribute
{
    public DatabaseIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")))
        {
            Skip = "Set ACCESSLEARN_TEST_DB_CONNECTION to run PostgreSQL integration tests.";
        }
    }
}

public sealed class FfprobeIntegrationFactAttribute : FactAttribute
{
    public FfprobeIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_FFPROBE_PATH")))
        {
            Skip = "Set ACCESSLEARN_TEST_FFPROBE_PATH to run ffprobe integration tests.";
        }
    }
}
