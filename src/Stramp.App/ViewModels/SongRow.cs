using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;

/// <summary>Wraps a Song for display as a row in the library or Up Next list.</summary>
/// <remarks>
/// Carries the flags a row is drawn with: whether it is the track playing, and, in Up Next, whether
/// it was added to the queue by hand. Its thumbnail is loaded lazily while a list container shows
/// the row, and the request bookkeeping lets recycled containers drop callbacks meant for an
/// earlier row.
/// </remarks>
public partial class SongRow(Song song) : ObservableObject
{
    public Song Song { get; } = song;

    public string Title => Song.Title;
    public string Artist => Song.Artist;

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    // Up Next rows are rebuilt whenever the queue changes, so this never changes on a live row.
    public bool IsQueued { get; init; }

    // Null until loaded.
    [ObservableProperty]
    public partial Bitmap? ArtBitmap { get; set; }

    internal bool IsArtRequested { get; set; }

    // Bumped when a container recycles the row, invalidating thumbnail callbacks for its earlier use.
    internal int ArtRequestVersion { get; set; }
}
