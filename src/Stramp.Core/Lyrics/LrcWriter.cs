using System.Globalization;
using System.Text;

namespace Stramp.Core.Lyrics;

/// <summary>Turns edited lines back into .lrc text: timestamped when synced, bare lines otherwise.</summary>
public static class LrcWriter
{
    /// <summary>Hundredths, which is what every .lrc consumer understands.</summary>
    public static string FormatTime(double seconds)
    {
        var centis = (long)Math.Round(Math.Max(0, seconds) * 100);
        var minutes = centis / 6000;
        var rest = centis % 6000;
        return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{rest / 100:00}.{rest % 100:00}");
    }

    /// <summary>Synced text: lines without a time are written as plain text so nothing is lost.</summary>
    public static string WriteSynced(IEnumerable<LyricLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.TimeSeconds is { } time)
                builder.Append('[').Append(FormatTime(time)).Append("] ");
            builder.Append(line.Text.Trim()).Append('\n');
        }
        return builder.ToString().TrimEnd('\n') + "\n";
    }

    public static string WritePlain(IEnumerable<LyricLine> lines) =>
        string.Join('\n', lines.Select(line => line.Text.Trim())).Trim('\n') + "\n";

    /// <summary>The plain version of lines that may be synced, which LRCLIB wants alongside the synced one.</summary>
    public static string ToPlain(IEnumerable<LyricLine> lines) =>
        WritePlain(lines.Where(line => !string.IsNullOrWhiteSpace(line.Text)));
}
