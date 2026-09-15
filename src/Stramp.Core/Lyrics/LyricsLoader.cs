namespace Stramp.Core.Lyrics;

/// <summary>Finds the .lrc that sits beside a track and reads it.</summary>
public static class LyricsLoader
{
    public static LyricsDocument Load(string audioPath)
    {
        var path = FindLyricsFile(audioPath);
        if (path is null)
            return LyricsDocument.Empty;

        try
        {
            return LrcParser.Parse(File.ReadAllText(path));
        }
        catch
        {
            return LyricsDocument.Empty;
        }
    }

    /// <summary>The sibling file with the same name as the track but an .lrc extension, if it exists.</summary>
    public static string? FindLyricsFile(string audioPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(audioPath));
            if (string.IsNullOrEmpty(directory))
                return null;

            var expected = Path.GetFileNameWithoutExtension(audioPath) + ".lrc";
            var candidate = Path.Combine(directory, expected);
            if (File.Exists(candidate))
                return candidate;

            // Case-sensitive file systems need the scan; on Windows the check above already matched.
            return Directory
                .EnumerateFiles(directory, "*.lrc")
                .FirstOrDefault(file => string.Equals(
                    Path.GetFileName(file), expected, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
