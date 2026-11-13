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
    public void AddSongToPlaylistCore(string playlistName, Song song, string? filePath = null)
    {
        var saved = _settings.Playlists.FirstOrDefault(p =>
            string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));

        if (saved is null)
        {
            var discovered = _discoveredPlaylists.FirstOrDefault(p =>
                string.Equals(p.Name, playlistName, StringComparison.OrdinalIgnoreCase));

            saved = new SavedPlaylist
            {
                Name = playlistName,
                FilePath = filePath ?? discovered?.FilePath,
                SongPaths = discovered?.Songs.Select(s => s.Path).ToList() ?? [],
            };
            _settings.Playlists.Add(saved);
        }

        if (!saved.SongPaths.Any(p => string.Equals(p, song.Path, StringComparison.OrdinalIgnoreCase)))
            saved.SongPaths.Add(song.Path);

        if (string.IsNullOrEmpty(saved.FilePath) && !string.IsNullOrEmpty(filePath))
            saved.FilePath = filePath;

        if (!string.IsNullOrEmpty(saved.FilePath))
        {
            try
            {
                var resolvedSongs = PlaylistScanner.ResolveSongs(saved.SongPaths, _library);
                PlaylistWriter.Write(saved.FilePath, resolvedSongs);
            }
            catch (Exception ex)
            {
                StatusText = $"Could not save playlist file: {ex.Message}";
            }
        }

        SettingsService.Save(_settings);

        if (string.Equals(CurrentSourceName, playlistName, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedSongs = PlaylistScanner.ResolveSongs(saved.SongPaths, _library);
            _activeSongs = [.. resolvedSongs];
            UpdateDisplayedSongs();
        }

        PopulateSources();
    }

    [RelayCommand]
    public void RemoveSongFromCurrentPlaylist(SongRow? row)
    {
        if (row is null || CurrentSourceKind != LibrarySourceKind.Playlist)
            return;

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
                    SongPaths = discovered.Songs.Select(s => s.Path).ToList(),
                };
                _settings.Playlists.Add(saved);
            }
        }

        if (saved is not null)
        {
            saved.SongPaths.RemoveAll(p => string.Equals(p, row.Song.Path, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(saved.FilePath))
            {
                try
                {
                    var resolvedSongs = PlaylistScanner.ResolveSongs(saved.SongPaths, _library);
                    PlaylistWriter.Write(saved.FilePath, resolvedSongs);
                }
                catch (Exception ex)
                {
                    StatusText = $"Could not save playlist file: {ex.Message}";
                }
            }
            SettingsService.Save(_settings);
        }

        _activeSongs.RemoveAll(s => string.Equals(s.Path, row.Song.Path, StringComparison.OrdinalIgnoreCase));
        UpdateDisplayedSongs();
        PopulateSources();
        StatusText = $"Removed '{row.Title}' from {playlistName}";
    }

    [RelayCommand]
    public void DeletePlaylist(LibrarySourceRow? row)
    {
        if (row is null || !row.IsPlaylist)
            return;

        var name = row.Name;
        _settings.Playlists.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _discoveredPlaylists.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(row.FilePath) && File.Exists(row.FilePath))
        {
            try
            {
                File.Delete(row.FilePath);
            }
            catch (Exception ex)
            {
                StatusText = $"Could not delete playlist file: {ex.Message}";
            }
        }

        SettingsService.Save(_settings);

        if (string.Equals(CurrentSourceName, name, StringComparison.OrdinalIgnoreCase))
        {
            CurrentSourceName = "All Songs";
            _activeSongs = [.. _library];
            IsBrowsingSources = true;
            SearchText = "";
            UpdateSortOptionsForCurrentSource();
            UpdateDisplayedSongs();
        }

        PopulateSources();
        StatusText = $"Deleted playlist '{name}'";
    }

}
