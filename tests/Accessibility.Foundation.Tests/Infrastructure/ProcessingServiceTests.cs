using Accessibility.Application.Abstractions;
using Accessibility.Application.Authorization;
using Accessibility.Application.Common;
using Accessibility.Application.Features.Processing;
using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Processing;
using Accessibility.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class ProcessingServiceTests
{
    [DatabaseIntegrationFact]
    public async Task StartIsIdempotentAndQueuedCancellationDeletesExternalJob()
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AccessLearnDbContext>().UseNpgsql(connectionString).Options;
        var ownerId = Guid.CreateVersion7();
        await using var database = new AccessLearnDbContext(options, TimeProvider.System);
        var seeded = await SeedSourceAsync(database, ownerId);
        var scheduler = new FakeScheduler();
        var service = new ProcessingService(
            database,
            new FakeUserContext(ownerId),
            scheduler,
            new FakeTranscriptionProvider(),
            new FakeTranslationProvider(),
            new TranscriptFormatService(),
            new ProcessingCancellationRegistry(),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingService>.Instance);

        var first = await service.StartAsync(
            seeded.LessonId,
            new StartProcessingCommand(seeded.AssetId, ["en", "am"]),
            "same-request",
            CancellationToken.None);
        var second = await service.StartAsync(
            seeded.LessonId,
            new StartProcessingCommand(seeded.AssetId, ["am", "en"]),
            "same-request",
            CancellationToken.None);
        var equivalentDifferentClientKey = await service.StartAsync(
            seeded.LessonId,
            new StartProcessingCommand(seeded.AssetId, ["en", "am"]),
            "different-request-key",
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Id, equivalentDifferentClientKey.Id);
        Assert.Equal(1, scheduler.EnqueueCount);
        await Assert.ThrowsAsync<StateConflictException>(() => service.StartAsync(
            seeded.LessonId,
            new StartProcessingCommand(seeded.AssetId, ["en"]),
            "same-request",
            CancellationToken.None));
        var cancelled = await service.CancelAsync(first.Id, CancellationToken.None);
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(1, scheduler.DeleteCount);
    }

    internal static async Task<(Guid LessonId, Guid AssetId)> SeedSourceAsync(
        AccessLearnDbContext database,
        Guid ownerId)
    {
        var email = $"processing-{ownerId:N}@accesslearn.test";
        database.Users.Add(new ApplicationUser
        {
            Id = ownerId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Processing Owner",
            EmailConfirmed = true
        });
        var lesson = new Lesson(ownerId, "Processing lesson", LanguageTags.English);
        var file = new StoredFile($"processing/{Guid.NewGuid():N}.wav", "source.wav", "audio/wav", 44, new string('a', 64));
        file.MarkReady("etag");
        var asset = new MediaAsset(lesson.Id, file.Id, MediaAssetRole.PrimaryAudio, LanguageTags.English);
        asset.MarkReady(1_000, null, null, "pcm_s16le");
        database.Lessons.Add(lesson);
        database.StoredFiles.Add(file);
        database.MediaAssets.Add(asset);
        await database.SaveChangesAsync();
        return (lesson.Id, asset.Id);
    }
}

internal sealed class FakeScheduler : IBackgroundJobScheduler
{
    public int EnqueueCount { get; private set; }
    public int DeleteCount { get; private set; }

    public string Enqueue(Guid processingJobId)
    {
        EnqueueCount++;
        return $"job-{processingJobId:N}";
    }

    public string Schedule(Guid processingJobId, TimeSpan delay) => Enqueue(processingJobId);

    public bool Delete(string externalJobId)
    {
        DeleteCount++;
        return true;
    }

    public bool IsActive(string externalJobId) => true;
}

internal sealed class FakeUserContext(Guid userId) : IUserContext
{
    public Guid? UserId => userId;
    public Guid? IntegrationClientId => null;
    public bool IsAuthenticated => true;
    public bool IsIntegrationClient => false;
    public bool IsInRole(string role) => role == AccessLearnRoles.Educator;
    public bool HasScope(string scope) => false;
}

internal sealed class FakeTranscriptionProvider : ITranscriptionProvider
{
    public string Name => "fake-stt";
    public string Version => "1";
    public Task<ProviderTranscript> TranscribeAsync(string audioFilePath, string language, CancellationToken cancellationToken) =>
        Task.FromResult(new ProviderTranscript(language, [new ProviderTranscriptSegment(0, 0, 1_000, "Hello")]));
}

internal sealed class FakeTranslationProvider : ITranslationProvider
{
    public string Name => "fake-translation";
    public string Version => "1";
    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> segments,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(segments.Select(value => $"{targetLanguage}:{value}").ToArray());
}
