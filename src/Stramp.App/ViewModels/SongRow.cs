using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;

/// <summary>Wraps a Song for display in a list, with an "is this the one playing" flag for row highlighting.</summary>
public partial class SongRow(Song song) : ObservableObject
{
    public Song Song { get; } = song;

    public string Title => Song.Title;
    public string Artist => Song.Artist;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Thumbnail for queue and library rows. Null until loaded.</summary>
    [ObservableProperty]
    public partial Bitmap? ArtBitmap { get; set; }
}
