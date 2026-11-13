using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Models;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial bool IsConfirmDialogOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmDialogTitle { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmDialogMessage { get; set; } = "";

    [ObservableProperty]
    public partial string ConfirmDialogConfirmText { get; set; } = "Confirm";

    private Action? _pendingConfirmAction;

    /// <summary>A generic "are you sure" overlay: callers supply the wording and what happens on
    /// confirm, so one dialog serves any destructive action rather than each needing its own.</summary>
    private void OpenConfirmDialog(string title, string message, string confirmText, Action onConfirm)
    {
        ConfirmDialogTitle = title;
        ConfirmDialogMessage = message;
        ConfirmDialogConfirmText = confirmText;
        _pendingConfirmAction = onConfirm;
        IsConfirmDialogOpen = true;
    }

    [RelayCommand]
    private void AcceptConfirmDialog()
    {
        IsConfirmDialogOpen = false;
        var action = _pendingConfirmAction;
        _pendingConfirmAction = null;
        action?.Invoke();
    }

    [RelayCommand]
    private void CancelConfirmDialog()
    {
        IsConfirmDialogOpen = false;
        _pendingConfirmAction = null;
    }

    [RelayCommand]
    private void RequestDeleteSongPermanently(SongRow? row)
    {
        if (row is null)
            return;

        OpenConfirmDialog(
            "Delete Song Permanently?",
            $"This will permanently DELETE \"{row.Title}\" by {row.Artist} from your drive. " +
            "This cannot be undone.",
            "Delete Permanently",
            () => DeleteSongPermanently(row.Song));
    }

    private void DeleteSongPermanently(Song song)
    {
        var wasCurrent = string.Equals(_queue.Current?.Path, song.Path, StringComparison.OrdinalIgnoreCase);
        var wasPlaying = IsPlaying;

        if (wasCurrent)
        {
            _player.Pause();
            IsPlaying = false;
        }

        try
        {
            if (File.Exists(song.Path))
                File.Delete(song.Path);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't delete '{song.Title}': {ex.Message}";
            return;
        }

        _library.RemoveAll(s => string.Equals(s.Path, song.Path, StringComparison.OrdinalIgnoreCase));
        _activeSongs.RemoveAll(s => string.Equals(s.Path, song.Path, StringComparison.OrdinalIgnoreCase));
        _queue.RemoveAllMatching(song.Path);
        foreach (var playlist in _settings.Playlists)
            playlist.SongPaths.RemoveAll(p => string.Equals(p, song.Path, StringComparison.OrdinalIgnoreCase));
        SettingsService.Save(_settings);

        UpdateDisplayedSongs();
        RefreshQueueRows();
        PopulateSources();

        if (wasCurrent && _queue.Current is not null)
            LoadCurrent(autoPlay: wasPlaying);

        StatusText = $"Deleted '{song.Title}' from your drive.";
    }
}
