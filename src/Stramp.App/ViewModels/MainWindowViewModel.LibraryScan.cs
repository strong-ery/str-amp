using Stramp.Core.Library;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    private async Task ReloadLibraryAsync(bool restorePlaybackState, bool preservePlayback)
    {
        _libraryScanCts?.Cancel();
        var scan = new CancellationTokenSource();
        _libraryScanCts = scan;
        IsLibraryScanning = true;
        CancelNormalizationWarmup();

        var sourcePaths = _libraryPaths.ToArray();

        // A big library takes a while to read: say how far along it is instead of sitting silent.
        var scanProgress = new Progress<LibraryScanProgress>(report =>
        {
            if (ReferenceEquals(_libraryScanCts, scan))
                StatusText = report.ReadingTags
                    ? $"Reading tags… {report.Read:N0} of {report.Found:N0} songs"
                    : $"Looking for songs… {report.Found:N0} found";
        });
        var effectivePlDir = EffectivePlaylistsDirectory;
        List<Song> scannedSongs;
        List<Playlist> scannedPlaylists;
        Dictionary<string, Song> scannedIndex;
        Dictionary<string, List<Song>> scannedFolderSongs;
        List<Song> scannedByArtist;
        try
        {
            (scannedSongs, scannedPlaylists, scannedIndex, scannedFolderSongs, scannedByArtist) = await Task.Run(() =>
            {
                var songs = LibraryScanner.Scan(sourcePaths, scan.Token,
                    _settings.CacheLibraryMetadata ? _libraryMetadataCache : null, scanProgress);
                // Everything that only needs the scanned songs is prepared here, off the UI thread.
                var index = PlaylistScanner.IndexByPath(songs);
                var folderSongs = sourcePaths.Length > 1
                    ? sourcePaths.Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(path => path, path => SongsUnder(songs, path), StringComparer.OrdinalIgnoreCase)
                    : [];
                var playlists = new List<Playlist>();
                var scannedDirPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in sourcePaths.Where(Directory.Exists))
                {
                    scan.Token.ThrowIfCancellationRequested();
                    var fullSource = Path.GetFullPath(path);
                    scannedDirPaths.Add(fullSource);
                    playlists.AddRange(PlaylistScanner.Scan(fullSource, index));
                }

                if (Directory.Exists(effectivePlDir))
                {
                    var fullPlDir = Path.GetFullPath(effectivePlDir);
                    if (!scannedDirPaths.Any(src => fullPlDir.Equals(src, StringComparison.OrdinalIgnoreCase) ||
                                                    fullPlDir.StartsWith(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        scan.Token.ThrowIfCancellationRequested();
                        playlists.AddRange(PlaylistScanner.Scan(fullPlDir, index));
                    }
                }

                // The default order, ready for the first queue build and playback-state save.
                var byArtist = SortSongs(songs, LibrarySortOption.Artist).ToList();
                return (songs, playlists, index, folderSongs, byArtist);
            }, scan.Token);
            scan.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            if (ReferenceEquals(_libraryScanCts, scan) && !_disposed)
                StatusText = "Couldn't update the library. One of its folders may be unavailable.";
            return;
        }
        finally
        {
            if (ReferenceEquals(_libraryScanCts, scan))
            {
                _libraryScanCts = null;
                IsLibraryScanning = false;
            }
            scan.Dispose();
        }

        if (_disposed)
            return;

        if (restorePlaybackState && _savedPlaybackStateTask is { } stateTask)
        {
            _savedPlaybackStateTask = null;
            _savedPlaybackState = await stateTask;

            // Matching 100K saved paths against the library is real work: do it here, off the UI thread.
            if (_savedPlaybackState is { } saved)
            {
                (_restoreQueueSongs, _restoreActiveSongs, _restoreSortedLibrary) = await Task.Run(() =>
                {
                    var queue = PlaylistScanner.ResolveSongs(saved.QueuePaths ?? [], scannedIndex);
                    var active = PlaylistScanner.ResolveSongs(saved.ActiveSongPaths ?? [], scannedIndex);

                    // The usual case: the playing source is All Songs. Sort it now, so the first
                    // playback-state save doesn't have to sort the whole library on the UI thread.
                    (List<Song>, LibrarySortOption)? sorted = null;
                    if (string.Equals(saved.SourceName, "All Songs", StringComparison.OrdinalIgnoreCase))
                    {
                        var sort = Enum.TryParse<LibrarySortOption>(saved.SortOption, out var parsed)
                            ? parsed : LibrarySortOption.Artist;
                        if (sort != LibrarySortOption.Playlist)
                            sorted = (sort == LibrarySortOption.Artist
                                ? scannedByArtist : SortSongs(scannedSongs, sort).ToList(), sort);
                    }

                    return (queue, active, sorted);
                });
            }
        }

        // Capture playback after the potentially long scan so advancing tracks while it runs is
        // never undone when the new library is installed.
        var previousQueue = preservePlayback ? _queue.Songs.ToList() : [];
        var previousCurrent = preservePlayback ? _queue.Current : null;
        var previousQueuePosition = _queue.Position;

        _library = scannedSongs;
        _libraryIndex = scannedIndex;
        _folderSongs = scannedFolderSongs;
        _discoveredPlaylists = scannedPlaylists;
        EnsurePlaylistsOnDisk();
        PopulateSources();

        if (_library.Count == 0)
        {
            _activeSongs = [];
            _playbackSongs = [];
            LibraryRows.ReplaceAll([]);
            _queue.Restore([], 0, 0);
            RefreshQueueRows();
            _playbackStateReady = false;
            StatusText = _libraryPaths.Count == 0
                ? "Add a folder to start building your library."
                : "No supported audio files found in your library folders.";
            return;
        }

        StatusText = "";
        if (restorePlaybackState && TryRestorePlaybackState())
        {
            StartNormalizationWarmup();
            return;
        }

        var selectedSource = LibrarySources.FirstOrDefault(source => string.Equals(
            source.Name, CurrentSourceName, StringComparison.OrdinalIgnoreCase));
        _activeSongs = selectedSource is not null ? [.. selectedSource.Songs] : _library;
        if (selectedSource is null)
        {
            CurrentSourceName = "All Songs";
        }
        UpdateSortOptionsForCurrentSource();
        UpdateDisplayedSongs();

        // The playing source is re-found by its own name: it may differ from the browsed one.
        var playbackSource = LibrarySources.FirstOrDefault(source => string.Equals(
            source.Name, _playbackSourceName, StringComparison.OrdinalIgnoreCase));
        if (playbackSource is null)
        {
            _playbackSourceName = "All Songs";
            _playbackSort = SelectedSortOption?.Option ?? LibrarySortOption.Artist;
        }
        _playbackSongs = playbackSource is not null ? [.. playbackSource.Songs] : [.. _library];
        if (playbackSource is null or { Kind: LibrarySourceKind.AllSongs })
            UseSortedPlayback(scannedByArtist, LibrarySortOption.Artist);

        if (preservePlayback && previousQueue.Count > 0)
        {
            var restoredQueue = ResolveSongs(previousQueue.Select(song => song.Path), _libraryIndex);
            var seen = restoredQueue.Select(song => song.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            restoredQueue.AddRange(SortedPlaybackSongs.Where(song => seen.Add(song.Path)));

            if (previousCurrent is not null && !seen.Contains(previousCurrent.Path))
                restoredQueue.Insert(Math.Clamp(previousQueuePosition, 0, restoredQueue.Count), previousCurrent);

            var currentIndex = previousCurrent is null ? -1 : restoredQueue.FindIndex(song =>
                string.Equals(song.Path, previousCurrent.Path, StringComparison.OrdinalIgnoreCase));
            _queue.Restore(restoredQueue,
                currentIndex >= 0 ? currentIndex : previousQueuePosition, _queue.ShuffleSeed);
            RefreshQueueRows();
            _playbackStateReady = true;
            SavePlaybackState(force: true);
        }
        else
        {
            IsBrowsingSources = true;
            _queue.Build(SortedPlaybackSongs, Shuffled);
            _playbackStateReady = true;
            LoadCurrent(autoPlay: false);
        }
        StartNormalizationWarmup();
    }

    private void PopulateSources()
    {
        LibrarySources.Clear();
        LibrarySources.Add(new LibrarySourceRow("All Songs", _library, LibrarySourceKind.AllSongs));

        var playlists = new List<Playlist>();
        var playlistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Prefer the first/imported copy when names collide across sources.
        foreach (var playlist in PlaylistScanner.FromSaved(_settings.Playlists, _libraryIndex)
                     .Concat(_discoveredPlaylists))
        {
            if (playlistNames.Add(playlist.Name))
                playlists.Add(playlist);
        }

        // One row per configured folder, so a library built from several folders can still be
        // browsed a folder at a time. With a single folder the row would just repeat All Songs.
        if (_libraryPaths.Count > 1)
        {
            var takenNames = new HashSet<string>(playlistNames, StringComparer.OrdinalIgnoreCase)
            {
                "All Songs",
            };
            foreach (var path in _libraryPaths)
            {
                var name = UniqueFolderSourceName(path, takenNames);
                takenNames.Add(name);
                LibrarySources.Add(new LibrarySourceRow(name,
                    _folderSongs.TryGetValue(path, out var under) ? under : SongsUnder(_library, path),
                    LibrarySourceKind.Folder));
            }
        }

        foreach (var playlist in playlists)
            LibrarySources.Add(new LibrarySourceRow(playlist.Name, playlist.Songs, LibrarySourceKind.Playlist, playlist.FilePath));

        // The rows were rebuilt, so the header's row is a new object.
        UpdateSourcePlayingState();
        OnPropertyChanged(nameof(CurrentSourceRow));
        LoadSourceCover(CurrentSourceRow);
    }

    /// <summary>The scanned songs that live inside one configured library folder. Songs under a
    /// folder nested in another configured folder show up under both, which is what the paths say.</summary>
    private static List<Song> SongsUnder(IReadOnlyList<Song> library, string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return library
            .Where(song => NormalizeSeparators(song.Path)
                .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string NormalizeSeparators(string path) =>
        Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? path
            : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    /// <summary>Folder rows are labelled by folder name, which two library folders can share
    /// ("D:\Music" and "E:\Music"). Source names key selection and saved state, so collisions
    /// fall back to the full path and then to a numeric suffix.</summary>
    private static string UniqueFolderSourceName(string path, HashSet<string> takenNames)
    {
        var leaf = new LibraryLocationRow(path).Name;
        if (!takenNames.Contains(leaf))
            return leaf;
        if (!takenNames.Contains(path))
            return path;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{leaf} ({suffix})";
            if (!takenNames.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>What the selected source is, looked up by name so it survives a rescan.</summary>
    public LibrarySourceKind CurrentSourceKind =>
        LibrarySources.FirstOrDefault(source => string.Equals(
                source.Name, CurrentSourceName, StringComparison.OrdinalIgnoreCase))?.Kind
        ?? (string.Equals(CurrentSourceName, "All Songs", StringComparison.OrdinalIgnoreCase)
            ? LibrarySourceKind.AllSongs
            : LibrarySourceKind.Playlist);

}
