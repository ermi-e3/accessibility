using Accessibility.Application.Common;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Features.Media;
using Accessibility.Domain.Enums;
using Xunit;

namespace Accessibility.Foundation.Tests.Application;

public sealed class AssetUploadPolicyTests
{
    [Theory]
    [InlineData(MediaAssetRole.PrimaryVideo, 524288000)]
    [InlineData(MediaAssetRole.SignLanguage, 262144000)]
    [InlineData(MediaAssetRole.PrimaryAudio, 262144000)]
    [InlineData(MediaAssetRole.AudioDescription, 262144000)]
    public void SupportedRolesExposeFrozenV1Limits(MediaAssetRole role, long maximumBytes)
    {
        Assert.Equal(maximumBytes, AssetUploadPolicy.For(role).MaximumBytes);
    }

    [Theory]
    [InlineData(MediaAssetRole.PlaybackRendition)]
    [InlineData(MediaAssetRole.OfflineAudio)]
    [InlineData(MediaAssetRole.Poster)]
    public void GeneratedRolesCannotBeUploadedDirectly(MediaAssetRole role)
    {
        Assert.Throws<UnsupportedFileTypeException>(() => AssetUploadPolicy.For(role));
    }

    [Fact]
    public void WavSignatureMustMatchExtensionAndMime()
    {
        var rule = AssetUploadPolicy.For(MediaAssetRole.PrimaryAudio);
        var header = new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E'
        };

        Assert.Equal("audio/wav", rule.CanonicalContentType("lesson.wav", "audio/wav"));
        AssetUploadPolicy.ValidateSignature(rule, "lesson.wav", header);
        Assert.Throws<ContentTypeMismatchException>(() =>
            rule.CanonicalContentType("lesson.wav", "video/mp4"));
        Assert.Throws<FileContentInvalidException>(() =>
            AssetUploadPolicy.ValidateSignature(rule, "lesson.wav", "not-a-wave"u8));
    }

    [Fact]
    public void IsoBmffRequiresFtypBox()
    {
        var rule = AssetUploadPolicy.For(MediaAssetRole.PrimaryVideo);
        var valid = new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0, 0, 0, 0 };

        AssetUploadPolicy.ValidateSignature(rule, "lesson.mp4", valid);
        Assert.Throws<FileContentInvalidException>(() =>
            AssetUploadPolicy.ValidateSignature(rule, "lesson.mp4", new byte[12]));
    }

    [Fact]
    public void ProbeMetadataMustMatchBrowserPlayableContainerAndCodec()
    {
        AssetUploadPolicy.ValidateProbe(
            "lesson.webm",
            new MediaProbeResult(1_000, true, true, 640, 360, ["vp9"], ["opus"], "matroska,webm"));

        Assert.Throws<FileContentInvalidException>(() => AssetUploadPolicy.ValidateProbe(
            "lesson.webm",
            new MediaProbeResult(1_000, true, true, 640, 360, ["h264"], ["opus"], "matroska,webm")));
        Assert.Throws<FileContentInvalidException>(() => AssetUploadPolicy.ValidateProbe(
            "lesson.mp4",
            new MediaProbeResult(1_000, true, true, 640, 360, ["h264"], ["aac"], "matroska")));
        Assert.Throws<FileContentInvalidException>(() => AssetUploadPolicy.ValidateProbe(
            "renamed-video.m4a",
            new MediaProbeResult(1_000, true, true, 640, 360, ["h264"], ["aac"], "mov,mp4,m4a")));
        Assert.Throws<FileContentInvalidException>(() => AssetUploadPolicy.ValidateProbe(
            "renamed-video.ogg",
            new MediaProbeResult(1_000, true, true, 640, 360, ["theora"], ["vorbis"], "ogg")));
    }
}
