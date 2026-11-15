using System.Collections.Concurrent;
using System.Text.Json;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Features.Processing;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class ProcessingJobRunnerTests
{
    [DatabaseIntegrationFact]
    public async Task RunnerPersistsOfflineAudioAndBilingualDrafts()
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AccessLearnDbContext>().UseNpgsql(connectionString).Options;
        var ownerId = Guid.CreateVersion7();
        await using var database = new AccessLearnDbContext(options, TimeProvider.System);
        var source = await ProcessingServiceTests.SeedSourceAsync(database, ownerId);
        var sourceMetadata = await (
            from asset in database.MediaAssets
            join file in database.StoredFiles on asset.StoredFileId equals file.Id
            where asset.Id == source.AssetId
            select new { Asset = asset, File = file }).SingleAsync();
        var job = new ProcessingJob(
            ProcessingJobType.PrepareMedia,
            $"runner-test-{Guid.NewGuid():N}",
            lessonId: source.LessonId,
            mediaAssetId: source.AssetId,
            requestedByUserId: ownerId,
            payloadJson: JsonSerializer.Serialize(new PrepareAccessibilityPayload(
                source.AssetId,
                sourceMetadata.Asset.Version,
                sourceMetadata.File.Sha256,
                ["am", "en"],
                "test-v1",
                null,
                null,
                null)));
        database.ProcessingJobs.Add(job);
        await database.SaveChangesAsync();
        var storage = new RunnerObjectStorage();
        storage.Seed(sourceMetadata.File.ObjectKey, new byte[] { 1, 2, 3, 4 });
        var runner = new ProcessingJobRunner(
            database,
            storage,
            new FakeMediaProcessingProvider(),
            new FakeTranscriptionProvider(),
            new FakeTranslationProvider(),
            new FakeScheduler(),
            new ProcessingCancellationRegistry(),
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ProcessingJobRunner>.Instance);

        await runner.ExecuteAsync(job.Id);

        await database.Entry(job).ReloadAsync();
        Assert.Equal(ProcessingJobStatus.Succeeded, job.Status);
        Assert.Equal(100, job.ProgressPercentage);
        var transcripts = await database.Transcripts
            .Where(transcript => transcript.ProcessingJobId == job.Id)
            .OrderBy(transcript => transcript.Language)
            .ToListAsync();
        Assert.Equal(new[] { "am", "en" }, transcripts.Select(transcript => transcript.Language));
        Assert.All(transcripts, transcript => Assert.Equal(ReviewStatus.Draft, transcript.ReviewStatus));
        Assert.Contains(transcripts, transcript => transcript.Provider == "fake-stt");
        Assert.Contains(transcripts, transcript => transcript.Provider == "fake-translation");
        Assert.True(await database.MediaAssets.AnyAsync(asset =>
            asset.LessonId == source.LessonId && asset.Role == MediaAssetRole.OfflineAudio &&
            asset.Status == ResourceStatus.Ready));
    }
}

internal sealed class FakeMediaProcessingProvider : IMediaProcessingProvider
{
    public async Task<PreparedMediaFiles> PrepareAsync(
        string sourceFilePath,
        string outputDirectory,
        bool generatePoster,
        CancellationToken cancellationToken)
    {
        var speech = Path.Combine(outputDirectory, "speech.wav");
        var offline = Path.Combine(outputDirectory, "offline.ogg");
        await File.WriteAllBytesAsync(speech, new byte[] { 1, 2 }, cancellationToken);
        await File.WriteAllBytesAsync(offline, new byte[] { 3, 4 }, cancellationToken);
        string? poster = null;
        if (generatePoster)
        {
            poster = Path.Combine(outputDirectory, "poster.jpg");
            await File.WriteAllBytesAsync(poster, new byte[] { 5, 6 }, cancellationToken);
        }
        return new PreparedMediaFiles(speech, offline, "audio/ogg", ".ogg", poster);
    }
}

internal sealed class RunnerObjectStorage : IObjectStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public void Seed(string key, byte[] value) => _objects[key] = value;

    public async Task<ObjectStorageWriteResult> PutAsync(
        string objectKey,
        Stream content,
        string contentType,
        long byteLength,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        await content.CopyToAsync(output, cancellationToken);
        _objects[objectKey] = output.ToArray();
        return new ObjectStorageWriteResult(objectKey, "etag", byteLength);
    }

    public Task<ObjectStorageReadResult> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var bytes = _objects[objectKey];
        return Task.FromResult(new ObjectStorageReadResult(
            new MemoryStream(bytes, writable: false),
            "application/octet-stream",
            bytes.Length,
            "etag"));
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
