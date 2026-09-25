using CommunityToolkit.Mvvm.ComponentModel;

namespace Stramp.App.ViewModels;

/// <summary>
/// A row in the "Add to Playlist" picker representing an existing playlist.
/// </summary>
public partial class PlaylistPickerItem : ObservableObject
{
    public string Name { get; }
    public string? FilePath { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    public partial int SongCount { get; set; }

    public string CountText => SongCount == 1 ? "1 song" : $"{SongCount} songs";

    [ObservableProperty]
    public partial bool IsAlreadyAdded { get; set; }

    [ObservableProperty]
    public partial bool IsJustAdded { get; set; }

    public PlaylistPickerItem(string name, int songCount, bool isAlreadyAdded, string? filePath = null)
    {
        Name = name;
        SongCount = songCount;
        IsAlreadyAdded = isAlreadyAdded;
        FilePath = filePath;
    }
}
