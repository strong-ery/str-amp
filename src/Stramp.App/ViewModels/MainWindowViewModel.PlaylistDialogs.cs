using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
using Stramp.Audio;
using Stramp.Core.Library;
using Stramp.Core.Lyrics;
using Stramp.Core.Models;
using Stramp.Core.Playback;
using Stramp.Core.Settings;
using Stramp.Integrations.Windows;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>
    /// Parses an .m3u/.m3u8 once, stores its song paths in settings, and adds it to the source menu.
    /// Returns null on success, or a short error message for the status line.
    /// </summary>
    public string? ImportPlaylist(string playlistFile)
    {
        var paths = PlaylistScanner.ParsePaths(playlistFile);
        if (paths.Count == 0)
            return "No local song paths found in that playlist.";

        var songs = PlaylistScanner.ResolveSongs(paths, _library);
        if (songs.Count == 0)
            return "None of the songs in that playlist could be found on disk.";

        var name = Path.GetFileNameWithoutExtension(playlistFile);
        if (string.IsNullOrWhiteSpace(name))
            name = "Imported Playlist";

        _settings.Playlists.RemoveAll(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _settings.Playlists.Add(new SavedPlaylist
        {
            Name = name,
            FilePath = playlistFile,
            SongPaths = paths,
        });
        SettingsService.Save(_settings);

        PopulateSources();

        var imported = LibrarySources.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (imported is not null)
            SelectSource(imported);

        StatusText = "";
        return null;
    }

    [RelayCommand]
    public void OpenAddToPlaylistForCurrentSong()
    {
        if (_queue.Current is { } song)
            OpenAddToPlaylist(song);
    }

    [RelayCommand]
    public void OpenAddToPlaylistForSong(SongRow? row)
    {
        if (row is not null)
            OpenAddToPlaylist(row.Song);
    }

    public void OpenAddToPlaylist(Song? song)
    {
        song ??= _queue.Current;
        if (song is null)
            return;

        _targetSongForPlaylist = song;
        AddToPlaylistSongTitle = song.Title;
        AddToPlaylistSongArtist = song.Artist;
        AddToPlaylistSongArt = null;

        _artProvider.GetArtAsync(song.Path, bitmap =>
        {
            if (_targetSongForPlaylist?.Path == song.Path)
                AddToPlaylistSongArt = bitmap;
        });

        RefreshPlaylistPickerItems();

        IsCreatingNewPlaylistInDialog = false;
        NewPlaylistNameInDialog = "";
        NewPlaylistErrorMessage = null;
        IsAddToPlaylistOpen = true;
    }

    private void RefreshPlaylistPickerItems()
    {
        PlaylistPickerItems.Clear();
        var playlists = GetAllPlaylists();
        foreach (var p in playlists)
        {
            var alreadyIn = _targetSongForPlaylist is not null &&
                p.Songs.Any(s => string.Equals(s.Path, _targetSongForPlaylist.Path, StringComparison.OrdinalIgnoreCase));
            PlaylistPickerItems.Add(new PlaylistPickerItem(p.Name, p.Songs.Count, alreadyIn, p.FilePath));
        }
        HasNoPlaylistsInPicker = PlaylistPickerItems.Count == 0;
    }

    [RelayCommand]
    public void CloseAddToPlaylist() => IsAddToPlaylistOpen = false;

    [RelayCommand]
    public void AddSongToExistingPlaylist(PlaylistPickerItem? item)
    {
        if (_targetSongForPlaylist is null || item is null)
            return;

        if (item.IsAlreadyAdded)
            return;

        AddSongToPlaylistCore(item.Name, _targetSongForPlaylist, item.FilePath);
        item.IsAlreadyAdded = true;
        item.IsJustAdded = true;
        item.SongCount++;
    }

    [RelayCommand]
    public void StartCreateNewPlaylistInDialog()
    {
        IsCreatingNewPlaylistInDialog = true;
        NewPlaylistNameInDialog = "";
        NewPlaylistErrorMessage = null;
    }

    [RelayCommand]
    public void CancelCreateNewPlaylistInDialog()
    {
        IsCreatingNewPlaylistInDialog = false;
        NewPlaylistNameInDialog = "";
        NewPlaylistErrorMessage = null;
    }

    [RelayCommand]
    public void ConfirmCreateNewPlaylistInDialog()
    {
        if (_targetSongForPlaylist is null)
            return;

        var name = NewPlaylistNameInDialog?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NewPlaylistErrorMessage = "Please enter a playlist name.";
            return;
        }

        if (GetAllPlaylists().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            NewPlaylistErrorMessage = "A playlist with this name already exists.";
            return;
        }

        var m3u8Path = DetermineM3u8Path(name);
        AddSongToPlaylistCore(name, _targetSongForPlaylist, m3u8Path);

        IsAddToPlaylistOpen = false;
    }

    [RelayCommand]
    public void OpenNewPlaylistDialog()
    {
        StandaloneNewPlaylistName = "";
        StandaloneNewPlaylistError = null;
        IsNewPlaylistDialogOpen = true;
    }

    [RelayCommand]
    public void CloseNewPlaylistDialog() => IsNewPlaylistDialogOpen = false;

    [RelayCommand]
    public void ConfirmStandaloneNewPlaylist()
    {
        var name = StandaloneNewPlaylistName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StandaloneNewPlaylistError = "Please enter a playlist name.";
            return;
        }

        if (GetAllPlaylists().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StandaloneNewPlaylistError = "A playlist with this name already exists.";
            return;
        }

        var m3u8Path = DetermineM3u8Path(name);
        if (!string.IsNullOrEmpty(m3u8Path))
        {
            try
            {
                PlaylistWriter.Write(m3u8Path, []);
            }
            catch
            {
                m3u8Path = null;
            }
        }

        _settings.Playlists.Add(new SavedPlaylist
        {
            Name = name,
            FilePath = m3u8Path,
            SongPaths = [],
        });
        SettingsService.Save(_settings);
        PopulateSources();

        IsNewPlaylistDialogOpen = false;
        StatusText = $"Created playlist '{name}'";

        var created = LibrarySources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        if (created is not null)
            SelectSource(created);
    }

}
