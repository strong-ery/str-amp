using Stramp.Core.Library;
using Stramp.Core.Models;
using Stramp.Core.Playback;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    private bool TryRestorePlaybackState()
    {
        var state = _savedPlaybackState;
        _savedPlaybackState = null;
        if (state is null || !PlaybackStateMatchesLibrary(state))
            return false;

        var savedActiveSongs = _restoreActiveSongs
                               ?? PlaylistScanner.ResolveSongs(state.ActiveSongPaths, _libraryIndex);
        var preResolvedQueue = _restoreQueueSongs;
        _restoreActiveSongs = _restoreQueueSongs = null;
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
        // The header's row was resolved before the saved source was known, so load its cover now.
        LoadSourceCover(CurrentSourceRow);
        UpdateSortOptionsForCurrentSource(state.SortOption);
        UpdateDisplayedSongs();
        SnapshotPlaybackSource();
        SeedSortedPlayback();

        Shuffled = state.Shuffled;
        LoopMode = state.LoopMode;
        var restoredQueue = preResolvedQueue ?? PlaylistScanner.ResolveSongs(state.QueuePaths, _libraryIndex);
        if (restoredQueue.Count > 0)
        {
            _queue.Restore(restoredQueue, state.QueuePosition, state.CurrentSongPath,
                state.ShuffleSeed, state.QueuedPaths);
        }
        else
        {
            _queue.Build(SortedPlaybackSongs, Shuffled,
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

    /// <summary>Uses the order prepared off-thread as the playing source's sorted list, when it is
    /// what the sorted list would be anyway: All Songs, same sort, same songs.</summary>
    private void SeedSortedPlayback()
    {
        var prepared = _restoreSortedLibrary;
        _restoreSortedLibrary = null;
        if (prepared is not { } ready
            || !ReferenceEquals(_activeSongs, _library)
            || _playbackSort != ready.Sort
            || _playbackSongs.Count != ready.Sorted.Count)
            return;

        UseSortedPlayback(ready.Sorted, ready.Sort);
    }

    /// <summary>Adopts an already-sorted copy of the playing source, if it really is that order of it.</summary>
    private void UseSortedPlayback(List<Song> sorted, LibrarySortOption sort)
    {
        if (_playbackSort != sort || _playbackSongs.Count != sorted.Count)
            return;

        _sortedPlaybackCache = sorted;
        _sortedPlaybackFor = _playbackSongs;
        _sortedPlaybackCount = _playbackSongs.Count;
        _sortedPlaybackSort = sort;
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

    private readonly object _stateWriteGate = new();
    private SavedPlaybackState? _pendingState;
    private int _stateWriterRunning;

    /// <summary>Writes the snapshot on a background thread, newest wins: with a huge library the
    /// file is tens of MB and must not be serialized and written on the UI thread. Exit writes
    /// synchronously so the last state is on disk before the process ends.</summary>
    private void WritePlaybackState(SavedPlaybackState state, bool wait)
    {
        if (wait)
        {
            Interlocked.Exchange(ref _pendingState, null);
            lock (_stateWriteGate)
                _playbackStateStore.Save(state);
            return;
        }

        Volatile.Write(ref _pendingState, state);
        if (Interlocked.Exchange(ref _stateWriterRunning, 1) == 0)
            _ = Task.Run(DrainPlaybackStateWrites);
    }

    private void DrainPlaybackStateWrites()
    {
        do
        {
            while (Interlocked.Exchange(ref _pendingState, null) is { } next)
            {
                lock (_stateWriteGate)
                {
                    try
                    {
                        _playbackStateStore.Save(next);
                    }
                    catch
                    {
                        // State persistence should never interrupt playback (read-only drives, full disk, etc.).
                    }
                }
            }

            Volatile.Write(ref _stateWriterRunning, 0);
        } while (Volatile.Read(ref _pendingState) is not null
                 && Interlocked.Exchange(ref _stateWriterRunning, 1) == 0);
    }

    private void SavePlaybackState(bool force = false, bool wait = false)
    {
        if (!_playbackStateReady || _library.Count == 0 || _disposed)
            return;

        var now = DateTime.UtcNow;
        if (!force && now - _lastPlaybackStateSaveUtc < TimeSpan.FromSeconds(10))
            return;

        var currentPath = _queue.Current?.Path;
        var activeSourcePosition = currentPath is null
            ? 0
            : SortedPlaybackList.FindIndex(song => string.Equals(
                song.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        var state = new SavedPlaybackState
        {
            LibraryPaths = [.. _libraryPaths],
            LibraryPath = LibraryPath,
            // What is saved is the playing source: on relaunch the queue and its source match.
            SourceName = _playbackSourceName,
            IsBrowsingSources = IsBrowsingSources,
            SortOption = _playbackSort.ToString(),
            // The full library is derived by scanning. Playlist paths are retained so an imported
            // or deleted playlist can still be restored as the active source.
            ActiveSongPaths = string.Equals(
                _playbackSourceName, "All Songs", StringComparison.OrdinalIgnoreCase)
                ? []
                : _playbackSongs.Select(song => song.Path).ToList(),
            ActiveSourcePosition = Math.Max(activeSourcePosition, 0),
            QueuePaths = _queue.Songs.Select(song => song.Path).ToList(),
            QueuePosition = _queue.Position,
            QueuedPaths = _queue.QueuedPathsFromCurrent.ToList(),
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
            WritePlaybackState(state, wait);
            _lastPlaybackStateSaveUtc = now;
        }
        catch
        {
            // State persistence should never interrupt playback (read-only drives, full disk, etc.).
        }
    }

}
