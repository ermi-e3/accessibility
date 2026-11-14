using System.Text;
using Accessibility.Application.Common;
using Accessibility.Infrastructure.Processing;
using Microsoft.Extensions.Options;
using Xunit;

namespace Accessibility.Foundation.Tests.Infrastructure;

public sealed class FfprobeMediaProbeTests
{
    [Fact]
    public void ParsesStringValuedFfprobeMetadata()
    {
        const string json = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 360 },
                { "codec_type": "audio", "codec_name": "aac" }
              ],
              "format": { "format_name": "mov,mp4", "duration": "1.250000" }
            }
            """;

        var result = FfprobeMediaProbe.Parse(json);

        Assert.Equal(1_250, result.DurationMilliseconds);
        Assert.True(result.HasVideo);
        Assert.True(result.HasAudio);
        Assert.Equal(640, result.Width);
        Assert.Equal(new[] { "h264" }, result.VideoCodecs);
        Assert.Equal(new[] { "aac" }, result.AudioCodecs);
    }

    [Fact]
    public void IgnoresAttachedCoverArtWhenClassifyingAudio()
    {
        const string json = """
            {
              "streams": [
                {
                  "codec_type": "video",
                  "codec_name": "mjpeg",
                  "width": 600,
                  "height": 600,
                  "disposition": { "attached_pic": 1 }
                },
                { "codec_type": "audio", "codec_name": "mp3" }
              ],
              "format": { "format_name": "mp3", "duration": "1.000000" }
            }
            """;

        var result = FfprobeMediaProbe.Parse(json);

        Assert.False(result.HasVideo);
        Assert.True(result.HasAudio);
        Assert.Empty(result.VideoCodecs);
        Assert.Equal(new[] { "mp3" }, result.AudioCodecs);
    }

    [FfprobeIntegrationFact]
    public async Task ProbesRightsSafeGeneratedWaveFile()
    {
        var executable = Environment.GetEnvironmentVariable("ACCESSLEARN_TEST_FFPROBE_PATH")!;
        var filePath = Path.Combine(Path.GetTempPath(), $"accesslearn-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(filePath, CreateSilentWave(milliseconds: 100));
            var probe = new FfprobeMediaProbe(Options.Create(new FfprobeOptions
            {
                ExecutablePath = executable,
                TimeoutSeconds = 30
            }));

            var result = await probe.ProbeAsync(filePath, CancellationToken.None);

            Assert.True(result.HasAudio);
            Assert.False(result.HasVideo);
            Assert.InRange(result.DurationMilliseconds, 90, 110);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task BoundedReaderRejectsExcessOutputAndHonorsCancellation()
    {
        await using var oversized = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65)));
        using var oversizedReader = new StreamReader(oversized);
        await Assert.ThrowsAsync<ProcessorUnavailableException>(() =>
            FfprobeMediaProbe.ReadBoundedAsync(oversizedReader, 64, CancellationToken.None));

        await using var cancelled = new MemoryStream(Encoding.UTF8.GetBytes("content"));
        using var cancelledReader = new StreamReader(cancelled);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FfprobeMediaProbe.ReadBoundedAsync(cancelledReader, 64, cancellation.Token));
    }

    private static byte[] CreateSilentWave(int milliseconds)
    {
        const int sampleRate = 8_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = sampleRate * milliseconds / 1_000;
        var dataLength = sampleCount * channels * bitsPerSample / 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        writer.Write(new byte[dataLength]);
        writer.Flush();
        return stream.ToArray();
    }
}
