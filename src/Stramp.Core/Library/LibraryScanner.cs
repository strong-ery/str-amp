using System.Collections.Concurrent;
using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>Recursively finds audio files under a directory and reads their tags.</summary>
public static class LibraryScanner
{
    private const int MetadataWorkers = 6;
    private static readonly string[] Extensions =
        [".mp3", ".flac", ".ogg", ".opus", ".m4a", ".wav"];

    public static List<Song> Scan(string musicDir)
        => Scan([musicDir], CancellationToken.None);

    /// <summary>Combines several roots into one library and ignores duplicate files.</summary>
    public static List<Song> Scan(IEnumerable<string> musicDirs)
        => Scan(musicDirs, CancellationToken.None);

    /// <summary>Combines roots while allowing a superseded background scan to stop promptly.</summary>
    public static List<Song> Scan(
        IEnumerable<string> musicDirs,
        CancellationToken cancellationToken,
        LibraryMetadataCache? metadataCache = null)
    {
        var songs = new ConcurrentBag<Song>();
        var uncachedFiles = new List<FileInfo>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var musicDir in musicDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(musicDir) || !Directory.Exists(musicDir))
                continue;

            try
            {
                foreach (var file in new DirectoryInfo(musicDir).EnumerateFiles(
                             "*", new EnumerationOptions
                             {
                                 RecurseSubdirectories = true,
                                 IgnoreInaccessible = true,
                                 ReturnSpecialDirectories = false,
                             }))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
                        continue;

                    if (!seenFiles.Add(file.FullName))
                        continue;

                    if (metadataCache is not null && metadataCache.TryGet(file, out var cached))
                        songs.Add(cached);
                    else
                        uncachedFiles.Add(file);
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

        try
        {
            Parallel.ForEach(uncachedFiles, new ParallelOptions
            {
                MaxDegreeOfParallelism = MetadataWorkers,
                CancellationToken = cancellationToken,
            }, file =>
            {
                var song = ReadSong(file.FullName);
                songs.Add(song);
                metadataCache?.Put(file, song);
            });
        }
        finally
        {
            // Preserve completed work even when a newer source change cancels this scan.
            metadataCache?.Save();
        }

        var sortedSongs = songs.ToList();
        sortedSongs.Sort((a, b) =>
        {
            var byArtist = string.Compare(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase);
            return byArtist != 0
                ? byArtist
                : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
        });

        return sortedSongs;
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
