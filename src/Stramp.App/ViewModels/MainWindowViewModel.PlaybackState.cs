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
    private bool TryRestorePlaybackState()
    {
        var state = _savedPlaybackState;
        _savedPlaybackState = null;
        if (state is null || !PlaybackStateMatchesLibrary(state))
            return false;

        var savedActiveSongs = PlaylistScanner.ResolveSongs(state.ActiveSongPaths, _library);
        var namedSource = LibrarySources.FirstOrDefault(source =>
            string.Equals(source.Name, state.SourceName, StringComparison.OrdinalIgnoreCase));

        _activeSongs = string.Equals(state.SourceName, "All Songs", StringComparison.OrdinalIgnoreCase)
            ? _library
            : namedSource is not null
                ? [.. namedSource.Songs]
                : savedActiveSongs.Count > 0
                    ? savedActiveSongs
                    : _library;
        CurrentSourceName = namedSource?.Name ??
            (string.IsNullOrWhiteSpace(state.SourceName) ? "All Songs" : state.SourceName);
        IsBrowsingSources = state.IsBrowsingSources;
        UpdateSortOptionsForCurrentSource(state.SortOption);
        UpdateDisplayedSongs();

        Shuffled = state.Shuffled;
        LoopMode = state.LoopMode;
        var restoredQueue = PlaylistScanner.ResolveSongs(state.QueuePaths, _library);
        if (restoredQueue.Count > 0)
        {
            var position = state.QueuePosition;
            if (!string.IsNullOrWhiteSpace(state.CurrentSongPath))
            {
                var currentIndex = restoredQueue.FindIndex(song => string.Equals(
                    song.Path, state.CurrentSongPath, StringComparison.OrdinalIgnoreCase));
                if (currentIndex >= 0)
                    position = currentIndex;
            }
            _queue.Restore(restoredQueue, position, state.ShuffleSeed);
        }
        else
        {
            _queue.Build(SortedActiveSongs, Shuffled,
                Shuffled && state.ShuffleSeed != 0 ? state.ShuffleSeed : null);
            var restoredPosition = !string.IsNullOrWhiteSpace(state.CurrentSongPath)
                ? _queue.Songs.ToList().FindIndex(song => string.Equals(
                    song.Path, state.CurrentSongPath, StringComparison.OrdinalIgnoreCase))
                : state.ActiveSourcePosition;
            _queue.JumpTo(restoredPosition);
        }

        _playbackStateReady = true;
        LoadCurrent(autoPlay: false, resumePositionSeconds: state.PositionSeconds);
        return true;
    }

    private static List<Song> ResolveSongs(
        IEnumerable<string>? paths, IReadOnlyDictionary<string, Song> songsByPath)
    {
        var songs = new List<Song>();
        if (paths is null)
            return songs;

        foreach (var path in paths)
        {
            if (songsByPath.TryGetValue(path, out var song))
                songs.Add(song);
        }
        return songs;
    }

    private bool PlaybackStateMatchesLibrary(SavedPlaybackState state)
    {
        var savedPaths = state.LibraryPaths ?? [];
        if (savedPaths.Count == 0 && !string.IsNullOrWhiteSpace(state.LibraryPath))
            savedPaths = [state.LibraryPath];

        var normalizedSaved = savedPaths
            .Select(NormalizeDirectory)
            .Where(path => path is not null)
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalizedSaved.SetEquals(_libraryPaths);
    }

    private void SavePlaybackState(bool force = false)
    {
        if (!_playbackStateReady || _library.Count == 0 || _disposed)
            return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastPlaybackStateSaveUtc < TimeSpan.FromSeconds(10))
            return;

        var currentPath = _queue.Current?.Path;
        var activeSourcePosition = currentPath is null
            ? 0
            : SortedActiveSongs.ToList().FindIndex(song => string.Equals(
                song.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        var state = new SavedPlaybackState
        {
            LibraryPaths = [.. _libraryPaths],
            LibraryPath = LibraryPath,
            SourceName = CurrentSourceName,
            IsBrowsingSources = IsBrowsingSources,
            SortOption = SelectedSortOption?.Option.ToString(),
            // The full library is derived by scanning. Playlist paths are retained so an imported
            // or deleted playlist can still be restored as the active source.
            ActiveSongPaths = string.Equals(
                CurrentSourceName, "All Songs", StringComparison.OrdinalIgnoreCase)
                ? []
                : _activeSongs.Select(song => song.Path).ToList(),
            ActiveSourcePosition = Math.Max(activeSourcePosition, 0),
            QueuePaths = _queue.Songs.Select(song => song.Path).ToList(),
            QueuePosition = _queue.Position,
            CurrentSongPath = currentPath,
            ShuffleSeed = _queue.ShuffleSeed,
            Shuffled = Shuffled,
            LoopMode = LoopMode,
            PositionSeconds = Math.Clamp(ProgressSeconds, 0, DurationSeconds),
            WasPlaying = IsPlaying,
            SavedAtUtc = now,
        };

        try
        {
            _playbackStateStore.Save(state);
            _lastPlaybackStateSaveUtc = now;
        }
        catch
        {
            // State persistence should never interrupt playback (read-only drives, full disk, etc.).
        }
    }

}
