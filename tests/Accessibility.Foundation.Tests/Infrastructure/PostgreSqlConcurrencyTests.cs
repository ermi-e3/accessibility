using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class PostgreSqlConcurrencyTests
{
    [DatabaseIntegrationFact]
    public async Task ConcurrentLessonUpdateRejectsTheStaleWriter()
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AccessLearnDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        Guid lessonId;
        Guid ownerId;

        await using (var setup = new AccessLearnDbContext(options, TimeProvider.System))
        {
            ownerId = Guid.CreateVersion7();
            var uniqueEmail = $"concurrency-{ownerId:N}@accesslearn.test";
            setup.Users.Add(new ApplicationUser
            {
                Id = ownerId,
                UserName = uniqueEmail,
                NormalizedUserName = uniqueEmail.ToUpperInvariant(),
                Email = uniqueEmail,
                NormalizedEmail = uniqueEmail.ToUpperInvariant(),
                DisplayName = "Concurrency Test Owner",
                EmailConfirmed = true
            });
            var lesson = new Lesson(ownerId, "Concurrency lesson", LanguageTags.English);
            setup.Lessons.Add(lesson);
            await setup.SaveChangesAsync(CancellationToken.None);
            lessonId = lesson.Id;
        }

        try
        {
            await using var first = new AccessLearnDbContext(options, TimeProvider.System);
            await using var second = new AccessLearnDbContext(options, TimeProvider.System);
            var firstLesson = await first.Lessons.SingleAsync(
                lesson => lesson.Id == lessonId,
                CancellationToken.None);
            var staleLesson = await second.Lessons.SingleAsync(
                lesson => lesson.Id == lessonId,
                CancellationToken.None);

            firstLesson.UpdateDetails("First writer", null, LanguageTags.English);
            await first.SaveChangesAsync(CancellationToken.None);

            staleLesson.UpdateDetails("Stale writer", null, LanguageTags.English);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                second.SaveChangesAsync(CancellationToken.None));
        }
        finally
        {
            await using var cleanup = new AccessLearnDbContext(options, TimeProvider.System);
            await cleanup.Lessons.Where(lesson => lesson.Id == lessonId)
                .ExecuteDeleteAsync(CancellationToken.None);
            await cleanup.Users.Where(user => user.Id == ownerId)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
    }
}
