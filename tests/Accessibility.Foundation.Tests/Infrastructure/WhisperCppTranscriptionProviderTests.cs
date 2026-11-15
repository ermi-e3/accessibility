using Accessibility.Infrastructure.Processing;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class WhisperCppTranscriptionProviderTests
{
    [Fact]
    public void ParsesTimestampedWhisperJsonAndDropsEmptySegments()
    {
        const string json = """
            {
              "transcription": [
                { "offsets": { "from": 0, "to": 1250 }, "text": " Welcome " },
                { "offsets": { "from": 1250, "to": 2500 }, "text": "" },
                { "offsets": { "from": 2500, "to": 4000 }, "text": "Second" }
              ]
            }
            """;

        var transcript = WhisperCppTranscriptionProvider.Parse(json, "en");

        Assert.Equal("en", transcript.Language);
        Assert.Equal(2, transcript.Segments.Count);
        Assert.Equal("Welcome", transcript.Segments[0].Text);
        Assert.Equal(4_000, transcript.Segments[1].EndMilliseconds);
    }
}
