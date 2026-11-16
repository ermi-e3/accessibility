using System.Security.Cryptography;
using Accessibility.Application.Features.Offline;
using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Offline;
using Accessibility.Infrastructure.Persistence;
using Accessibility.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class OfflinePackageTests
{
    [DatabaseIntegrationFact]
    public async Task StartIsIdempotentForCurrentPublication()
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AccessLearnDbContext>().UseNpgsql(connectionString).Options;
        var ownerId = Guid.CreateVersion7();
        await using var database = new AccessLearnDbContext(options, TimeProvider.System);
        var email = $"offline-{ownerId:N}@accesslearn.test";
        database.Users.Add(new ApplicationUser
        {
            Id = ownerId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Offline Owner",
            EmailConfirmed = true
        });
        var lesson = new Lesson(ownerId, "Offline lesson", LanguageTags.English);
        var manifest = new StoredFile(
            $"offline-test/{Guid.NewGuid():N}.json",
            "manifest.json",
            "application/json",
            2,
            new string('a', 64));
        manifest.MarkReady("etag");
        var publication = new LessonPublication(
            lesson.Id,
            1,
            manifest.Id,
            ownerId,
            new string('a', 64),
            DateTimeOffset.UtcNow);
        lesson.Publish(publication);
        database.Lessons.Add(lesson);
        database.StoredFiles.Add(manifest);
        database.LessonPublications.Add(publication);
        await database.SaveChangesAsync();
        var scheduler = new FakeOfflineScheduler();
        var service = new OfflinePackageService(
            database,
            new FakeUserContext(ownerId),
            scheduler,
            new RunnerObjectStorage(),
            TimeProvider.System,
            Options.Create(new OfflineLearningOptions()),
            NullLogger<OfflinePackageService>.Instance);

        var first = await service.StartAsync(lesson.Id, CancellationToken.None);
        var second = await service.StartAsync(lesson.Id, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, scheduler.EnqueueCount);
    }

    [Fact]
    public void ZipGenerationIsDeterministic()
    {
        var root = Path.Combine(Path.GetTempPath(), $"offline-zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstZip = Path.Combine(Path.GetTempPath(), $"offline-{Guid.NewGuid():N}.zip");
        var secondZip = Path.Combine(Path.GetTempPath(), $"offline-{Guid.NewGuid():N}.zip");
        try
        {
            File.WriteAllText(Path.Combine(root, "b.txt"), "second");
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllText(Path.Combine(root, "nested", "a.txt"), "first");

            OfflinePackageJobRunner.CreateDeterministicZip(root, firstZip);
            File.SetLastWriteTimeUtc(Path.Combine(root, "b.txt"), DateTime.UtcNow.AddDays(5));
            OfflinePackageJobRunner.CreateDeterministicZip(root, secondZip);

            Assert.Equal(SHA256.HashData(File.ReadAllBytes(firstZip)), SHA256.HashData(File.ReadAllBytes(secondZip)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(firstZip);
            File.Delete(secondZip);
        }
    }
}

internal sealed class FakeOfflineScheduler : IOfflinePackageScheduler
{
    public int EnqueueCount { get; private set; }
    public string Enqueue(Guid offlinePackageId)
    {
        EnqueueCount++;
        return $"offline-{offlinePackageId:N}";
    }
    public bool IsActive(string externalJobId) => true;
    public string Schedule(Guid offlinePackageId, TimeSpan delay) => Enqueue(offlinePackageId);
}
