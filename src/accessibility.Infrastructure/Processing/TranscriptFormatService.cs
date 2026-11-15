using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Accessibility.Application.Abstractions;
using Accessibility.Application.Common;

namespace Accessibility.Infrastructure.Processing;

public sealed partial class TranscriptFormatService : ITranscriptFormatService
{
    public IReadOnlyList<ProviderTranscriptSegment> Parse(
        string content,
        TranscriptExchangeFormat format,
        long? mediaDurationMilliseconds)
    {
        if (format == TranscriptExchangeFormat.Text)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["format"] = ["Plain text import is unsynchronized. Import WebVTT or SRT instead."]
            });
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (format == TranscriptExchangeFormat.WebVtt)
        {
            if (!WebVttHeader().IsMatch(normalized)) throw InvalidFormat();
            normalized = WebVttHeader().Replace(normalized, string.Empty, 1).Trim();
        }

        var blocks = BlankLine().Split(normalized);
        var segments = new List<ProviderTranscriptSegment>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var timingIndex = Array.FindIndex(lines, line => line.Contains(" --> ", StringComparison.Ordinal));
            if (timingIndex < 0)
            {
                if (format == TranscriptExchangeFormat.WebVtt && lines.Length > 0 &&
                    (lines[0].StartsWith("NOTE", StringComparison.Ordinal) ||
                     lines[0] is "STYLE" or "REGION"))
                {
                    continue;
                }
                throw InvalidFormat();
            }
            if (timingIndex == lines.Length - 1) throw InvalidFormat();
            var timing = lines[timingIndex].Split(" --> ", StringSplitOptions.TrimEntries);
            if (timing.Length != 2) throw InvalidFormat();
            if (format == TranscriptExchangeFormat.WebVtt && timingIndex > 1)
            {
                throw InvalidFormat();
            }
            if (format == TranscriptExchangeFormat.Srt &&
                (timingIndex != 1 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
                  timing[0].Count(character => character == ':') != 2 ||
                 timing[1].Count(character => character == ':') != 2 || timing[1].Contains(' ')))
            {
                throw InvalidFormat();
            }
            long start;
            long end;
            try
            {
                start = ParseTime(timing[0], format);
                end = ParseTime(timing[1].Split(' ', 2)[0], format);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                throw InvalidFormat();
            }
            var text = string.Join("\n", lines[(timingIndex + 1)..]).Trim();
            if (start < 0 || end <= start || string.IsNullOrWhiteSpace(text) ||
                mediaDurationMilliseconds.HasValue && end > mediaDurationMilliseconds.Value)
            {
                throw InvalidFormat();
            }

            if (segments.Count > 0 && start < segments[^1].EndMilliseconds) throw InvalidFormat();
            segments.Add(new ProviderTranscriptSegment(segments.Count, start, end, text));
        }

        return segments.Count > 0 ? segments : throw InvalidFormat();
    }

    public string Serialize(
        IReadOnlyList<ProviderTranscriptSegment> segments,
        TranscriptExchangeFormat format)
    {
        if (format == TranscriptExchangeFormat.Text)
        {
            return string.Join(Environment.NewLine, segments.Select(segment => segment.Text));
        }

        var output = new StringBuilder();
        if (format == TranscriptExchangeFormat.WebVtt) output.AppendLine("WEBVTT").AppendLine();
        foreach (var segment in segments)
        {
            output.AppendLine((segment.Sequence + 1).ToString(CultureInfo.InvariantCulture));
            output.Append(FormatTime(segment.StartMilliseconds, format));
            output.Append(" --> ");
            output.AppendLine(FormatTime(segment.EndMilliseconds, format));
            var cueText = string.Join(
                "\n",
                segment.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            if (string.IsNullOrWhiteSpace(cueText)) throw InvalidFormat();
            output.AppendLine(cueText.Replace("-->", "-- >", StringComparison.Ordinal));
            output.AppendLine();
        }

        return output.ToString();
    }

    private static long ParseTime(string value, TranscriptExchangeFormat format)
    {
        var original = value.Trim();
        var valid = format == TranscriptExchangeFormat.Srt
            ? SrtTimestamp().IsMatch(original)
            : VttTimestamp().IsMatch(original);
        if (!valid) throw InvalidFormat();
        var normalized = original.Replace(',', '.');
        var parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3) throw InvalidFormat();
        var hours = parts.Length == 3 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        return checked((long)Math.Round((hours * 3600 + minutes * 60 + seconds) * 1000));
    }

    private static string FormatTime(long milliseconds, TranscriptExchangeFormat format)
    {
        var time = TimeSpan.FromMilliseconds(milliseconds);
        var separator = format == TranscriptExchangeFormat.Srt ? ',' : '.';
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";
    }

    private static RequestValidationException InvalidFormat() =>
        new(new Dictionary<string, string[]> { ["file"] = ["Caption timestamps or cue text are invalid."] });

    [GeneratedRegex("^WEBVTT(?:[ \t].*)?\n+", RegexOptions.IgnoreCase)]
    private static partial Regex WebVttHeader();

    [GeneratedRegex("\n[ \t]*\n+")]
    private static partial Regex BlankLine();

    [GeneratedRegex("^(?:[0-9]{2,}:)?[0-5][0-9]:[0-5][0-9]\.[0-9]{3}$")]
    private static partial Regex VttTimestamp();

    [GeneratedRegex("^[0-9]{2,}:[0-5][0-9]:[0-5][0-9],[0-9]{3}$")]
    private static partial Regex SrtTimestamp();
}
