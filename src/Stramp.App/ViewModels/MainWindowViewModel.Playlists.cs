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
    [RelayCommand]
    public void MoveSongInPlaylistUp(SongRow? row) => MoveSongInPlaylist(row, -1);

    [RelayCommand]
    public void MoveSongInPlaylistDown(SongRow? row) => MoveSongInPlaylist(row, 1);

    [RelayCommand]
    public void MoveSongInPlaylistToTop(SongRow? row) => MoveSongInPlaylist(row, -int.MaxValue);

    [RelayCommand]
    public void MoveSongInPlaylistToBottom(SongRow? row) => MoveSongInPlaylist(row, int.MaxValue);

    public void MoveSongInPlaylist(SongRow? row, int delta)
    {
        row ??= SelectedLibraryRow;
        if (row is null || !IsViewingPlaylist)
            return;

        var currentIndex = _activeSongs.FindIndex(s => string.Equals(s.Path, row.Song.Path, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
            return;

        int newIndex;
        if (delta == -int.MaxValue)
            newIndex = 0;
        else if (delta == int.MaxValue)
            newIndex = _activeSongs.Count - 1;
        else
            newIndex = Math.Clamp(currentIndex + delta, 0, _activeSongs.Count - 1);

        if (newIndex == currentIndex)
            return;

        var song = _activeSongs[currentIndex];
        _activeSongs.RemoveAt(currentIndex);
        _activeSongs.Insert(newIndex, song);

        // Ensure we are viewing in Playlist order so user immediately sees their reorder
        if (SelectedSortOption?.Option != LibrarySortOption.Playlist)
            SelectedSortOption = SortPlaylist;

        var playlistName = CurrentSourceName;
        var saved = _settings.Playlists.FirstOrDefault(p =>
            string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));

        if (saved is null)
        {
            var discovered = _discoveredPlaylists.FirstOrDefault(p =>
                string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));
            if (discovered is not null)
            {
                saved = new SavedPlaylist
                {
                    Name = playlistName,
                    FilePath = discovered.FilePath,
                    SongPaths = _activeSongs.Select(s => s.Path).ToList(),
                };
                _settings.Playlists.Add(saved);
            }
        }
        else
        {
            saved.SongPaths = _activeSongs.Select(s => s.Path).ToList();
        }

        if (saved is not null && !string.IsNullOrEmpty(saved.FilePath))
        {
            try
            {
                PlaylistWriter.Write(saved.FilePath, _activeSongs);
            }
            catch (Exception ex)
            {
                StatusText = $"Could not save playlist file: {ex.Message}";
            }
        }

        SettingsService.Save(_settings);

        if (!Shuffled)
        {
            _queue.RestoreOrderKeepingCurrent(SortedActiveSongs);
            RefreshQueueRows();
        }

        UpdateDisplayedSongs();
        SelectedLibraryRow = LibraryRows.FirstOrDefault(r => string.Equals(r.Song.Path, song.Path, StringComparison.OrdinalIgnoreCase));
        SavePlaybackState(force: true);
        StatusText = $"Moved '{song.Title}' to #{newIndex + 1}";
    }

    private Song? _targetSongForPlaylist;

    public List<Playlist> GetAllPlaylists()
    {
        var playlists = new List<Playlist>();
        var playlistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in PlaylistScanner.FromSaved(_settings.Playlists, _library)
                     .Concat(_discoveredPlaylists))
        {
            if (playlistNames.Add(playlist.Name))
                playlists.Add(playlist);
        }
        return playlists;
    }

    public string? ExportPlaylist(string playlistName, string targetFilePath)
    {
        var allPlaylists = GetAllPlaylists();
        var playlist = allPlaylists.FirstOrDefault(p => string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));
        if (playlist is null)
            return $"Playlist '{playlistName}' not found.";

        try
        {
            PlaylistWriter.Write(targetFilePath, playlist.Songs);
            return null;
        }
        catch (Exception ex)
        {
            return $"Export failed: {ex.Message}";
        }
    }

}
