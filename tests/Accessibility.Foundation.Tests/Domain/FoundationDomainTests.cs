using Accessibility.Domain.Common;
using Accessibility.Domain.Entities;
using Accessibility.Domain.Enums;
using Accessibility.Infrastructure.Identity;
using Xunit;

namespace Accessibility.Foundation.Tests.Domain;

public sealed class FoundationDomainTests
{
    private const string Checksum = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void EntityIdsUseUuidVersion7()
    {
        var lesson = CreateLesson();

        Assert.Equal(7, lesson.Id.Version);
    }

    [Fact]
    public void ArchivedLessonRejectsAllMutationAndPublication()
    {
        var lesson = CreateLesson();
        var publication = CreatePublication(lesson.Id);
        lesson.Archive();

        Assert.Throws<InvalidOperationException>(() => lesson.UpdateDetails("Changed", null, LanguageTags.Amharic));
        Assert.Throws<InvalidOperationException>(lesson.AdvanceDraftRevision);
        Assert.Throws<InvalidOperationException>(() => lesson.Publish(publication));
    }

    [Fact]
    public void LessonRejectsPublicationOwnedByAnotherLesson()
    {
        var lesson = CreateLesson();
        var otherLesson = CreateLesson();
        var publication = CreatePublication(otherLesson.Id);

        Assert.Throws<ArgumentException>(() => lesson.Publish(publication));
    }

    [Fact]
    public void SignLanguageAssetRequiresEthiopianSignLanguageTag()
    {
        Assert.Throws<ArgumentException>(() => new MediaAsset(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            MediaAssetRole.SignLanguage,
            LanguageTags.Amharic));

        var asset = new MediaAsset(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            MediaAssetRole.SignLanguage,
            LanguageTags.EthiopianSignLanguage);

        Assert.Equal(LanguageTags.EthiopianSignLanguage, asset.Language);
    }

    [Fact]
    public void TranscriptSegmentRequiresForwardNonnegativeTiming()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranscriptSegment(
            Guid.CreateVersion7(), 0, -1, 10, "text"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TranscriptSegment(
            Guid.CreateVersion7(), 0, 10, 10, "text"));
    }

    [Fact]
    public void ImageDescriptionRequiresAltTextOrDecorativeStateExclusively()
    {
        var image = new DocumentImage(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "rId5",
            "body/paragraph[1]/image[1]",
            "source suggestion",
            suggestedDecorative: false);
        var reviewerId = Guid.CreateVersion7();
        var reviewedAt = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentException>(() => image.ApproveDescription(null, false, reviewerId, reviewedAt));
        Assert.Throws<ArgumentException>(() => image.ApproveDescription("description", true, reviewerId, reviewedAt));

        image.ApproveDescription("description", false, reviewerId, reviewedAt);
        Assert.Equal(ReviewStatus.Approved, image.ReviewStatus);
        Assert.Equal("description", image.AltText);
        Assert.Equal(reviewerId, image.ReviewedByUserId);
    }

    [Fact]
    public void DocumentOutputRecordsTruthfulFailureAndCanBeRegenerated()
    {
        var output = new DocumentOutput(
            Guid.CreateVersion7(),
            sourceRevision: 3,
            DocumentOutputKind.Pdf,
            Guid.CreateVersion7(),
            "weasyprint-70.0");
        var reportId = Guid.CreateVersion7();

        output.RecordFailure(
            reportId,
            "veraPDF",
            "1.30.4",
            "ua1",
            "{\"outcome\":\"operationalFailure\"}",
            DateTimeOffset.UtcNow);

        Assert.Equal(DocumentOutputStatus.Failed, output.Status);
        Assert.Equal(reportId, output.ValidationReportFileId);
        output.RetryGeneration(Guid.CreateVersion7(), "weasyprint-70.0");
        Assert.Equal(DocumentOutputStatus.Generated, output.Status);
        Assert.Null(output.ValidationReportFileId);
        Assert.Null(output.ValidatorName);
    }

    [Fact]
    public void JobCannotCompleteBeforeItStartsAndNormalizesUtcTimes()
    {
        var job = new ProcessingJob(
            ProcessingJobType.Transcribe,
            "job-key",
            lessonId: Guid.CreateVersion7());
        var localTime = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.FromHours(3));

        Assert.Throws<InvalidOperationException>(() => job.Succeed(localTime));

        job.Start(localTime);
        job.Succeed(localTime.AddMinutes(1));

        Assert.Equal(TimeSpan.Zero, job.StartedAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, job.CompletedAt!.Value.Offset);
        Assert.Equal(ProcessingJobStatus.Succeeded, job.Status);
    }

    [Fact]
    public void IntegrationClientRejectsNonHttpsNonLoopbackOrigin()
    {
        Assert.Throws<ArgumentException>(() => new IntegrationClient(
            Guid.CreateVersion7(),
            "Client",
            "prefix",
            "hash",
            ["lessons:read"],
            ["http://example.com"]));
    }

    [Theory]
    [InlineData("", "", "", false, false)]
    [InlineData("admin@example.test", "", "", true, false)]
    [InlineData("admin@example.test", "Administrator", "Aa1!secure-password", true, true)]
    public void BootstrapConfigurationDistinguishesMissingPartialAndCompleteValues(
        string email,
        string displayName,
        string password,
        bool hasAny,
        bool isConfigured)
    {
        var options = new BootstrapAdminOptions
        {
            Email = email,
            DisplayName = displayName,
            Password = password
        };

        Assert.Equal(hasAny, options.HasAnyValue);
        Assert.Equal(isConfigured, options.IsConfigured);
    }

    private static Lesson CreateLesson()
    {
        return new Lesson(Guid.CreateVersion7(), "Lesson", LanguageTags.English);
    }

    private static LessonPublication CreatePublication(Guid lessonId)
    {
        return new LessonPublication(
            lessonId,
            1,
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Checksum,
            DateTimeOffset.UtcNow);
    }
}
