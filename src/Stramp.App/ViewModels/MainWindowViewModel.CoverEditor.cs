using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>Custom covers already read from disk, by playlist name, so rebuilding the source
    /// list doesn't reread them.</summary>
    private readonly Dictionary<string, Bitmap> _customCovers = new(StringComparer.OrdinalIgnoreCase);

    private string? _coverEditorPlaylist;

    [ObservableProperty]
    public partial bool IsCoverEditorOpen { get; set; }

    /// <summary>The picture being cropped. The crop control owns the position; this just hands it over.</summary>
    [ObservableProperty]
    public partial Bitmap? CoverEditorSource { get; set; }

    [ObservableProperty]
    public partial double CoverEditorZoom { get; set; } = 1;

    [ObservableProperty]
    public partial int CoverEditorRotation { get; set; }

    [ObservableProperty]
    public partial string CoverEditorPlaylistName { get; set; } = "";

    /// <summary>Opens the crop dialog on a picture the user picked for the browsed playlist.</summary>
    public void OpenCoverEditor(Bitmap picture)
    {
        if (CurrentSourceRow is not { IsPlaylist: true } playlist)
        {
            picture.Dispose();
            return;
        }

        _coverEditorPlaylist = playlist.Name;
        CoverEditorPlaylistName = playlist.Name;
        ReplaceCoverEditorSource(picture);
        IsCoverEditorOpen = true;
    }

    /// <summary>Swaps the picture while the dialog stays open ("Choose another…").</summary>
    public void ReplaceCoverEditorSource(Bitmap picture)
    {
        var previous = CoverEditorSource;
        CoverEditorZoom = 1;
        CoverEditorRotation = 0;
        CoverEditorSource = picture;
        previous?.Dispose();
    }

    [RelayCommand]
    private void CloseCoverEditor()
    {
        IsCoverEditorOpen = false;
        var previous = CoverEditorSource;
        CoverEditorSource = null;
        previous?.Dispose();
        _coverEditorPlaylist = null;
    }

    [RelayCommand]
    private void RotateCoverLeft() => CoverEditorRotation = (CoverEditorRotation + 270) % 360;

    [RelayCommand]
    private void RotateCoverRight() => CoverEditorRotation = (CoverEditorRotation + 90) % 360;

    /// <summary>Stores the cropped square as the playlist's cover and shows it right away.</summary>
    public void SaveCoverFromEditor(Bitmap cropped)
    {
        var name = _coverEditorPlaylist;
        if (name is null)
        {
            cropped.Dispose();
            return;
        }

        try
        {
            PlaylistCoverStore.Save(name, cropped);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save the cover: {ex.Message}";
            cropped.Dispose();
            return;
        }
        cropped.Dispose();

        var shown = PlaylistCoverStore.TryLoad(name);
        if (shown is null)
        {
            StatusText = "The cover was saved but couldn't be read back.";
            return;
        }

        _customCovers[name] = shown;
        if (FindSource(name) is { } row)
        {
            row.HasCustomCover = true;
            row.Cover = shown;
        }

        CloseCoverEditor();
        StatusText = $"Updated the cover for '{name}'";
    }

    /// <summary>Drops the browsed playlist's custom cover so it goes back to its automatic collage.</summary>
    [RelayCommand]
    private void RemoveCustomCover()
    {
        if (CurrentSourceRow is not { IsPlaylist: true } playlist)
            return;

        var name = playlist.Name;

        PlaylistCoverStore.Delete(name);
        _customCovers.Remove(name);
        if (FindSource(name) is { } row)
        {
            row.HasCustomCover = false;
            row.Cover = null;
            row.CoverRequested = false;
            LoadSourceCover(row);
        }

        StatusText = $"Removed the custom cover from '{name}'";
    }

    private LibrarySourceRow? FindSource(string name) => LibrarySources.FirstOrDefault(source =>
        string.Equals(source.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Shows a playlist's saved cover if it has one. True when it did (or will, once read).</summary>
    private bool TryApplyCustomCover(LibrarySourceRow source)
    {
        if (_customCovers.TryGetValue(source.Name, out var cached))
        {
            source.HasCustomCover = true;
            source.Cover = cached;
            return true;
        }

        if (!PlaylistCoverStore.Exists(source.Name))
            return false;

        _ = LoadCustomCoverAsync(source);
        return true;
    }

    private async Task LoadCustomCoverAsync(LibrarySourceRow source)
    {
        var name = source.Name;
        var loaded = await Task.Run(() => PlaylistCoverStore.TryLoad(name));
        if (loaded is null)
        {
            // Unreadable file: fall back to the collage rather than showing nothing.
            StartCollage(source);
            return;
        }

        _customCovers[name] = loaded;
        source.HasCustomCover = true;
        source.Cover = loaded;
    }
}
