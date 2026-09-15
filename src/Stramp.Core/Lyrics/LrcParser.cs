using System.Globalization;
using System.Text.RegularExpressions;

namespace Stramp.Core.Lyrics;

/// <summary>
/// Reads .lrc content. Handles the synced form (one or more <c>[mm:ss.xx]</c> tags per line),
/// the enhanced form (per-word <c>&lt;mm:ss.xx&gt;</c> tags, which are stripped for display), and
/// plain unsynced files, which are just the lyric text with no tags at all.
/// </summary>
public static partial class LrcParser
{
    /// <summary>A line timestamp, anchored so only a run of tags at the head of a line is consumed.</summary>
    [GeneratedRegex(@"\G\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimeTagRegex();

    /// <summary>A header tag such as <c>[ti:Title]</c>. The key is never numeric, so this can't eat a timestamp.</summary>
    [GeneratedRegex(@"^\[([A-Za-z#]{1,16}):(.*)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex MetaTagRegex();

    /// <summary>Enhanced-LRC word timings. We highlight whole lines, so these are dropped.</summary>
    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.CultureInvariant)]
    private static partial Regex WordTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRunRegex();

    public static LyricsDocument Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return LyricsDocument.Empty;

        var timed = new List<(double Time, string Text)>();
        var plain = new List<string>();
        var offsetSeconds = 0d;
        string? title = null;
        string? artist = null;

        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                plain.Add("");
                continue;
            }

            var times = ReadTimeTags(line, out var textStart);
            if (times.Count > 0)
            {
                var text = CleanText(line[textStart..]);
                foreach (var time in times)
                    timed.Add((time, text));
                continue;
            }

            if (MetaTagRegex().Match(line) is { Success: true } meta)
            {
                var value = meta.Groups[2].Value.Trim();
                switch (meta.Groups[1].Value.ToLowerInvariant())
                {
                    // Positive offsets mean the words land earlier than their timestamps say.
                    case "offset" when int.TryParse(
                        value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms):
                        offsetSeconds = ms / 1000.0;
                        break;
                    case "ti":
                        title = value;
                        break;
                    case "ar":
                        artist = value;
                        break;
                }
                continue;
            }

            plain.Add(CleanText(line));
        }

        if (timed.Count > 0)
        {
            // OrderBy is stable, so lines sharing a timestamp keep the order the file gave them.
            var lines = timed
                .OrderBy(entry => entry.Time)
                .Select(entry => new LyricLine(Math.Max(0, entry.Time - offsetSeconds), entry.Text))
                .ToList();
            return new LyricsDocument(lines, IsSynced: true, title, artist);
        }

        Tidy(plain);
        return plain.Count == 0
            ? LyricsDocument.Empty
            : new LyricsDocument(
                plain.Select(text => new LyricLine(null, text)).ToList(), IsSynced: false, title, artist);
    }

    /// <summary>Consumes the leading run of timestamps and reports where the lyric text starts.</summary>
    private static List<double> ReadTimeTags(string line, out int textStart)
    {
        var times = new List<double>();
        textStart = 0;

        while (textStart < line.Length && TimeTagRegex().Match(line, textStart) is { Success: true } match)
        {
            if (TryParseTime(match.Groups[1].Value, match.Groups[2].Value, out var seconds))
                times.Add(seconds);
            textStart = match.Index + match.Length;
        }

        return times;
    }

    /// <summary>
    /// Both <c>[mm:ss.xx]</c> and the older <c>[mm:ss:xx]</c> are accepted; the fraction is read at
    /// whatever precision it was written with (tenths, hundredths or milliseconds).
    /// </summary>
    private static bool TryParseTime(string minutes, string seconds, out double totalSeconds)
    {
        totalSeconds = 0;
        if (!int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var mins))
            return false;
        if (!double.TryParse(
                seconds.Replace(':', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return false;

        totalSeconds = mins * 60 + secs;
        return true;
    }

    private static string CleanText(string text) =>
        WhitespaceRunRegex().Replace(WordTagRegex().Replace(text, ""), " ").Trim();

    /// <summary>Drops blank padding at the edges and collapses runs of blanks to a single break.</summary>
    private static void Tidy(List<string> lines)
    {
        for (var i = lines.Count - 1; i > 0; i--)
        {
            if (lines[i].Length == 0 && lines[i - 1].Length == 0)
                lines.RemoveAt(i);
        }

        while (lines.Count > 0 && lines[0].Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
    }
}
