using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>
/// Finds .m3u/.m3u8 playlists under the library folder and resolves their entries against songs
/// we already scanned, so playlists show up without needing a separate import step.
/// </summary>
public static class PlaylistScanner
{
    public static List<Playlist> Scan(string musicDir, IReadOnlyList<Song> library)
    {
        var byPath = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in library)
            byPath[Path.GetFullPath(song.Path)] = song;

        var playlists = new List<Playlist>();

        IEnumerable<string> files;
        try
        {
            files = Directory
                .EnumerateFiles(musicDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return playlists;
        }

        foreach (var file in files)
        {
            var songs = ReadEntries(file, byPath);
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

    private static List<Song> ReadEntries(string playlistFile, Dictionary<string, Song> byPath)
    {
        var songs = new List<Song>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string[] lines;
        try
        {
            lines = File.ReadAllLines(playlistFile);
        }
        catch
        {
            return songs;
        }

        var baseDir = Path.GetDirectoryName(playlistFile) ?? string.Empty;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
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

            if (byPath.TryGetValue(full, out var song) && seen.Add(full))
                songs.Add(song);
        }

        return songs;
    }
}
