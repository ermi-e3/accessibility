using Accessibility.Application.Common;
using Accessibility.Application.Abstractions;
using Accessibility.Domain.Enums;

namespace Accessibility.Application.Features.Media;

public sealed record AssetUploadRule(
    MediaAssetRole Role,
    long MaximumBytes,
    IReadOnlyDictionary<string, string> ExtensionContentTypes,
    bool RequiresVideo,
    bool RequiresAudio)
{
    public string CanonicalExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return ExtensionContentTypes.ContainsKey(extension)
            ? extension
            : throw new UnsupportedFileTypeException($"Extension '{extension}' is not supported for role '{Role}'.");
    }

    public string CanonicalContentType(string fileName, string declaredContentType)
    {
        var extension = CanonicalExtension(fileName);
        var expected = ExtensionContentTypes[extension];
        if (!string.Equals(expected, declaredContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentTypeMismatchException(
                $"Content type '{declaredContentType}' does not match extension '{extension}'.");
        }

        return expected;
    }
}

public static class AssetUploadPolicy
{
    public const long MaximumPrimaryVideoBytes = 524_288_000;
    public const long MaximumSecondaryVideoBytes = 262_144_000;
    public const long MaximumAudioBytes = 262_144_000;
    public const long MultipartOverheadBytes = 1_048_576;
    public const long AbsoluteRequestLimit = MaximumPrimaryVideoBytes + MultipartOverheadBytes;

    private static readonly IReadOnlyDictionary<string, string> VideoTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm"
        };

    private static readonly IReadOnlyDictionary<string, string> AudioTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".mp3"] = "audio/mpeg",
            [".wav"] = "audio/wav",
            [".m4a"] = "audio/mp4",
            [".ogg"] = "audio/ogg",
            [".opus"] = "audio/ogg"
        };

    public static AssetUploadRule For(MediaAssetRole role)
    {
        return role switch
        {
            MediaAssetRole.PrimaryVideo =>
                new AssetUploadRule(role, MaximumPrimaryVideoBytes, VideoTypes, RequiresVideo: true, RequiresAudio: false),
            MediaAssetRole.SignLanguage =>
                new AssetUploadRule(role, MaximumSecondaryVideoBytes, VideoTypes, RequiresVideo: true, RequiresAudio: false),
            MediaAssetRole.PrimaryAudio or MediaAssetRole.AudioDescription =>
                new AssetUploadRule(role, MaximumAudioBytes, AudioTypes, RequiresVideo: false, RequiresAudio: true),
            _ => throw new UnsupportedFileTypeException($"Role '{role}' cannot be uploaded directly.")
        };
    }

    public static void ValidateSignature(AssetUploadRule rule, string fileName, ReadOnlySpan<byte> header)
    {
        var extension = rule.CanonicalExtension(fileName);
        var valid = extension switch
        {
            ".mp4" or ".m4a" =>
                header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8),
            ".webm" =>
                header.Length >= 4 && header[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }),
            ".wav" =>
                header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8),
            ".ogg" or ".opus" =>
                header.Length >= 4 && header[..4].SequenceEqual("OggS"u8),
            ".mp3" =>
                header.Length >= 3 && header[..3].SequenceEqual("ID3"u8) ||
                header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0,
            _ => false
        };

        if (!valid)
        {
            throw new FileContentInvalidException("The file signature does not match its declared type.");
        }
    }

    public static void ValidateProbe(string fileName, MediaProbeResult probe)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var containers = probe.Container.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var videoCodecs = probe.VideoCodecs.Select(codec => codec.ToLowerInvariant()).ToArray();
        var audioCodecs = probe.AudioCodecs.Select(codec => codec.ToLowerInvariant()).ToArray();
        var valid = extension switch
        {
            ".mp4" => containers.Contains("mp4") &&
                       videoCodecs.Length > 0 && videoCodecs.All(codec => codec == "h264") &&
                       audioCodecs.All(codec => codec == "aac"),
            ".webm" => containers.Contains("webm") &&
                        videoCodecs.Length > 0 && videoCodecs.All(codec => codec is "vp8" or "vp9" or "av1") &&
                        audioCodecs.All(codec => codec is "opus" or "vorbis"),
            ".m4a" => (containers.Contains("m4a") || containers.Contains("mp4")) &&
                       videoCodecs.Length == 0 &&
                       audioCodecs.Length > 0 && audioCodecs.All(codec => codec == "aac"),
            ".mp3" => containers.Contains("mp3") &&
                       videoCodecs.Length == 0 &&
                       audioCodecs.Length > 0 && audioCodecs.All(codec => codec == "mp3"),
            ".wav" => containers.Contains("wav") &&
                       videoCodecs.Length == 0 &&
                       audioCodecs.Length > 0 && audioCodecs.All(codec => codec.StartsWith("pcm_", StringComparison.Ordinal)),
            ".ogg" => containers.Contains("ogg") &&
                       videoCodecs.Length == 0 &&
                       audioCodecs.Length > 0 && audioCodecs.All(codec => codec is "opus" or "vorbis" or "flac"),
            ".opus" => containers.Contains("ogg") &&
                        videoCodecs.Length == 0 &&
                        audioCodecs.Length > 0 && audioCodecs.All(codec => codec == "opus"),
            _ => false
        };

        if (!valid)
        {
            throw new FileContentInvalidException(
                "The detected media container or codec does not match the uploaded file type.");
        }
    }
}
