using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Foundation.Tests.Infrastructure;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Accessibility.Foundation.Tests.Api;

public sealed class LessonApiTests
{
    [DatabaseIntegrationFact]
    public async Task CookieAndApiKeyAuthorizationPreserveOwnershipAndScopes()
    {
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var other = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(factory.Services, owner.Id, [AccessLearnScopes.LessonsRead]);

        using var ownerClient = factory.CreateSecureClient();
        await LoginAsync(ownerClient, owner.Email, owner.Password);
        var lessonId = await CreateLessonWithCookieAsync(ownerClient);

        using var otherClient = factory.CreateSecureClient();
        await LoginAsync(otherClient, other.Email, other.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/lessons/{lessonId}")).StatusCode);

        using var keyClient = factory.CreateSecureClient();
        keyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        Assert.Equal(HttpStatusCode.OK, (await keyClient.GetAsync($"/api/v1/lessons/{lessonId}")).StatusCode);
        var forbidden = await keyClient.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal("INSUFFICIENT_SCOPE", await ReadCodeAsync(forbidden));

        using var invalidClient = factory.CreateSecureClient();
        invalidClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential + "x");
        var invalid = await invalidClient.GetAsync($"/api/v1/lessons/{lessonId}");
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Equal("INVALID_API_KEY", await ReadCodeAsync(invalid));

        using var browserClient = factory.CreateSecureClient();
        browserClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        browserClient.DefaultRequestHeaders.Add("Origin", "https://unapproved.example");
        var rejectedOrigin = await browserClient.GetAsync($"/api/v1/lessons/{lessonId}");
        Assert.Equal(HttpStatusCode.Unauthorized, rejectedOrigin.StatusCode);
        Assert.Equal("ORIGIN_NOT_ALLOWED", await ReadCodeAsync(rejectedOrigin));

        var originOnlyCredential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [AccessLearnScopes.LessonsRead]);
        using var originOnlyClient = factory.CreateSecureClient();
        originOnlyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            originOnlyCredential);
        originOnlyClient.DefaultRequestHeaders.Add("Origin", "https://unapproved.example");
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await originOnlyClient.GetAsync($"/api/v1/lessons/{lessonId}")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var prefix = originOnlyCredential.Split('.')[1];
        Assert.Null((await database.IntegrationClients.AsNoTracking()
            .SingleAsync(client => client.KeyPrefix == prefix)).LastUsedAt);
    }

    [DatabaseIntegrationFact]
    public async Task ApiKeyCanStreamValidAudioButSpoofedContentNeverReachesStorage()
    {
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [AccessLearnScopes.LessonsRead, AccessLearnScopes.LessonsWrite, AccessLearnScopes.ResourcesWrite]);
        using var client = factory.CreateSecureClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);

        var created = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var lessonId = (await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetGuid();

        using var valid = Multipart("lesson.wav", "audio/wav", CreateWaveHeader());
        var upload = await client.PostAsync($"/api/v1/lessons/{lessonId}/assets?role=primaryAudio", valid);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var body = await upload.Content.ReadAsStringAsync();
        Assert.DoesNotContain("objectKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("etag", body, StringComparison.OrdinalIgnoreCase);

        var secondLesson = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        var secondLessonId = (await JsonDocument.ParseAsync(await secondLesson.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetGuid();
        var writesBeforeSpoof = factory.Storage.WriteCount;
        using var spoofed = Multipart("lesson.wav", "audio/wav", "not a wave"u8.ToArray());
        var rejected = await client.PostAsync($"/api/v1/lessons/{secondLessonId}/assets?role=primaryAudio", spoofed);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        Assert.Equal(writesBeforeSpoof, factory.Storage.WriteCount);
    }

    [DatabaseIntegrationFact]
    public async Task StorageFailureLeavesRejectedTrackedMetadata()
    {
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [AccessLearnScopes.LessonsWrite, AccessLearnScopes.ResourcesWrite]);
        using var client = factory.CreateSecureClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var created = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        var lessonId = (await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetGuid();
        factory.Storage.FailWritesAfterStore = true;

        using var valid = Multipart("lesson.wav", "audio/wav", CreateWaveHeader());
        var response = await client.PostAsync($"/api/v1/lessons/{lessonId}/assets?role=primaryAudio", valid);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("STORAGE_UNAVAILABLE", await ReadCodeAsync(response));
        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var asset = await database.MediaAssets.SingleAsync(value => value.LessonId == lessonId);
        var file = await database.StoredFiles.SingleAsync(value => value.Id == asset.StoredFileId);
        Assert.Equal(ResourceStatus.Rejected, asset.Status);
        Assert.False(asset.IsActive);
        Assert.Equal(StoredFileStatus.Rejected, file.Status);
        Assert.Equal(1, factory.Storage.ObjectCount);
    }

    [DatabaseIntegrationFact]
    public async Task MetadataFinalizationFailureRetainsPendingRowsAndStoredObject()
    {
        var migrationConnection = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_MIGRATION_CONNECTION")
            ?? throw new InvalidOperationException("ACCESSLEARN_TEST_MIGRATION_CONNECTION is required.");
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [AccessLearnScopes.LessonsWrite, AccessLearnScopes.ResourcesWrite]);
        using var client = factory.CreateSecureClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var created = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        var lessonId = (await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetGuid();
        var migrationOptions = new DbContextOptionsBuilder<AccessLearnDbContext>()
            .UseNpgsql(migrationConnection)
            .Options;
        await using var migrationDatabase = new AccessLearnDbContext(migrationOptions, TimeProvider.System);
        await migrationDatabase.Database.ExecuteSqlRawAsync("""
            CREATE OR REPLACE FUNCTION accesslearn_test_fail_ready() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'simulated metadata finalization failure';
            END;
            $$;
            CREATE TRIGGER accesslearn_test_fail_ready_trigger
            BEFORE UPDATE OF status ON media_assets
            FOR EACH ROW WHEN (NEW.status = 'Ready')
            EXECUTE FUNCTION accesslearn_test_fail_ready();
            """);
        try
        {
            using var valid = Multipart("lesson.wav", "audio/wav", CreateWaveHeader());
            var response = await client.PostAsync($"/api/v1/lessons/{lessonId}/assets?role=primaryAudio", valid);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("STATE_CONFLICT", await ReadCodeAsync(response));
            await using var scope = factory.Services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
            var asset = await database.MediaAssets.AsNoTracking().SingleAsync(value => value.LessonId == lessonId);
            var file = await database.StoredFiles.AsNoTracking().SingleAsync(value => value.Id == asset.StoredFileId);
            Assert.Equal(ResourceStatus.Pending, asset.Status);
            Assert.Equal(StoredFileStatus.Quarantined, file.Status);
            Assert.Equal(1, factory.Storage.ObjectCount);
        }
        finally
        {
            await migrationDatabase.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS accesslearn_test_fail_ready_trigger ON media_assets;
                DROP FUNCTION IF EXISTS accesslearn_test_fail_ready();
                """);
        }
    }

    [DatabaseIntegrationFact]
    public async Task MediaUploadConcurrencyIsBoundedToTwoRequests()
    {
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [AccessLearnScopes.LessonsWrite, AccessLearnScopes.ResourcesWrite]);
        using var client = factory.CreateSecureClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var lessonIds = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var created = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
            lessonIds.Add((await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
                .RootElement.GetProperty("id").GetGuid());
        }

        factory.Storage.BlockWrites = true;
        using var firstContent = Multipart("first.wav", "audio/wav", CreateWaveHeader());
        using var secondContent = Multipart("second.wav", "audio/wav", CreateWaveHeader());
        using var thirdContent = Multipart("third.wav", "audio/wav", CreateWaveHeader());
        var first = client.PostAsync($"/api/v1/lessons/{lessonIds[0]}/assets?role=primaryAudio", firstContent);
        await factory.Storage.WaitUntilFirstWriteBlockedAsync();
        var second = client.PostAsync($"/api/v1/lessons/{lessonIds[1]}/assets?role=primaryAudio", secondContent);
        await factory.Storage.WaitUntilTwoWritesBlockedAsync();
        var third = client.PostAsync($"/api/v1/lessons/{lessonIds[2]}/assets?role=primaryAudio", thirdContent);
        try
        {
            var rejected = await third.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal("RATE_LIMITED", await ReadCodeAsync(rejected));
        }
        finally
        {
            factory.Storage.ReleaseWrites();
        }

        Assert.Equal(HttpStatusCode.Created, (await first).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await second).StatusCode);
    }

    [DatabaseIntegrationFact]
    public async Task ApprovedTranscriptPublishesPublicManifestAndTranscript()
    {
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [
                AccessLearnScopes.LessonsRead,
                AccessLearnScopes.LessonsWrite,
                AccessLearnScopes.ResourcesWrite,
                AccessLearnScopes.LessonsPublish
            ]);
        using var client = factory.CreateSecureClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var created = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        var lessonId = (await JsonDocument.ParseAsync(await created.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetGuid();
        using var media = Multipart("lesson.wav", "audio/wav", CreateWaveHeader());
        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsync($"/api/v1/lessons/{lessonId}/assets?role=primaryAudio", media)).StatusCode);
        var transcriptResponse = await client.PostAsJsonAsync(
            $"/api/v1/lessons/{lessonId}/transcripts",
            new
            {
                language = "en",
                segments = new[]
                {
                    new
                    {
                        sequence = 0,
                        startMilliseconds = 0,
                        endMilliseconds = 100,
                        text = "Accessible lesson transcript",
                        speakerLabel = "Educator"
                    }
                }
            });
        Assert.Equal(HttpStatusCode.Created, transcriptResponse.StatusCode);
        using var transcriptJson = await JsonDocument.ParseAsync(await transcriptResponse.Content.ReadAsStreamAsync());
        var transcriptId = transcriptJson.RootElement.GetProperty("id").GetGuid();
        var transcriptVersion = transcriptJson.RootElement.GetProperty("version").GetInt64();
        using var approveRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/transcripts/{transcriptId}/approve");
        approveRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{transcriptVersion}\"");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(approveRequest)).StatusCode);
        var lessonDetail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/lessons/{lessonId}");
        var lessonVersion = lessonDetail.GetProperty("lesson").GetProperty("version").GetInt64();
        using var publishRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/lessons/{lessonId}/publish");
        publishRequest.Headers.TryAddWithoutValidation("If-Match", $"\"{lessonVersion}\"");
        var publishResponse = await client.SendAsync(publishRequest);
        Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);
        var publication = await publishResponse.Content.ReadFromJsonAsync<JsonElement>();
        var publicationId = publication.GetProperty("id").GetGuid();

        using var publicClient = factory.CreateSecureClient();
        var manifest = await publicClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/public/lessons/{lessonId}/manifest");
        Assert.Equal(lessonId, manifest.GetProperty("lessonId").GetGuid());
        Assert.Single(manifest.GetProperty("captions").EnumerateArray());
        Assert.Single(manifest.GetProperty("transcripts").EnumerateArray());
        var transcript = await publicClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/public/lessons/{lessonId}/publications/{publicationId}/transcripts/en");
        Assert.Equal("Accessible lesson transcript", transcript.GetProperty("segments")[0].GetProperty("text").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var storedSegment = await database.TranscriptSegments.SingleAsync(
            segment => segment.TranscriptId == transcriptId);
        storedSegment.Update(0, 100, "Unpublished transcript edit", "Editor");
        await database.SaveChangesAsync();
        var frozenTranscript = await publicClient.GetFromJsonAsync<JsonElement>(
            $"/api/v1/public/lessons/{lessonId}/publications/{publicationId}/transcripts/en");
        Assert.Equal(
            "Accessible lesson transcript",
            frozenTranscript.GetProperty("segments")[0].GetProperty("text").GetString());
    }

    [DatabaseIntegrationFact]
    public async Task ProcessingApiIsIdempotentAndSupportsCancellation()
    {
        using var factory = new LessonApiFactory();
        var owner = await SeedEducatorAsync(factory.Services);
        var credential = await SeedClientAsync(
            factory.Services,
            owner.Id,
            [
                AccessLearnScopes.LessonsWrite,
                AccessLearnScopes.ResourcesWrite,
                AccessLearnScopes.ProcessingRead,
                AccessLearnScopes.ProcessingWrite
            ]);
        using var client = factory.CreateSecureClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        var lesson = await client.PostAsJsonAsync("/api/v1/lessons", LessonRequest());
        var lessonId = (await lesson.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var media = Multipart("lesson.wav", "audio/wav", CreateWaveHeader());
        var upload = await client.PostAsync($"/api/v1/lessons/{lessonId}/assets?role=primaryAudio", media);
        var assetId = (await upload.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var request = new { sourceAssetId = assetId, targetLanguages = new[] { "en", "am" } };
        client.DefaultRequestHeaders.Add("Idempotency-Key", "processing-api-test");

        var first = await client.PostAsJsonAsync($"/api/v1/lessons/{lessonId}/processing-jobs", request);
        var second = await client.PostAsJsonAsync($"/api/v1/lessons/{lessonId}/processing-jobs", request);
        var firstJob = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondJob = await second.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = firstJob.GetProperty("id").GetGuid();
        Assert.Equal(jobId, secondJob.GetProperty("id").GetGuid());
        Assert.Equal(1, factory.ProcessingScheduler.EnqueueCount);

        var cancelled = await client.PostAsync(
            $"/api/v1/processing-jobs/{jobId}/cancel",
            null);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal("cancelled", (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.Equal(1, factory.ProcessingScheduler.DeleteCount);
    }

    private static object LessonRequest() => new
    {
        title = $"Lesson {Guid.NewGuid():N}",
        description = "Integration test",
        sourceLanguage = "en"
    };

    private static MultipartFormDataContent Multipart(string fileName, string contentType, byte[] content)
    {
        var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        multipart.Add(file, "file", fileName);
        return multipart;
    }

    private static byte[] CreateWaveHeader()
    {
        return
        [
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 36, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E',
            (byte)'f', (byte)'m', (byte)'t', (byte)' ', 16, 0, 0, 0,
            1, 0, 1, 0, 64, 31, 0, 0, 128, 62, 0, 0,
            2, 0, 16, 0, (byte)'d', (byte)'a', (byte)'t', (byte)'a', 0, 0, 0, 0
        ];
    }

    private static async Task<Guid> CreateLessonWithCookieAsync(HttpClient client)
    {
        var token = await GetAntiforgeryAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/lessons")
        {
            Content = JsonContent.Create(LessonRequest())
        };
        request.Headers.Add(token.HeaderName, token.RequestToken);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetGuid();
    }

    private static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var token = await GetAntiforgeryAsync(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email, password, rememberMe = false })
        };
        request.Headers.Add(token.HeaderName, token.RequestToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    private static async Task<AntiforgeryToken> GetAntiforgeryAsync(HttpClient client)
    {
        return await client.GetFromJsonAsync<AntiforgeryToken>("/api/v1/auth/antiforgery")
            ?? throw new InvalidOperationException("Antiforgery token response was empty.");
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static async Task<TestUser> SeedEducatorAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var id = Guid.CreateVersion7();
        var email = $"educator-{id:N}@accesslearn.test";
        const string password = "Aa1!accesslearn-test-password";
        var user = new ApplicationUser
        {
            Id = id,
            Email = email,
            UserName = email,
            DisplayName = "Test Educator",
            EmailConfirmed = true
        };
        Assert.True((await manager.CreateAsync(user, password)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(user, AccessLearnRoles.Educator)).Succeeded);
        return new TestUser(id, email, password);
    }

    private static async Task<string> SeedClientAsync(IServiceProvider services, Guid ownerId, string[] scopes)
    {
        await using var scope = services.CreateAsyncScope();
        var keys = scope.ServiceProvider.GetRequiredService<IIntegrationApiKeyService>();
        var database = scope.ServiceProvider.GetRequiredService<AccessLearnDbContext>();
        var credential = keys.Generate();
        database.IntegrationClients.Add(new IntegrationClient(
            ownerId,
            "Lesson API test client",
            credential.Prefix,
            credential.Hash,
            scopes,
            []));
        await database.SaveChangesAsync();
        return credential.Token;
    }

    private sealed record TestUser(Guid Id, string Email, string Password);

    private sealed record AntiforgeryToken(string RequestToken, string HeaderName);
}

internal sealed class LessonApiFactory : WebApplicationFactory<Program>
{
    private readonly string _quarantinePath = Path.Combine(
        Path.GetTempPath(),
        $"accesslearn-api-tests-{Guid.NewGuid():N}");

    public FakeObjectStorage Storage { get; } = new();

    public FakeScheduler ProcessingScheduler { get; } = new();

    public HttpClient CreateSecureClient()
    {
        return CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var database = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Database"] = database,
                ["ConnectionStrings:Hangfire"] = database,
                ["ObjectStorage:ServiceUrl"] = "http://localhost:9000",
                ["ObjectStorage:AccessKey"] = "test",
                ["ObjectStorage:SecretKey"] = "test",
                ["ObjectStorage:BucketName"] = "test",
                ["ApiKeyAuthentication:Pepper"] = new string('p', 48),
                ["MediaProbe:ExecutablePath"] = "test-double",
                ["LessonIngest:QuarantinePath"] = _quarantinePath,
                ["Processing:WorkerEnabled"] = "false",
                ["Processing:DashboardEnabled"] = "false"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IObjectStorage>();
            services.RemoveAll<IMediaProbe>();
            services.RemoveAll<IBackgroundJobScheduler>();
            services.AddSingleton<IObjectStorage>(Storage);
            services.AddSingleton<IMediaProbe>(new FakeMediaProbe());
            services.AddSingleton<IBackgroundJobScheduler>(ProcessingScheduler);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_quarantinePath))
        {
            Directory.Delete(_quarantinePath, recursive: true);
        }
    }
}

internal sealed class FakeObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();
    private readonly TaskCompletionSource _firstWriteBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _twoWritesBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseWrites = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _blockedWrites;

    public int WriteCount { get; private set; }

    public bool FailWritesAfterStore { get; set; }

    public int ObjectCount => _objects.Count;

    public bool BlockWrites { get; set; }

    public Task WaitUntilFirstWriteBlockedAsync() => _firstWriteBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public Task WaitUntilTwoWritesBlockedAsync() => _twoWritesBlocked.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void ReleaseWrites() => _releaseWrites.TrySetResult();

    public async Task<ObjectStorageWriteResult> PutAsync(
        string objectKey,
        Stream content,
        string contentType,
        long byteLength,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _objects[objectKey] = buffer.ToArray();
        WriteCount++;
        if (BlockWrites)
        {
            var blocked = Interlocked.Increment(ref _blockedWrites);
            _firstWriteBlocked.TrySetResult();
            if (blocked >= 2)
            {
                _twoWritesBlocked.TrySetResult();
            }

            await _releaseWrites.Task.WaitAsync(cancellationToken);
        }

        if (FailWritesAfterStore)
        {
            throw new IOException("Simulated ambiguous storage completion failure.");
        }
        return new ObjectStorageWriteResult(objectKey, "test-etag", byteLength);
    }

    public Task<ObjectStorageReadResult> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var content = _objects[objectKey];
        return Task.FromResult(new ObjectStorageReadResult(
            new MemoryStream(content, writable: false),
            "application/octet-stream",
            content.Length,
            "test-etag"));
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        _objects.TryRemove(objectKey, out _);
        return Task.CompletedTask;
    }

    public Task<Uri> CreateReadUriAsync(string objectKey, TimeSpan lifetime, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://storage.test/{objectKey}"));

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}

internal sealed class FakeMediaProbe : IMediaProbe
{
    public Task<MediaProbeResult> ProbeAsync(string filePath, CancellationToken cancellationToken) =>
        Task.FromResult(new MediaProbeResult(
            100,
            HasVideo: false,
            HasAudio: true,
            Width: null,
            Height: null,
            VideoCodecs: [],
            AudioCodecs: ["pcm_s16le"],
            Container: "wav"));
}
