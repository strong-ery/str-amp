using Stramp.Core.Models;
using Stramp.Core.Settings;

namespace Stramp.Core.Library;

/// <summary>
/// Parses .m3u/.m3u8 path lists, resolves them against the library (or disk), and discovers
/// playlists that still live under the music folder.
/// </summary>
public static class PlaylistScanner
{
    public static List<Playlist> Scan(string musicDir, IReadOnlyList<Song> library)
    {
        var byPath = IndexByPath(library);
        var playlists = new List<Playlist>();

        List<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(musicDir, "*.*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                })
                .Where(f => f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return playlists;
        }

        foreach (var file in files)
        {
            var songs = ResolveSongs(ParsePaths(file), byPath);
            if (songs.Count > 0)
            {
                playlists.Add(new Playlist
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    Songs = songs,
                });
            }
        }

        playlists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return playlists;
    }

    /// <summary>
    /// Reads absolute file paths from an .m3u/.m3u8. Relative entries are resolved against the
    /// playlist file's directory. Comments and blank lines are skipped.
    /// </summary>
    public static List<string> ParsePaths(string playlistFile)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(playlistFile);
        }
        catch
        {
            return paths;
        }

        var baseDir = Path.GetDirectoryName(playlistFile) ?? string.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Skip network streams — we only import local files.
            if (line.Contains("://", StringComparison.Ordinal))
                continue;

            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(line) ? line : Path.Combine(baseDir, line));
            }
            catch
            {
                continue;
            }

            if (seen.Add(full))
                paths.Add(full);
        }

        return paths;
    }

    /// <summary>Builds playlists from previously imported path lists without touching the original m3u.</summary>
    public static List<Playlist> FromSaved(IEnumerable<SavedPlaylist> saved, IReadOnlyList<Song> library)
    {
        var byPath = IndexByPath(library);
        var playlists = new List<Playlist>();

        foreach (var entry in saved)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.SongPaths.Count == 0)
                continue;

            var songs = ResolveSongs(entry.SongPaths, byPath);
            if (songs.Count == 0)
                continue;

            playlists.Add(new Playlist { Name = entry.Name, Songs = songs });
        }

        playlists.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return playlists;
    }

    /// <summary>
    /// Maps playlist paths to Song objects: prefer an already-scanned library entry, otherwise
    /// read tags from disk when the file still exists.
    /// </summary>
    public static List<Song> ResolveSongs(IEnumerable<string> paths, IReadOnlyList<Song> library) =>
        ResolveSongs(paths, IndexByPath(library));

    private static List<Song> ResolveSongs(IEnumerable<string> paths, Dictionary<string, Song> byPath)
    {
        var songs = new List<Song>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            string full;
            try
            {
                full = Path.GetFullPath(raw);
            }
            catch
            {
                continue;
            }

            if (!seen.Add(full))
                continue;

            if (byPath.TryGetValue(full, out var song))
            {
                songs.Add(song);
                continue;
            }

            if (!File.Exists(full))
                continue;

            try
            {
                songs.Add(LibraryScanner.ReadSong(full));
            }
            catch
            {
                // Unreadable audio — skip this entry.
            }
        }

        return songs;
    }

    private static Dictionary<string, Song> IndexByPath(IReadOnlyList<Song> library)
    {
        var byPath = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in library)
        {
            try
            {
                byPath[Path.GetFullPath(song.Path)] = song;
            }
            catch
            {
                // Skip songs with unusable paths.
            }
        }

        return byPath;
    }
}
