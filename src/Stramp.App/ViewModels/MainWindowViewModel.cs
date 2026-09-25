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

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int QueueShowCount = 40;

    /// <summary>The "no pinned endpoint" entry: playback follows whatever Windows is using.</summary>
    private static readonly OutputDeviceRow SystemDefaultOutputDevice = new(null, "System default");

    private static readonly SortOptionRow SortPlaylist = new(LibrarySortOption.Playlist, "Playlist");
    private static readonly SortOptionRow SortArtist = new(LibrarySortOption.Artist, "Artist Name");
    private static readonly SortOptionRow SortAlbum = new(LibrarySortOption.Album, "Album Name");
    private static readonly SortOptionRow SortSong = new(LibrarySortOption.Song, "Song Name");

    private readonly IMediaPlayer _player;
    private readonly PlaybackQueue _queue = new();
    private readonly AppSettings _settings;
    private readonly PlaybackStateStore _playbackStateStore;
    private readonly AlbumArtProvider _artProvider = new();
    private readonly LibraryMetadataCache _libraryMetadataCache = new();
    private readonly LoudnessNormalizationService _loudnessNormalizer = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private readonly LyricsProvider _lyricsProvider = new();
    private CancellationTokenSource? _normalizationCts;
    private CancellationTokenSource? _normalizationWarmupCts;
    private CancellationTokenSource? _lyricsCts;
    private CancellationTokenSource? _libraryScanCts;
    private string? _pendingPlaybackPath;
    private List<Song> _library = [];
    private List<Playlist> _discoveredPlaylists = [];
    private readonly List<string> _libraryPaths = [];
    private SavedPlaybackState? _savedPlaybackState;
    private double _resumePositionSeconds;
    private DateTime _lastPlaybackStateSaveUtc = DateTime.MinValue;
    private bool _playbackStateReady;
    private bool _disposed;

    /// <summary>Songs of the selected source (All Songs or a playlist) — what the queue is built from.</summary>
    private List<Song> _activeSongs = [];
    private bool _isSeeking;
    private bool _isAdvancing;

    // The player only reports its position a few times a second, which makes the progress bar
    // step visibly. We interpolate between reports with a stopwatch so the bar moves smoothly.
    private readonly Stopwatch _sincePositionReport = new();
    private double _reportedPosition;

    /// <summary>False until the current queue entry has actually been handed to the player.</summary>
    private bool _playerHasCurrentTrack;

    /// <summary>Index into LyricLines of the highlighted line, or -1 before the first one.</summary>
    private int _activeLyricIndex = -1;

    /// <summary>Both current-cover modes are cached so switching the dropdown is instant.</summary>
    private ArtPalette? _inferredArtPalette;
    private ArtPalette? _directArtPalette;

    public BulkObservableCollection<SongRow> LibraryRows { get; } = [];
    public ObservableCollection<SongRow> QueueRows { get; } = [];
    public ObservableCollection<LibrarySourceRow> LibrarySources { get; } = [];
    public ObservableCollection<LibraryLocationRow> LibraryLocations { get; } = [];

    public ObservableCollection<SortOptionRow> AvailableSortOptions { get; } = [];

    [ObservableProperty]
    private SortOptionRow? _selectedSortOption;

    partial void OnSelectedSortOptionChanged(SortOptionRow? value)
    {
        UpdateDisplayedSongs();
        SavePlaybackState();
    }

    /// <summary>True while the library panel shows the source menu rather than a song list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryHeaderText))]
    public partial bool IsBrowsingSources { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryHeaderText))]
    public partial string CurrentSourceName { get; set; } = "All Songs";

    public string LibraryHeaderText =>
        IsBrowsingSources ? "LIBRARY" : CurrentSourceName.ToUpperInvariant();

    [ObservableProperty]
    public partial string LibraryPath { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = "Nothing playing";

    [ObservableProperty]
    public partial string CurrentArtist { get; set; } = "";

    public Song? CurrentSong => _queue.Current;
    public bool HasCurrentSong => _queue.Current is not null;

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    public partial double ProgressSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressFraction))]
    public partial double DurationSeconds { get; set; } = 1;

    /// <summary>Playback position as 0-1, for the waveform seek bar.</summary>
    public double ProgressFraction => DurationSeconds > 0
        ? Math.Clamp(ProgressSeconds / DurationSeconds, 0, 1)
        : 0;

    [ObservableProperty]
    public partial string ElapsedText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string TotalText { get; set; } = "0:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIcon))]
    public partial double Volume { get; set; }

    /// <summary>Silences playback without moving the volume slider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeIcon))]
    [NotifyPropertyChangedFor(nameof(VolumeToolTip))]
    public partial bool IsMuted { get; set; }

    public Geometry VolumeIcon => IsMuted ? Icons.VolumeMuted
        : Volume <= 0 ? Icons.VolumeZero
        : Volume < 50 ? Icons.VolumeLow
        : Icons.VolumeHigh;

    public string VolumeToolTip => IsMuted ? "Unmute" : "Mute";

    /// <summary>Entries of the playback-device picker; the first one always follows the OS default.</summary>
    public ObservableCollection<OutputDeviceRow> OutputDevices { get; } = [];

    [ObservableProperty]
    public partial OutputDeviceRow? SelectedOutputDevice { get; set; }

    /// <summary>Suppresses device switching while the picker's list is being rebuilt.</summary>
    private bool _isRefreshingOutputDevices;

    [ObservableProperty]
    public partial bool Shuffled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoopActive))]
    [NotifyPropertyChangedFor(nameof(LoopIcon))]
    [NotifyPropertyChangedFor(nameof(LoopToolTip))]
    public partial LoopMode LoopMode { get; set; }

    public bool IsLoopActive => LoopMode != LoopMode.Off;
    public Geometry LoopIcon => LoopMode == LoopMode.Track ? Icons.RepeatOne : Icons.Repeat;
    public string LoopToolTip => LoopMode switch
    {
        LoopMode.Playlist => "Loop: Playlist",
        LoopMode.Track => "Loop: Track",
        _ => "Loop: Off"
    };

    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLibraryScanning { get; set; }

    [ObservableProperty]
    public partial Bitmap? CurrentArtBitmap { get; set; }

    [ObservableProperty]
    public partial bool IsLibraryOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsUpNextOpen { get; set; } = true;

    /// <summary>Splits the now-playing card: cover on the left half, the track's .lrc on the right.</summary>
    [ObservableProperty]
    public partial bool IsLyricsOpen { get; set; }

    /// <summary>Lines of the current track's .lrc, in display order. Empty when there is no file.</summary>
    public ObservableCollection<LyricLineRow> LyricLines { get; } = [];

    [ObservableProperty]
    public partial bool HasLyrics { get; set; }

    /// <summary>True when the .lrc carried timestamps, so lines can be highlighted and clicked to seek.</summary>
    [ObservableProperty]
    public partial bool IsLyricsSynced { get; set; }

    /// <summary>Shown in place of the lines when there are none.</summary>
    [ObservableProperty]
    public partial string LyricsStatusText { get; set; } = "Nothing playing";

    /// <summary>The line playback is currently on, or null when unsynced or before the first line.</summary>
    [ObservableProperty]
    public partial LyricLineRow? ActiveLyricLine { get; set; }

    /// <summary>Where the shown lyrics came from, for the caption under the panel header.</summary>
    [ObservableProperty]
    public partial string LyricsSourceText { get; set; } = "";

    /// <summary>Suppresses the refresh button while a lookup is already running.</summary>
    [ObservableProperty]
    public partial bool IsLyricsLoading { get; set; }

    [ObservableProperty]
    public partial double LeftPanelWidth { get; set; } = 280;

    [ObservableProperty]
    public partial double RightPanelWidth { get; set; } = 280;

    [ObservableProperty]
    public partial bool IsCompactControlBar { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAddToPlaylistOpen { get; set; }

    [ObservableProperty]
    public partial string AddToPlaylistSongTitle { get; set; } = "";

    [ObservableProperty]
    public partial string AddToPlaylistSongArtist { get; set; } = "";

    [ObservableProperty]
    public partial Bitmap? AddToPlaylistSongArt { get; set; }

    [ObservableProperty]
    public partial bool IsCreatingNewPlaylistInDialog { get; set; }

    [ObservableProperty]
    public partial string NewPlaylistNameInDialog { get; set; } = "";

    [ObservableProperty]
    public partial string? NewPlaylistErrorMessage { get; set; }

    public ObservableCollection<PlaylistPickerItem> PlaylistPickerItems { get; } = [];

    [ObservableProperty]
    public partial bool HasNoPlaylistsInPicker { get; set; }

    [ObservableProperty]
    public partial SongRow? SelectedLibraryRow { get; set; }

    [ObservableProperty]
    public partial bool IsNewPlaylistDialogOpen { get; set; }

    [ObservableProperty]
    public partial string StandaloneNewPlaylistName { get; set; } = "";

    [ObservableProperty]
    public partial string? StandaloneNewPlaylistError { get; set; }

    public AudioVisualizerFeed VisualizerFeed { get; } = new();

    public AppSettings Settings => _settings;

    public ThemeSettingsViewModel Theme { get; }

    public MainWindowViewModel() : this(new WasapiMediaPlayer(), SettingsService.Load())
    {
    }

    public MainWindowViewModel(
        IMediaPlayer player, AppSettings settings, PlaybackStateStore? playbackStateStore = null)
    {
        _player = player;
        _settings = settings;
        _playbackStateStore = playbackStateStore ?? new PlaybackStateStore();
        _savedPlaybackState = _playbackStateStore.Load();

        Shuffled = settings.Shuffle;
        LoopMode = settings.LoopMode;
        Volume = settings.Volume;
        _player.Volume = settings.Volume;
        _player.OutputDeviceId = settings.OutputDeviceId;
        IsLibraryOpen = settings.LibraryPanelOpen;
        IsUpNextOpen = settings.UpNextPanelOpen;
        IsLyricsOpen = settings.LyricsPanelOpen;
        LeftPanelWidth = settings.LeftPanelWidth > 0 ? settings.LeftPanelWidth : 280;
        RightPanelWidth = settings.RightPanelWidth > 0 ? settings.RightPanelWidth : 280;
        Theme = new ThemeSettingsViewModel(
            settings, ApplyTheme, ApplyNormalizationSetting, ApplyDiscordPresenceSetting,
            ApplyMonoOutputSetting, ApplySurroundSoundSetting, ApplyLrcLibSetting, ApplyLibraryMetadataCacheSetting,
            ApplyDesktopShortcutSetting, ApplyStartMenuShortcutSetting);
        Theme.InitializeEqualizer(player.DefaultEqualizerBands, ApplyEqualizer, ApplyEffects);
        ApplyEqualizer();
        ApplyEffects();
        ApplyMonoOutputSetting();
        ApplySurroundSoundSetting();
        ApplyDiscordPresenceSetting();
        RefreshOutputDevices();

        var isLegacyLibrarySetting = settings.LibraryPaths is null;
        var configuredPaths = settings.LibraryPaths ?? [];
        if (isLegacyLibrarySetting && !string.IsNullOrWhiteSpace(settings.LibraryPath))
            configuredPaths = [settings.LibraryPath];
        if (isLegacyLibrarySetting && configuredPaths.Count == 0)
            configuredPaths =
                [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music")];

        foreach (var path in configuredPaths)
        {
            var normalized = NormalizeDirectory(path);
            if (normalized is not null && !_libraryPaths.Contains(
                    normalized, StringComparer.OrdinalIgnoreCase))
                _libraryPaths.Add(normalized);
        }

        LibraryPath = _libraryPaths.FirstOrDefault() ?? "";
        _settings.LibraryPaths = [.. _libraryPaths];
        _settings.LibraryPath = _libraryPaths.FirstOrDefault();
        RefreshLibraryLocations();

        _player.TimePositionChanged += OnTimePositionChanged;
        _player.PlaybackEnded += OnPlaybackEnded;

        EnsurePlaylistsOnDisk();

        if (_libraryPaths.Any(Directory.Exists))
            _ = ReloadLibraryAsync(restorePlaybackState: true, preservePlayback: false);
        else if (_libraryPaths.Count == 0)
            StatusText = "Add a folder to start building your library.";
    }

    /// <summary>Legacy single-folder entry point. Adds the folder to the combined library.</summary>
    public void LoadLibrary(string directory)
    {
        AddLibraryLocation(directory);
    }

    public void AddLibraryLocation(string directory)
    {
        var normalized = NormalizeDirectory(directory);
        if (normalized is null || !Directory.Exists(normalized))
        {
            StatusText = "That library folder is not available.";
            return;
        }

        if (_libraryPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            StatusText = "That folder is already in your library sources.";
            return;
        }

        _libraryPaths.Add(normalized);
        LibraryPath = _libraryPaths[0];
        PersistLibraryLocations();
        RefreshLibraryLocations();
        OnPropertyChanged(nameof(EffectivePlaylistsDirectory));
        _ = ReloadLibraryAsync(restorePlaybackState: false, preservePlayback: true);
    }

    [RelayCommand]
    private void RemoveLibraryLocation(LibraryLocationRow location)
    {
        var removed = _libraryPaths.RemoveAll(path => string.Equals(
            path, location.Path, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
            return;

        LibraryPath = _libraryPaths.FirstOrDefault() ?? "";
        PersistLibraryLocations();
        RefreshLibraryLocations();
        OnPropertyChanged(nameof(EffectivePlaylistsDirectory));
        _ = ReloadLibraryAsync(restorePlaybackState: false, preservePlayback: true);
    }

    private async Task ReloadLibraryAsync(bool restorePlaybackState, bool preservePlayback)
    {
        _libraryScanCts?.Cancel();
        var scan = new CancellationTokenSource();
        _libraryScanCts = scan;
        IsLibraryScanning = true;
        CancelNormalizationWarmup();

        var sourcePaths = _libraryPaths.ToArray();
        var effectivePlDir = EffectivePlaylistsDirectory;
        List<Song> scannedSongs;
        List<Playlist> scannedPlaylists;
        try
        {
            (scannedSongs, scannedPlaylists) = await Task.Run(() =>
            {
                var songs = LibraryScanner.Scan(sourcePaths, scan.Token,
                    _settings.CacheLibraryMetadata ? _libraryMetadataCache : null);
                var playlists = new List<Playlist>();
                var scannedDirPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in sourcePaths.Where(Directory.Exists))
                {
                    scan.Token.ThrowIfCancellationRequested();
                    var fullSource = Path.GetFullPath(path);
                    scannedDirPaths.Add(fullSource);
                    playlists.AddRange(PlaylistScanner.Scan(fullSource, songs));
                }

                if (Directory.Exists(effectivePlDir))
                {
                    var fullPlDir = Path.GetFullPath(effectivePlDir);
                    if (!scannedDirPaths.Any(src => fullPlDir.Equals(src, StringComparison.OrdinalIgnoreCase) ||
                                                    fullPlDir.StartsWith(src.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    {
                        scan.Token.ThrowIfCancellationRequested();
                        playlists.AddRange(PlaylistScanner.Scan(fullPlDir, songs));
                    }
                }

                return (songs, playlists);
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

        // Capture playback after the potentially long scan so advancing tracks while it runs is
        // never undone when the new library is installed.
        var previousQueue = preservePlayback ? _queue.Songs.ToList() : [];
        var previousCurrent = preservePlayback ? _queue.Current : null;
        var previousQueuePosition = _queue.Position;

        _library = scannedSongs;
        _discoveredPlaylists = scannedPlaylists;
        EnsurePlaylistsOnDisk();
        PopulateSources();

        if (_library.Count == 0)
        {
            _activeSongs = [];
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

        if (preservePlayback && previousQueue.Count > 0)
        {
            var songsByPath = _library.ToDictionary(song => song.Path, StringComparer.OrdinalIgnoreCase);
            var restoredQueue = ResolveSongs(previousQueue.Select(song => song.Path), songsByPath);
            var seen = restoredQueue.Select(song => song.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            restoredQueue.AddRange(SortedActiveSongs.Where(song => seen.Add(song.Path)));

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
            _queue.Build(SortedActiveSongs, Shuffled);
            _playbackStateReady = true;
            LoadCurrent(autoPlay: false);
        }
        StartNormalizationWarmup();
    }

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

    private void PersistLibraryLocations()
    {
        _settings.LibraryPaths = [.. _libraryPaths];
        _settings.LibraryPath = _libraryPaths.FirstOrDefault();
        SettingsService.Save(_settings);
    }

    private void RefreshLibraryLocations()
    {
        LibraryLocations.Clear();
        foreach (var path in _libraryPaths)
            LibraryLocations.Add(new LibraryLocationRow(path));
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            var root = Path.GetPathRoot(fullPath);
            return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                          or PathTooLongException)
        {
            return null;
        }
    }

    private void PopulateSources()
    {
        LibrarySources.Clear();
        LibrarySources.Add(new LibrarySourceRow("All Songs", _library, LibrarySourceKind.AllSongs));

        var playlists = new List<Playlist>();
        var playlistNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Prefer the first/imported copy when names collide across sources.
        foreach (var playlist in PlaylistScanner.FromSaved(_settings.Playlists, _library)
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
                LibrarySources.Add(new LibrarySourceRow(name, SongsUnder(path), LibrarySourceKind.Folder));
            }
        }

        foreach (var playlist in playlists)
            LibrarySources.Add(new LibrarySourceRow(playlist.Name, playlist.Songs, LibrarySourceKind.Playlist, playlist.FilePath));
    }

    /// <summary>The scanned songs that live inside one configured library folder. Songs under a
    /// folder nested in another configured folder show up under both, which is what the paths say.</summary>
    private List<Song> SongsUnder(string folder)
    {
        var prefix = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return _library
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
    private void SelectSource(LibrarySourceRow source)
    {
        _activeSongs = [.. source.Songs];
        CurrentSourceName = source.Name;
        IsBrowsingSources = false;
        SearchText = "";
        UpdateSortOptionsForCurrentSource();
        UpdateDisplayedSongs();
        OnPropertyChanged(nameof(IsViewingPlaylist));

        if (_activeSongs.Count > 0)
        {
            if (IsPlaying && _queue.Current is { } currentSong)
            {
                _queue.PlayFromLibrary(currentSong, SortedActiveSongs, Shuffled);
                RefreshQueueRows();
            }
            else
            {
                _queue.Build(SortedActiveSongs, Shuffled);
                LoadCurrent(autoPlay: false);
            }
            StartNormalizationWarmup();
        }

        SavePlaybackState(force: true);
    }

    public bool IsViewingPlaylist => !IsBrowsingSources && CurrentSourceKind == LibrarySourceKind.Playlist;

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
            catch
            {
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
        {
            StatusText = $"'{_targetSongForPlaylist.Title}' is already in {item.Name}";
            return;
        }

        AddSongToPlaylistCore(item.Name, _targetSongForPlaylist, item.FilePath);
        item.IsAlreadyAdded = true;
        item.IsJustAdded = true;
        item.SongCount++;
        StatusText = $"Added '{_targetSongForPlaylist.Title}' to {item.Name}";
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

        StatusText = $"Created playlist '{name}' with '{_targetSongForPlaylist.Title}'";
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

    public string EffectivePlaylistsDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.PlaylistsDirectory))
                return _settings.PlaylistsDirectory;

            var libraryRoot = _libraryPaths.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(libraryRoot))
                return Path.Combine(libraryRoot, "Playlists");

            return Path.Combine(SettingsService.ConfigDirectory, "playlists");
        }
    }

    public bool HasCustomPlaylistsDirectory => !string.IsNullOrWhiteSpace(_settings.PlaylistsDirectory);

    public void SetPlaylistsDirectory(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            _settings.PlaylistsDirectory = null;
        else
            _settings.PlaylistsDirectory = Path.GetFullPath(folderPath);

        SettingsService.Save(_settings);
        OnPropertyChanged(nameof(EffectivePlaylistsDirectory));
        OnPropertyChanged(nameof(HasCustomPlaylistsDirectory));

        EnsurePlaylistsOnDisk();
        PopulateSources();
    }

    [RelayCommand]
    public void ResetPlaylistsDirectory() => SetPlaylistsDirectory(null);

    public void EnsurePlaylistsOnDisk()
    {
        try
        {
            var dir = EffectivePlaylistsDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool changed = false;
            foreach (var saved in _settings.Playlists)
            {
                if (string.IsNullOrWhiteSpace(saved.FilePath) || !File.Exists(saved.FilePath))
                {
                    saved.FilePath = Path.Combine(dir, $"{SanitizeFileName(saved.Name)}.m3u8");
                    changed = true;
                }

                if (!File.Exists(saved.FilePath))
                {
                    var resolvedSongs = PlaylistScanner.ResolveSongs(saved.SongPaths, _library);
                    PlaylistWriter.Write(saved.FilePath, resolvedSongs);
                }
            }

            if (changed)
                SettingsService.Save(_settings);
        }
        catch
        {
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var clean = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Playlist" : clean;
    }

    private string? DetermineM3u8Path(string playlistName)
    {
        try
        {
            var dir = EffectivePlaylistsDirectory;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var safeName = SanitizeFileName(playlistName);
            return Path.Combine(dir, $"{safeName}.m3u8");
        }
        catch
        {
            return null;
        }
    }

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
            catch
            {
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
                catch
                {
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
            catch
            {
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

    private void UpdateSortOptionsForCurrentSource(string? savedSortOptionName = null)
    {
        // Only playlists carry a meaningful hand-made order; folder rows sort like the library.
        var isPlaylist = CurrentSourceKind == LibrarySourceKind.Playlist;
        var targetOptions = isPlaylist
            ? new[] { SortPlaylist, SortArtist, SortAlbum, SortSong }
            : new[] { SortArtist, SortAlbum, SortSong };

        AvailableSortOptions.Clear();
        foreach (var option in targetOptions)
            AvailableSortOptions.Add(option);

        if (!string.IsNullOrWhiteSpace(savedSortOptionName) &&
            Enum.TryParse<LibrarySortOption>(savedSortOptionName, out var parsedSortOption))
        {
            var matched = AvailableSortOptions.FirstOrDefault(o => o.Option == parsedSortOption);
            if (matched is not null)
            {
                SelectedSortOption = matched;
                return;
            }
        }

        SelectedSortOption = isPlaylist ? SortPlaylist : SortArtist;
    }

    /// <summary>The active source in the order the library list shows it. Queue rebuilds follow
    /// this so "next" matches the list the user is looking at, not raw source order.</summary>
    private IEnumerable<Song> SortedActiveSongs => GetSortedSongs(_activeSongs);

    private IEnumerable<Song> GetSortedSongs(IEnumerable<Song> songs)
    {
        return SelectedSortOption?.Option switch
        {
            LibrarySortOption.Album => songs
                .OrderBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase),

            LibrarySortOption.Song => songs
                .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Artist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album, StringComparer.OrdinalIgnoreCase),

            LibrarySortOption.Playlist => songs,

            LibrarySortOption.Artist or _ => songs
                .OrderBy(s => string.IsNullOrWhiteSpace(s.AlbumArtist) ? s.Artist : s.AlbumArtist, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Album, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void UpdateDisplayedSongs()
    {
        var sorted = SortedActiveSongs;
        var q = SearchText.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? sorted
            : LibrarySearch.Rank(sorted, q);
        PopulateLibraryRows(filtered);
    }

    [RelayCommand]
    private void BackToSources()
    {
        IsBrowsingSources = true;
        OnPropertyChanged(nameof(IsViewingPlaylist));
        SavePlaybackState(force: true);
    }

    private void PopulateLibraryRows(IEnumerable<Song> songs)
    {
        LibraryRows.ReplaceAll(songs.Select(song => new SongRow(song)));
        RefreshCurrentHighlight();
    }

    /// <summary>Called when Avalonia materializes a visible virtualized song row.</summary>
    public void LoadRowArt(SongRow row)
    {
        if (row.IsArtRequested)
            return;

        row.IsArtRequested = true;
        var requestVersion = ++row.ArtRequestVersion;
        _artProvider.GetThumbnailAsync(row.Song.Path, bitmap =>
        {
            if (row.IsArtRequested && row.ArtRequestVersion == requestVersion)
                row.ArtBitmap = bitmap;
        });
    }

    /// <summary>Releases an off-screen row while retaining a small shared thumbnail cache.</summary>
    public void ReleaseRowArt(SongRow row)
    {
        if (!row.IsArtRequested)
            return;
        row.IsArtRequested = false;
        row.ArtRequestVersion++;
        row.ArtBitmap = null;
        _artProvider.ReleaseThumbnail(row.Song.Path);
    }

    partial void OnSearchTextChanged(string value)
    {
        UpdateDisplayedSongs();
    }

    [RelayCommand]
    private void PlaySong(SongRow row)
    {
        _queue.PlayFromLibrary(row.Song, SortedActiveSongs, Shuffled);
        StartNormalizationWarmup();
        LoadCurrent();
    }

    [RelayCommand]
    private void JumpToQueueRow(SongRow row)
    {
        var index = _queue.Position + QueueRows.IndexOf(row);
        _queue.JumpTo(index);
        LoadCurrent();
    }

    [RelayCommand]
    private void RemoveFromQueue(SongRow row)
    {
        var index = _queue.Position + QueueRows.IndexOf(row);
        _queue.RemoveAt(index);
        RefreshQueueRows();
        SavePlaybackState(force: true);
    }

    [RelayCommand]
    private void PlayPause()
    {
        var song = _queue.Current;
        if (song is null)
            return;

        if (_pendingPlaybackPath is not null)
            return;

        // Nothing handed to the player yet (fresh launch) — start this track rather than toggling.
        if (!_playerHasCurrentTrack)
        {
            StartPlayback(song.Path);
            return;
        }

        IsPlaying = _player.TogglePause();
        if (IsPlaying)
            _sincePositionReport.Restart();
        UpdateDiscordPresence();
        SavePlaybackState(force: true);
    }

    private void StartPlayback(string path)
    {
        CancelNormalizationAnalysis();

        if (!_settings.AudioNormalizationEnabled)
        {
            _player.NormalizationGainDb = 0;
            StartPlaybackNow(path);
            return;
        }

        if (_loudnessNormalizer.TryGetGainDb(
            path, _settings.AudioNormalizationLevel, out var cachedGain))
        {
            _player.NormalizationGainDb = cachedGain;
            StartPlaybackNow(path);
            return;
        }

        // Do not let either the previous track or this track play at the wrong loudness while the
        // one-time analysis runs. Cached tracks bypass this path and start immediately.
        _player.Pause();
        _playerHasCurrentTrack = false;
        IsPlaying = false;
        _sincePositionReport.Reset();
        _pendingPlaybackPath = path;
        _normalizationCts = new CancellationTokenSource();
        _ = AnalyzeAndApplyNormalizationAsync(path, startPlayback: true, _normalizationCts.Token);
    }

    private void StartPlaybackNow(string path)
    {
        _pendingPlaybackPath = null;
        _player.Play(path);
        _playerHasCurrentTrack = true;
        IsPlaying = true;
        if (_resumePositionSeconds > 0)
        {
            _player.Seek(_resumePositionSeconds);
            _reportedPosition = _resumePositionSeconds;
            ProgressSeconds = _resumePositionSeconds;
            ElapsedText = FormatTime(_resumePositionSeconds);
            _resumePositionSeconds = 0;
        }
        else
        {
            _reportedPosition = 0;
        }
        _sincePositionReport.Restart();
        SavePlaybackState(force: true);
    }

    [RelayCommand]
    private void Next()
    {
        if (_queue.Current is not null && !_activeSongs.Any(s => s.Path == _queue.Current.Path))
        {
            _queue.Build(SortedActiveSongs, Shuffled);
            LoadCurrent();
        }
        else
        {
            _queue.AdvanceOrRebuild(SortedActiveSongs, Shuffled);
            LoadCurrent();
        }
    }

    [RelayCommand]
    private void Previous()
    {
        if (_player.TimePosition > 3)
        {
            _player.Seek(0);
            ProgressSeconds = 0;
            _reportedPosition = 0;
            UpdateDiscordPresence();
            SavePlaybackState(force: true);
        }
        else
        {
            _queue.Previous();
            LoadCurrent();
        }
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        Shuffled = !Shuffled;

        if (Shuffled)
            Reshuffle();
        else
        {
            if (_queue.Current is null || !_activeSongs.Any(s => s.Path == _queue.Current.Path))
            {
                _queue.Build(SortedActiveSongs, shuffled: false);
                if (IsPlaying)
                    LoadCurrent(autoPlay: true);
                else
                    LoadCurrent(autoPlay: false);
            }
            else
            {
                _queue.RestoreOrderKeepingCurrent(SortedActiveSongs);
            }
            RefreshQueueRows();
            StartNormalizationWarmup();
            SavePlaybackState(force: true);
        }
    }

    [RelayCommand]
    private void ToggleLibraryOpen() => IsLibraryOpen = !IsLibraryOpen;

    [RelayCommand]
    private void ToggleUpNextOpen() => IsUpNextOpen = !IsUpNextOpen;

    [RelayCommand]
    private void ToggleLyricsOpen() => IsLyricsOpen = !IsLyricsOpen;

    /// <summary>
    /// Runs when the LRCLIB setting is toggled: turning it on searches for the track that is
    /// already up, rather than leaving the panel empty until the next one starts.
    /// </summary>
    private void ApplyLrcLibSetting()
    {
        if (_queue.Current is { } song)
            LoadLyrics(song);
    }

    private void ApplyLibraryMetadataCacheSetting()
    {
        if (_settings.CacheLibraryMetadata && _libraryPaths.Count > 0)
            _ = ReloadLibraryAsync(restorePlaybackState: false, preservePlayback: true);
    }

    private void ApplyDesktopShortcutSetting(bool enabled)
    {
        if (!WindowsShortcutService.SetDesktopShortcut(enabled))
            StatusText = enabled
                ? "Couldn't create the Desktop shortcut."
                : "Couldn't remove the Desktop shortcut.";
    }

    private void ApplyStartMenuShortcutSetting(bool enabled)
    {
        if (!WindowsShortcutService.SetStartMenuShortcut(enabled))
            StatusText = enabled
                ? "Couldn't create the Start Menu shortcut."
                : "Couldn't remove the Start Menu shortcut.";
    }

    /// <summary>Jumps playback to a clicked lyric. Unsynced lines carry no time and are ignored.</summary>
    [RelayCommand]
    private void SeekToLyric(LyricLineRow? line)
    {
        if (line?.TimeSeconds is { } seconds)
            EndSeek(Math.Clamp(seconds, 0, DurationSeconds));
    }

    [RelayCommand]
    private void EqualizeSidePanels()
    {
        var avg = (LeftPanelWidth + RightPanelWidth) / 2.0;
        avg = Math.Clamp(avg, 180, 500);
        LeftPanelWidth = avg;
        RightPanelWidth = avg;
        IsLibraryOpen = true;
        IsUpNextOpen = true;
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        Theme.SaveToDisk();
    }

    private void ApplyEffects() =>
        _player.ApplyEffects(!_settings.EffectsEnabled ? AudioEffectSettings.None : new AudioEffectSettings
        {
            Clarity = _settings.ClarityAmount,
            Ambience = _settings.AmbienceAmount,
            Surround = _settings.SurroundAmount,
            DynamicBoost = _settings.DynamicBoostAmount,
            BassBoost = _settings.BassBoostAmount,
        });

    private void ApplyEqualizer() =>
        _player.ApplyEqualizer(
            [.. _settings.EqualizerFrequencies.Select(hz => (float)hz)],
            _settings.EqualizerGains,
            _settings.EqualizerEnabled);

    private void ApplyMonoOutputSetting() => _player.MonoOutput = _settings.MonoAudioEnabled;

    private void ApplySurroundSoundSetting() => _player.SurroundSoundEnabled = _settings.SurroundSoundEnabled;

    private void ApplyDiscordPresenceSetting()
    {
        _discordPresence.Configure(_settings.DiscordClientId, _settings.DiscordRichPresenceEnabled);
        UpdateDiscordPresence();
    }

    /// <summary>Pushes the current track/playback-state to Discord. A no-op when the integration
    /// is off or not configured — see <see cref="DiscordPresenceService"/>.</summary>
    private void UpdateDiscordPresence()
    {
        if (_queue.Current is { } song)
            _discordPresence.UpdateNowPlaying(song, IsPlaying, ProgressSeconds, DurationSeconds);
        else
            _discordPresence.Clear();
    }

    private void ApplyNormalizationSetting()
    {
        var currentPath = _queue.Current?.Path;
        if (!_settings.AudioNormalizationEnabled)
        {
            var pendingPath = _pendingPlaybackPath;
            CancelNormalizationAnalysis();
            _player.NormalizationGainDb = 0;

            if (pendingPath is not null && currentPath == pendingPath)
                StartPlaybackNow(pendingPath);
            return;
        }

        if (!_playerHasCurrentTrack || currentPath is null || _pendingPlaybackPath is not null)
            return;

        CancelNormalizationAnalysis();

        if (_loudnessNormalizer.TryGetGainDb(
            currentPath, _settings.AudioNormalizationLevel, out var cachedGain))
        {
            _player.NormalizationGainDb = cachedGain;
            return;
        }

        _player.NormalizationGainDb = 0;
        _normalizationCts = new CancellationTokenSource();
        _ = AnalyzeAndApplyNormalizationAsync(
            currentPath, startPlayback: false, _normalizationCts.Token);
    }

    private async Task AnalyzeAndApplyNormalizationAsync(
        string path, bool startPlayback, CancellationToken ct)
    {
        var gainDb = await _loudnessNormalizer.GetGainDbAsync(
            path, _settings.AudioNormalizationLevel, ct);
        if (ct.IsCancellationRequested)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested || !_settings.AudioNormalizationEnabled ||
                _queue.Current?.Path != path)
                return;

            // The user may have selected another preset while analysis was running. Measurements
            // are target-independent, so derive the gain again from the now-cached values.
            _player.NormalizationGainDb = _loudnessNormalizer.TryGetGainDb(
                path, _settings.AudioNormalizationLevel, out var currentGain)
                ? currentGain
                : gainDb ?? 0;
            if (startPlayback && _pendingPlaybackPath == path)
                StartPlaybackNow(path);
        });
    }

    private void StartNormalizationWarmup()
    {
        CancelNormalizationWarmup();
        if (_library.Count == 0)
            return;

        _normalizationWarmupCts = new CancellationTokenSource();
        var prioritizedPaths = BuildNormalizationCacheOrder();
        _ = _loudnessNormalizer.CacheLibraryAsync(
            prioritizedPaths, _normalizationWarmupCts.Token);
    }

    private IReadOnlyList<string> BuildNormalizationCacheOrder()
    {
        var paths = new List<string>(_library.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Cache what will play next first. Once the queue is exhausted, sweep every remaining
        // library track so leaving STRAMP open eventually produces a complete disk cache.
        var queueStart = Math.Clamp(_queue.Position, 0, _queue.Songs.Count);
        for (var index = queueStart; index < _queue.Songs.Count; index++)
        {
            var path = _queue.Songs[index].Path;
            if (seen.Add(path))
                paths.Add(path);
        }

        foreach (var song in _library)
        {
            if (seen.Add(song.Path))
                paths.Add(song.Path);
        }

        return paths;
    }

    private void CancelNormalizationWarmup()
    {
        _normalizationWarmupCts?.Cancel();
        _normalizationWarmupCts?.Dispose();
        _normalizationWarmupCts = null;
    }

    private void CancelNormalizationAnalysis()
    {
        _normalizationCts?.Cancel();
        _normalizationCts?.Dispose();
        _normalizationCts = null;
        _pendingPlaybackPath = null;
    }

    /// <summary>Re-applies the theme from whichever source is currently active (artwork or manual picks).</summary>
    private void ApplyTheme()
    {
        var palette = _settings.ArtColorMode switch
        {
            AlbumArtColorMode.Inferred when CurrentArtBitmap is not null =>
                _inferredArtPalette ??= AlbumPalette.ExtractInferred(CurrentArtBitmap),
            AlbumArtColorMode.Direct when CurrentArtBitmap is not null =>
                _directArtPalette ??= AlbumPalette.ExtractDirect(CurrentArtBitmap),
            _ => null,
        };

        if (palette is { } artPalette)
            ThemeService.ApplyFromArt(artPalette, animate: !_settings.DisableAnimations);
        else
            ThemeService.Apply(_settings);
    }

    partial void OnIsLibraryOpenChanged(bool value)
    {
        _settings.LibraryPanelOpen = value;
        SettingsService.Save(_settings);
    }

    partial void OnIsUpNextOpenChanged(bool value)
    {
        _settings.UpNextPanelOpen = value;
        SettingsService.Save(_settings);
    }

    partial void OnIsLyricsOpenChanged(bool value)
    {
        _settings.LyricsPanelOpen = value;
        SettingsService.Save(_settings);
    }

    partial void OnLeftPanelWidthChanged(double value)
    {
        _settings.LeftPanelWidth = value;
        SettingsService.Save(_settings);
    }

    partial void OnRightPanelWidthChanged(double value)
    {
        _settings.RightPanelWidth = value;
        SettingsService.Save(_settings);
    }

    partial void OnShuffledChanged(bool value)
    {
        _settings.Shuffle = value;
        SettingsService.Save(_settings);
        SavePlaybackState(force: true);
    }

    partial void OnLoopModeChanged(LoopMode value)
    {
        _settings.LoopMode = value;
        SettingsService.Save(_settings);
        SavePlaybackState(force: true);
    }

    [RelayCommand]
    private void ToggleLoop()
    {
        LoopMode = LoopMode switch
        {
            LoopMode.Off => LoopMode.Playlist,
            LoopMode.Playlist => LoopMode.Track,
            LoopMode.Track => LoopMode.Off,
            _ => LoopMode.Off
        };
    }

    [RelayCommand]
    private void Reshuffle()
    {
        if (_queue.Current is null || !_activeSongs.Any(s => s.Path == _queue.Current.Path))
        {
            _queue.Build(SortedActiveSongs, shuffled: true);
            if (IsPlaying)
                LoadCurrent(autoPlay: true);
            else
                LoadCurrent(autoPlay: false);
        }
        else
        {
            _queue.ReshuffleKeepingCurrent(SortedActiveSongs);
        }
        RefreshQueueRows();
        StartNormalizationWarmup();
        SavePlaybackState(force: true);
    }

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = value;
        _settings.Volume = value;
        SettingsService.Save(_settings);

        // Turning the level up is an implicit unmute; without this the slider (and the volume media
        // keys, which come through here too) would appear dead while muted.
        if (IsMuted && value > 0)
            IsMuted = false;
    }

    partial void OnIsMutedChanged(bool value) => _player.Muted = value;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    /// <summary>Re-enumerates endpoints and re-selects the saved one. Runs each time the picker opens.</summary>
    [RelayCommand]
    private void RefreshOutputDevices()
    {
        var rows = new List<OutputDeviceRow> { SystemDefaultOutputDevice };
        foreach (var device in _player.GetOutputDevices())
            rows.Add(new OutputDeviceRow(device.Id, device.Name));

        var savedId = _settings.OutputDeviceId;
        var saved = rows.FirstOrDefault(row => IsSameDevice(row.Id, savedId));

        // A pinned device that is currently unplugged stays listed, so the picker still shows what
        // playback is meant to route to rather than silently reading as "system default".
        if (saved is null && !string.IsNullOrWhiteSpace(savedId))
        {
            saved = new OutputDeviceRow(savedId, "Unavailable device");
            rows.Add(saved);
        }

        _isRefreshingOutputDevices = true;
        try
        {
            OutputDevices.Clear();
            foreach (var row in rows)
                OutputDevices.Add(row);
            SelectedOutputDevice = saved ?? SystemDefaultOutputDevice;
        }
        finally
        {
            _isRefreshingOutputDevices = false;
        }
    }

    partial void OnSelectedOutputDeviceChanged(OutputDeviceRow? value)
    {
        if (_isRefreshingOutputDevices || value is null)
            return;
        ApplyOutputDevice(value.Id);
    }

    /// <summary>Routes playback to an endpoint, keeping position and playing state across the switch.</summary>
    private void ApplyOutputDevice(string? deviceId)
    {
        if (IsSameDevice(_settings.OutputDeviceId, deviceId))
            return;

        try
        {
            _player.OutputDeviceId = deviceId;
        }
        catch (Exception ex)
        {
            StatusText = $"Could not switch playback device: {ex.Message}";
            return;
        }

        _settings.OutputDeviceId = deviceId;
        SettingsService.Save(_settings);
    }

    private static bool IsSameDevice(string? left, string? right) =>
        string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.OrdinalIgnoreCase);

    public void BeginSeek() => _isSeeking = true;

    /// <summary>Scrub to a 0-1 position without committing the seek yet (drag in progress).</summary>
    public void ScrubTo(double fraction)
    {
        BeginSeek();
        ProgressSeconds = Math.Clamp(fraction, 0, 1) * DurationSeconds;
    }

    /// <summary>Commit a scrub at a 0-1 position.</summary>
    public void CompleteScrub(double fraction) =>
        EndSeek(Math.Clamp(fraction, 0, 1) * DurationSeconds);

    /// <summary>While scrubbing, the elapsed readout follows the handle instead of the player.</summary>
    partial void OnProgressSecondsChanged(double value)
    {
        if (_isSeeking)
            ElapsedText = FormatTime(value);

        UpdateActiveLyric();
    }

    public void EndSeek(double seconds)
    {
        _isSeeking = false;
        if (_playerHasCurrentTrack)
            _player.Seek(seconds);
        else
            _resumePositionSeconds = seconds;
        ProgressSeconds = seconds;
        _reportedPosition = seconds;
        _sincePositionReport.Restart();
        UpdateDiscordPresence();
        SavePlaybackState(force: true);
    }

    /// <summary>
    /// Called every animation frame by the view: advances the displayed position between the
    /// player's infrequent position reports so the progress bar glides instead of stepping.
    /// </summary>
    public void TickProgress()
    {
        SavePlaybackState();
        if (_isSeeking || !IsPlaying || !_sincePositionReport.IsRunning)
            return;

        var estimated = Math.Min(_reportedPosition + _sincePositionReport.Elapsed.TotalSeconds, DurationSeconds);
        ProgressSeconds = estimated;
        ElapsedText = FormatTime(estimated);
    }

    private void LoadCurrent(bool autoPlay = true, double resumePositionSeconds = 0)
    {
        var song = _queue.Current;
        if (song is null)
            return;

        _isAdvancing = false;
        CurrentTitle = song.Title;
        CurrentArtist = song.Artist;
        OnPropertyChanged(nameof(CurrentSong));
        OnPropertyChanged(nameof(HasCurrentSong));
        DurationSeconds = Math.Max(1, song.Duration.TotalSeconds);
        TotalText = FormatTime(song.Duration.TotalSeconds);
        _resumePositionSeconds = Math.Clamp(resumePositionSeconds, 0, DurationSeconds);
        ProgressSeconds = _resumePositionSeconds;
        ElapsedText = FormatTime(_resumePositionSeconds);
        CurrentArtBitmap = null;
        _inferredArtPalette = null;
        _directArtPalette = null;
        _reportedPosition = _resumePositionSeconds;
        _sincePositionReport.Reset();

        if (autoPlay)
        {
            StartPlayback(song.Path);
        }
        else
        {
            // Show the track as "up" but leave the player untouched until the user hits play.
            _playerHasCurrentTrack = false;
            IsPlaying = false;
        }

        VisualizerFeed.LoadTrack(song.Path);
        LoadLyrics(song);

        var requestedPath = song.Path;
        _artProvider.GetArtAsync(song.Path, bitmap =>
        {
            if (_queue.Current?.Path != requestedPath)
                return;

            CurrentArtBitmap = bitmap;
            _inferredArtPalette = null;
            _directArtPalette = null;
            ApplyTheme();
        });

        RefreshQueueRows();
        RefreshCurrentHighlight();
        UpdateDiscordPresence();
        SavePlaybackState(force: true);
    }

    // ── Lyrics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the track's lyrics off the UI thread: a sibling .lrc first, then LRCLIB. Like the
    /// artwork, a result that arrives after the user has moved on is dropped rather than shown
    /// against the wrong song.
    /// </summary>
    private async void LoadLyrics(Song song)
    {
        CancelLyricsLookup();
        var lookup = new CancellationTokenSource();
        _lyricsCts = lookup;

        var searchesOnline = Theme.LrcLibLookup;
        ResetLyrics(searchesOnline ? "Searching lrclib.net…" : "Looking for lyrics…");
        IsLyricsLoading = true;

        LyricsResult result;
        try
        {
            result = await _lyricsProvider.GetAsync(song, searchesOnline, forceRefresh: false, lookup.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer track's lookup has taken over; it owns the panel now.
            return;
        }
        catch
        {
            result = LyricsResult.Empty;
        }
        finally
        {
            if (ReferenceEquals(_lyricsCts, lookup))
            {
                _lyricsCts = null;
                lookup.Dispose();
            }
        }

        if (_disposed || _queue.Current?.Path != song.Path)
            return;

        IsLyricsLoading = false;

        if (!result.HasLines)
        {
            ResetLyrics(StatusFor(result.Origin, searchesOnline));
            return;
        }

        foreach (var line in result.Document.Lines)
            LyricLines.Add(new LyricLineRow(line));

        IsLyricsSynced = result.Document.IsSynced;
        HasLyrics = true;
        LyricsStatusText = "";
        LyricsSourceText = SourceFor(result.Origin, result.Document.IsSynced);
        UpdateActiveLyric();
    }

    /// <summary>Distinguishes "LRCLIB has nothing" from "we never got to ask", which reads the same
    /// to a user staring at an empty panel but means very different things about retrying.</summary>
    private static string StatusFor(LyricsOrigin origin, bool searchedOnline) => origin switch
    {
        LyricsOrigin.Instrumental => "This track is instrumental - no lyrics to show.",
        LyricsOrigin.Unavailable => "Couldn't reach lrclib.net. Check your connection.",
        _ when searchedOnline => "Hm... We couldn't find lyrics for this one.",
        _ => "No lyrics found. Sorry!",
    };

    private static string SourceFor(LyricsOrigin origin, bool synced)
    {
        var kind = synced ? "synced" : "unsynced";
        return origin switch
        {
            LyricsOrigin.LocalFile => $"Local .lrc · {kind}",
            LyricsOrigin.LrcLib or LyricsOrigin.Cache => $"lrclib.net · {kind}",
            _ => "",
        };
    }

    private void ResetLyrics(string status)
    {
        LyricLines.Clear();
        _activeLyricIndex = -1;
        ActiveLyricLine = null;
        HasLyrics = false;
        IsLyricsSynced = false;
        LyricsStatusText = status;
        LyricsSourceText = "";
    }

    private void CancelLyricsLookup()
    {
        var running = _lyricsCts;
        _lyricsCts = null;
        if (running is null)
            return;

        running.Cancel();
        running.Dispose();
        IsLyricsLoading = false;
    }

    /// <summary>
    /// Moves the highlight to whichever line the playback position has reached. Called on every
    /// position change, so the common "still on the same line" case costs a binary search.
    /// </summary>
    private void UpdateActiveLyric()
    {
        if (!IsLyricsSynced || LyricLines.Count == 0)
            return;

        var index = FindLyricIndex(ProgressSeconds);
        if (index == _activeLyricIndex)
            return;

        _activeLyricIndex = index;
        for (var i = 0; i < LyricLines.Count; i++)
        {
            LyricLines[i].IsActive = i == index;
            LyricLines[i].IsPast = i < index;
        }

        ActiveLyricLine = index >= 0 ? LyricLines[index] : null;
    }

    /// <summary>Index of the last line whose timestamp has passed, or -1 before the first line.</summary>
    private int FindLyricIndex(double seconds)
    {
        var low = 0;
        var high = LyricLines.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (LyricLines[mid].TimeSeconds <= seconds)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
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

    private void RefreshQueueRows()
    {
        QueueRows.Clear();
        var songs = _queue.Songs;
        var end = Math.Min(songs.Count, _queue.Position + QueueShowCount);
        for (var i = _queue.Position; i < end; i++)
        {
            var row = new SongRow(songs[i]) { IsCurrent = i == _queue.Position };
            QueueRows.Add(row);
        }
    }

    private void RefreshCurrentHighlight()
    {
        var currentPath = _queue.Current?.Path;
        foreach (var row in LibraryRows)
            row.IsCurrent = row.Song.Path == currentPath;
    }

    private void OnTimePositionChanged(double seconds)
    {
        if (_isSeeking)
            return;

        // Just re-anchor the interpolation baseline; TickProgress does the actual UI updating.
        _reportedPosition = seconds;
        _sincePositionReport.Restart();
    }

    private void OnPlaybackEnded()
    {
        if (_isAdvancing || _isSeeking)
            return;
        _isAdvancing = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (LoopMode == LoopMode.Track)
            {
                var song = _queue.Current;
                if (song is not null)
                {
                    StartPlayback(song.Path);
                    _isAdvancing = false;
                    return;
                }
            }

            if (LoopMode == LoopMode.Off && !_queue.HasNext)
            {
                IsPlaying = false;
                _player.Pause();
                _isAdvancing = false;
                return;
            }

            _queue.AdvanceOrRebuild(SortedActiveSongs, Shuffled);
            LoadCurrent();
        });
    }

    private static string FormatTime(double seconds)
    {
        var total = (int)seconds;
        return $"{total / 60}:{total % 60:D2}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        SavePlaybackState(force: true);
        _disposed = true;
        _libraryScanCts?.Cancel();
        CancelNormalizationAnalysis();
        CancelNormalizationWarmup();
        CancelLyricsLookup();
        _artProvider.Dispose();
        _lyricsProvider.Dispose();
        _loudnessNormalizer.Dispose();
        _discordPresence.Dispose();
        _player.Dispose();
    }
}
