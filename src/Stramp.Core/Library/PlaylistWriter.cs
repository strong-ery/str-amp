using System.Text;
using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>
/// Creates and writes .m3u8 playlist files complying with the extended M3U specification (UTF-8).
/// </summary>
public static class PlaylistWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    /// <summary>
    /// Generates standard #EXTM3U formatted content for the given songs.
    /// </summary>
    public static string ToM3u8(IEnumerable<Song> songs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var song in songs)
        {
            var seconds = (int)Math.Round(song.Duration.TotalSeconds);
            if (seconds <= 0)
                seconds = -1;

            var artist = song.Artist?.Trim() ?? string.Empty;
            var title = song.Title?.Trim() ?? string.Empty;
            var label = string.IsNullOrEmpty(artist)
                ? (string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(song.Path) : title)
                : $"{artist} - {title}";

            sb.AppendLine($"#EXTINF:{seconds},{label}");
            sb.AppendLine(song.Path);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates standard #EXTM3U formatted content for raw file paths.
    /// </summary>
    public static string ToM3u8FromPaths(IEnumerable<string> paths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");

        foreach (var path in paths)
        {
            var label = Path.GetFileNameWithoutExtension(path);
            sb.AppendLine($"#EXTINF:-1,{label}");
            sb.AppendLine(path);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Writes the songs to the specified .m3u8 file path using UTF-8 without BOM.
    /// Creates directory if needed.
    /// </summary>
    public static void Write(string filePath, IEnumerable<Song> songs)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var content = ToM3u8(songs);
        File.WriteAllText(filePath, content, Utf8WithoutBom);
    }

    /// <summary>
    /// Writes the file paths to the specified .m3u8 file path using UTF-8 without BOM.
    /// </summary>
    public static void WriteFromPaths(string filePath, IEnumerable<string> paths)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var content = ToM3u8FromPaths(paths);
        File.WriteAllText(filePath, content, Utf8WithoutBom);
    }

    /// <summary>
    /// Appends a song entry to an existing .m3u8 file, or creates a new one if it does not exist.
    /// </summary>
    public static void Append(string filePath, Song song)
    {
        if (!File.Exists(filePath))
        {
            Write(filePath, [song]);
            return;
        }

        var seconds = (int)Math.Round(song.Duration.TotalSeconds);
        if (seconds <= 0)
            seconds = -1;

        var artist = song.Artist?.Trim() ?? string.Empty;
        var title = song.Title?.Trim() ?? string.Empty;
        var label = string.IsNullOrEmpty(artist)
            ? (string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(song.Path) : title)
            : $"{artist} - {title}";

        var entry = $"#EXTINF:{seconds},{label}{Environment.NewLine}{song.Path}{Environment.NewLine}";
        File.AppendAllText(filePath, entry, Utf8WithoutBom);
    }
}
