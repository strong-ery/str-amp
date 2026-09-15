using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>Recursively finds audio files under a directory and reads their tags.</summary>
public static class LibraryScanner
{
    private static readonly string[] Extensions =
        [".mp3", ".flac", ".ogg", ".opus", ".m4a", ".wav"];

    public static List<Song> Scan(string musicDir)
        => Scan([musicDir]);

    /// <summary>Combines several roots into one library and ignores duplicate files.</summary>
    public static List<Song> Scan(IEnumerable<string> musicDirs)
    {
        var songs = new List<Song>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var musicDir in musicDirs)
        {
            if (string.IsNullOrWhiteSpace(musicDir) || !Directory.Exists(musicDir))
                continue;

            try
            {
                foreach (var file in Directory.EnumerateFiles(
                             musicDir, "*", new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                                 ReturnSpecialDirectories = false,
                             }))
                {
                    if (!Extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        continue;

                    var fullPath = Path.GetFullPath(file);
                    if (seenFiles.Add(fullPath))
                        songs.Add(ReadSong(fullPath));
                }
            }
            catch (IOException)
            {
                // A removable/network source can disappear while it is being scanned.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip an inaccessible source without losing the other configured roots.
            }
        }

        songs.Sort((a, b) =>
        {
            var byArtist = string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
            return byArtist != 0
                ? byArtist
                : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        return songs;
    }

    /// <summary>Reads tags for a single file, falling back to the "Artist - Title" filename convention.</summary>
    public static Song ReadSong(string path)
    {
        string? title = null;
        string? artist = null;
        var album = "";
        var duration = TimeSpan.Zero;

        try
        {
            using var tagFile = TagLib.File.Create(path);
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.Title))
                title = tagFile.Tag.Title;
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.FirstPerformer))
                artist = tagFile.Tag.FirstPerformer;
            if (!string.IsNullOrWhiteSpace(tagFile.Tag.Album))
                album = tagFile.Tag.Album;
            duration = tagFile.Properties?.Duration ?? TimeSpan.Zero;
        }
        catch
        {
            // Corrupt/unreadable tags — fall back to filename below.
        }

        if (title is null || artist is null)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var sepIndex = name.IndexOf(" - ", StringComparison.Ordinal);
            if (sepIndex >= 0)
            {
                artist ??= name[..sepIndex].Trim();
                title ??= name[(sepIndex + 3)..].Trim();
            }
            else
            {
                artist ??= "Unknown";
                title ??= name;
            }
        }

        return new Song
        {
            Path = path, Title = title, Artist = artist, Album = album, Duration = duration,
        };
    }
}
