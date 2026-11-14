using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Infrastructure.Identity;
using Accessibility.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class MediaAssetConstraintTests
{
    [DatabaseIntegrationFact]
    public async Task SignLanguageRowCannotPersistWithNullLanguage()
    {
        var connectionString = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_DB_CONNECTION")!;
        var options = new DbContextOptionsBuilder<AccessLearnDbContext>().UseNpgsql(connectionString).Options;
        await using var database = new AccessLearnDbContext(options, TimeProvider.System);
        var ownerId = Guid.CreateVersion7();
        var email = $"constraint-{ownerId:N}@accesslearn.test";
        database.Users.Add(new ApplicationUser
        {
            Id = ownerId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            DisplayName = "Constraint Test Owner",
            EmailConfirmed = true
        });
        var lesson = new Lesson(ownerId, "Constraint lesson", LanguageTags.English);
        var file = new StoredFile(
            $"test/{Guid.NewGuid():N}.mp4",
            "sign.mp4",
            "video/mp4",
            10,
            new string('a', 64));
        file.MarkReady("etag");
        database.Lessons.Add(lesson);
        database.StoredFiles.Add(file);
        await database.SaveChangesAsync();

        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                database.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO media_assets
                        (id, lesson_id, stored_file_id, role, language, duration_milliseconds,
                         width, height, codec, sync_offset_milliseconds, status, is_active,
                         created_at, updated_at, version)
                    VALUES
                        ({{Guid.CreateVersion7()}}, {{lesson.Id}}, {{file.Id}}, 'SignLanguage', NULL, 1000,
                         320, 240, 'h264', 0, 'Ready', TRUE,
                         {{DateTimeOffset.UtcNow}}, {{DateTimeOffset.UtcNow}}, 1)
                    """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("ck_media_assets_sign_language", exception.ConstraintName);
        }
        finally
        {
            await database.Lessons.Where(value => value.Id == lesson.Id).ExecuteDeleteAsync();
            await database.StoredFiles.Where(value => value.Id == file.Id).ExecuteDeleteAsync();
            await database.Users.Where(value => value.Id == ownerId).ExecuteDeleteAsync();
        }
    }
}
