using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;

/// <summary>What a source row stands for, which decides its icon and sort options.</summary>
public enum LibrarySourceKind
{
    AllSongs,
    Folder,
    Playlist,
}

/// <summary>An entry in the library's source menu: "All Songs", one library folder, or one playlist.</summary>
public sealed partial class LibrarySourceRow(
    string name, IReadOnlyList<Song> songs, LibrarySourceKind kind = LibrarySourceKind.Playlist, string? filePath = null)
    : ObservableObject
{
    /// <summary>How many distinct covers it takes to fill the 2x2 collage.</summary>
    internal const int CollageCovers = 4;

    /// <summary>Songs inspected for cover art. Some have none, so this reaches past the four needed.</summary>
    private const int CoverCandidateLimit = 12;

    public string Name { get; } = name;
    public IReadOnlyList<Song> Songs { get; } = songs;
    public LibrarySourceKind Kind { get; } = kind;
    public string? FilePath { get; } = filePath;
    public bool IsPlaylist => Kind == LibrarySourceKind.Playlist;
    public string CountText => Songs.Count == 1 ? "1 song" : $"{Songs.Count} songs";

    /// <summary>"12 songs · 48 min", for the header above an opened source.</summary>
    public string SummaryText
    {
        get
        {
            var total = TimeSpan.FromTicks(Songs.Sum(song => song.Duration.Ticks));
            var length = total.TotalHours >= 1
                ? $"{(int)total.TotalHours} hr {total.Minutes} min"
                : $"{Math.Max(1, (int)Math.Round(total.TotalMinutes))} min";
            return Songs.Count == 0 ? CountText : $"{CountText} · {length}";
        }
    }

    public Geometry Icon => Kind switch
    {
        LibrarySourceKind.AllSongs => Icons.LibraryMusic,
        LibrarySourceKind.Folder => Icons.FolderOpen,
        _ => Icons.Playlist,
    };

    /// <summary>Playlists and All Songs get a collage thumbnail; folders show their icon.</summary>
    public bool HasCover => Kind != LibrarySourceKind.Folder;

    /// <summary>The collage thumbnail. Null until built, and for sources without one.</summary>
    [ObservableProperty]
    public partial Bitmap? Cover { get; set; }

    internal bool CoverRequested { get; set; }

    /// <summary>True when <see cref="Cover"/> is a picture the user chose, not the automatic collage.</summary>
    [ObservableProperty]
    public partial bool HasCustomCover { get; set; }

    /// <summary>True while this is the source the queue came from and audio is playing, so its
    /// Play buttons show Pause and pause it instead of starting it over.</summary>
    [ObservableProperty]
    public partial bool IsPlayingNow { get; set; }

    /// <summary>Songs to take cover art from, one per distinct album so a single-album source
    /// doesn't become four copies of the same picture. A playlist uses its own order (so the
    /// cover is its first albums). All Songs has no meaningful order, so it picks by a hash of the
    /// album: the same albums win no matter how the library happens to be scanned.</summary>
    internal IReadOnlyList<string> CoverCandidatePaths() =>
        Kind == LibrarySourceKind.Playlist ? FirstSongPerAlbum() : StableSongPerAlbum();

    private List<string> StableSongPerAlbum()
    {
        var pick = new Dictionary<string, Song>(StringComparer.OrdinalIgnoreCase);
        foreach (var song in Songs)
        {
            // No album tag means nothing to group by, so such a song stands for itself.
            var key = string.IsNullOrWhiteSpace(song.Album) ? "\0" + song.Path : song.Album;
            if (!pick.TryGetValue(key, out var existing) ||
                string.Compare(song.Path, existing.Path, StringComparison.OrdinalIgnoreCase) < 0)
                pick[key] = song;
        }

        return pick
            .OrderBy(entry => StableHash(entry.Key))
            .ThenBy(entry => entry.Value.Path, StringComparer.OrdinalIgnoreCase)
            .Take(CoverCandidateLimit)
            .Select(entry => entry.Value.Path)
            .ToList();
    }

    /// <summary>FNV-1a over the lowercased text. string.GetHashCode is randomized per process, which
    /// would pick different covers every launch.</summary>
    private static ulong StableHash(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in text)
        {
            hash = (hash ^ char.ToLowerInvariant(character)) * 1099511628211UL;
        }
        return hash;
    }

    private List<string> FirstSongPerAlbum()
    {
        var seenAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        foreach (var song in Songs)
        {
            // Songs with no album tag each count as their own, since there is nothing to compare.
            if (!string.IsNullOrWhiteSpace(song.Album) && !seenAlbums.Add(song.Album))
                continue;

            paths.Add(song.Path);
            if (paths.Count == CoverCandidateLimit)
                break;
        }
        return paths;
    }
}
