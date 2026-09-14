using Stramp.Core.Models;

namespace Stramp.App.ViewModels;

/// <summary>An entry in the library's source menu: "All Songs" or one playlist.</summary>
public sealed class LibrarySourceRow(string name, IReadOnlyList<Song> songs)
{
    public string Name { get; } = name;
    public IReadOnlyList<Song> Songs { get; } = songs;
    public string CountText => Songs.Count == 1 ? "1 song" : $"{Songs.Count} songs";
}
