using Avalonia.Media;
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
public sealed class LibrarySourceRow(
    string name, IReadOnlyList<Song> songs, LibrarySourceKind kind = LibrarySourceKind.Playlist, string? filePath = null)
{
    public string Name { get; } = name;
    public IReadOnlyList<Song> Songs { get; } = songs;
    public LibrarySourceKind Kind { get; } = kind;
    public string? FilePath { get; } = filePath;
    public bool IsPlaylist => Kind == LibrarySourceKind.Playlist;
    public string CountText => Songs.Count == 1 ? "1 song" : $"{Songs.Count} songs";

    public Geometry Icon => Kind switch
    {
        LibrarySourceKind.AllSongs => Icons.LibraryMusic,
        LibrarySourceKind.Folder => Icons.FolderOpen,
        _ => Icons.Playlist,
    };
}
