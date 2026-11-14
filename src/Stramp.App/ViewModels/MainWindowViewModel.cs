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
using Stramp.Core.Spotify;
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
    private readonly object _listeningStatsLock = new();
    private readonly Stopwatch _listeningStopwatch = new();
    private DateTime _lastListeningSaveUtc = DateTime.UtcNow;
    private readonly ListeningHistoryStore _listeningHistoryStore;
    private readonly ListeningHistoryDatabase _listeningHistory;

    /// <summary>Seconds actually listened to the current track this time through, toward the
    /// "play" threshold applied in <see cref="AccrueListeningTime"/>.</summary>
    private double _currentTrackListenedSeconds;
    private bool _currentTrackPlayRecorded;

    /// <summary>Songs of the selected source (All Songs or a playlist): what the queue is built from.</summary>
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
    public partial bool IsStatsOpen { get; set; }

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

    [ObservableProperty]
    public partial string TotalTimeListenedText { get; set; } = "0s";

    [ObservableProperty]
    public partial string MonthlyTimeListenedText { get; set; } = "0s";

    [ObservableProperty]
    public partial string TotalTimeListenedTooltip { get; set; } = "";

    [ObservableProperty]
    public partial string MonthlyTimeListenedTooltip { get; set; } = "";

    public AudioVisualizerFeed VisualizerFeed { get; } = new();

    public AppSettings Settings => _settings;

    public ThemeSettingsViewModel Theme { get; }

    public SpotifyImportViewModel SpotifyImport { get; }

    public ListeningStatsViewModel Stats { get; } = new();

    public string ListeningHistoryFilePath => _listeningHistoryStore.FilePath;

    public MainWindowViewModel() : this(new WasapiMediaPlayer(), SettingsService.Load())
    {
    }

    public MainWindowViewModel(
        IMediaPlayer player, AppSettings settings, PlaybackStateStore? playbackStateStore = null,
        ListeningHistoryStore? listeningHistoryStore = null)
    {
        _player = player;
        _settings = settings;
        _playbackStateStore = playbackStateStore ?? new PlaybackStateStore();
        _listeningHistoryStore = listeningHistoryStore ?? new ListeningHistoryStore();
        _listeningHistory = _listeningHistoryStore.Load();
        _savedPlaybackState = _playbackStateStore.Load();

        if (_listeningHistory.TotalSecondsListened == 0 && _settings.TotalListenedSeconds > 0)
        {
            _listeningHistory.TotalSecondsListened = _settings.TotalListenedSeconds;
            _listeningHistory.FormattedTotalTime = ListeningDurationFormatter.Format(_listeningHistory.TotalSecondsListened);
            foreach (var kvp in _settings.MonthlyListenedSeconds)
            {
                if (!_listeningHistory.Months.ContainsKey(kvp.Key))
                {
                    var displayName = DateTime.TryParseExact(kvp.Key, "yyyy-MM", null, System.Globalization.DateTimeStyles.None, out var dt)
                        ? dt.ToString("MMMM yyyy")
                        : kvp.Key;

                    _listeningHistory.Months[kvp.Key] = new MonthlyListeningRecord
                    {
                        Month = kvp.Key,
                        DisplayName = displayName,
                        SecondsListened = kvp.Value,
                        FormattedTime = ListeningDurationFormatter.Format(kvp.Value)
                    };
                }
            }
            _listeningHistoryStore.Save(_listeningHistory);
        }

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
        Theme = new ThemeSettingsViewModel(settings, new ThemeSettingsCallbacks(
            ApplyTheme, ApplyNormalizationSetting, ApplyDiscordPresenceSetting,
            ApplyMonoOutputSetting, ApplySurroundSoundSetting, ApplyLrcLibSetting, ApplyLibraryMetadataCacheSetting,
            ApplyDesktopShortcutSetting, ApplyStartMenuShortcutSetting));
        Theme.InitializeEqualizer(player.DefaultEqualizerBands, ApplyEqualizer, ApplyEffects);
        SpotifyImport = new SpotifyImportViewModel(settings, new SpotifyImportCallbacks(
            () => _library, SaveNewPlaylist, message => StatusText = message, OpenSettings));
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
        UpdateListeningStatsDisplay();

        if (_libraryPaths.Any(Directory.Exists))
            _ = ReloadLibraryAsync(restorePlaybackState: true, preservePlayback: false);
        else if (_libraryPaths.Count == 0)
            StatusText = "Add a folder to start building your library.";

        InitializeSetupWizard();
        CheckWhatsNew();
        _ = CheckForUpdatesAsync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        AccrueListeningTime();
        SettingsService.Save(_settings);
        lock (_listeningStatsLock)
        {
            _listeningHistoryStore.Save(_listeningHistory);
        }
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
        _updateChecker.Dispose();
        _player.Dispose();
    }
}
