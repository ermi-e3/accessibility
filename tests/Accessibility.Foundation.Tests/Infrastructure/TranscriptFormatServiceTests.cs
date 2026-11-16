using Accessibility.Application.Abstractions;
using Accessibility.Infrastructure.Processing;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class TranscriptFormatServiceTests
{
    private readonly TranscriptFormatService _formats = new();

    [Theory]
    [InlineData(TranscriptExchangeFormat.WebVtt)]
    [InlineData(TranscriptExchangeFormat.Srt)]
    public void TimestampedFormatsRoundTrip(TranscriptExchangeFormat format)
    {
        ProviderTranscriptSegment[] segments =
        [
            new(0, 0, 1_250, "First caption"),
            new(1, 1_500, 2_750, "Second caption", "Educator")
        ];

        var serialized = _formats.Serialize(segments, format);
        var parsed = _formats.Parse(serialized, format, 3_000);

        Assert.Equal(segments.Select(segment => segment.Text), parsed.Select(segment => segment.Text));
        Assert.Equal(segments.Select(segment => segment.StartMilliseconds), parsed.Select(segment => segment.StartMilliseconds));
    }

    [Fact]
    public void RejectsOverlappingOrUnsynchronizedImport()
    {
        const string overlapping = """
            WEBVTT

            1
            00:00:00.000 --> 00:00:02.000
            First

            2
            00:00:01.500 --> 00:00:03.000
            Second
            """;

        Assert.ThrowsAny<Exception>(() =>
            _formats.Parse(overlapping, TranscriptExchangeFormat.WebVtt, 5_000));
        Assert.ThrowsAny<Exception>(() =>
            _formats.Parse("plain text", TranscriptExchangeFormat.Text, 5_000));
        Assert.ThrowsAny<Exception>(() =>
            _formats.Parse(
                "00:00.000 --> 00:01.000\nMissing header",
                TranscriptExchangeFormat.WebVtt,
                5_000));
    }
}
