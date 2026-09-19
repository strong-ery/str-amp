namespace Stramp.App.ViewModels;

public enum LibrarySortOption
{
    Artist,
    Album,
    Song,
    AlbumArtist,
    Playlist,
}

public sealed class SortOptionRow
{
    public LibrarySortOption Option { get; }
    public string Name { get; }

    public SortOptionRow(LibrarySortOption option, string name)
    {
        Option = option;
        Name = name;
    }

    public override string ToString() => Name;

    public override bool Equals(object? obj) =>
        obj is SortOptionRow other && Option == other.Option;

    public override int GetHashCode() => Option.GetHashCode();
}
