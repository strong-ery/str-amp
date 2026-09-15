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
using Stramp.Core.Models;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int QueueShowCount = 40;

    /// <summary>The "no pinned endpoint" entry: playback follows whatever Windows is using.</summary>
    private static readonly OutputDeviceRow SystemDefaultOutputDevice = new(null, "System default");

    private readonly IMediaPlayer _player;
    private readonly PlaybackQueue _queue = new();
    private readonly AppSettings _settings;
    private readonly PlaybackStateStore _playbackStateStore;
    private readonly AlbumArtProvider _artProvider = new();
    private readonly LoudnessNormalizationService _loudnessNormalizer = new();
    private readonly DiscordPresenceService _discordPresence = new();
    private CancellationTokenSource? _normalizationCts;
    private CancellationTokenSource? _normalizationWarmupCts;
    private string? _pendingPlaybackPath;
    private List<Song> _library = [];
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

    /// <summary>Both current-cover modes are cached so switching the dropdown is instant.</summary>
    private ArtPalette? _inferredArtPalette;
    private ArtPalette? _directArtPalette;

    public ObservableCollection<SongRow> LibraryRows { get; } = [];
    public ObservableCollection<SongRow> QueueRows { get; } = [];
    public ObservableCollection<LibrarySourceRow> LibrarySources { get; } = [];

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
    public partial Bitmap? CurrentArtBitmap { get; set; }

    [ObservableProperty]
    public partial bool IsLibraryOpen { get; set; } = true;

    [ObservableProperty]
    public partial bool IsUpNextOpen { get; set; } = true;

    [ObservableProperty]
    public partial double LeftPanelWidth { get; set; } = 280;

    [ObservableProperty]
    public partial double RightPanelWidth { get; set; } = 280;

    [ObservableProperty]
    public partial bool IsCompactControlBar { get; set; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

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
        LeftPanelWidth = settings.LeftPanelWidth > 0 ? settings.LeftPanelWidth : 280;
        RightPanelWidth = settings.RightPanelWidth > 0 ? settings.RightPanelWidth : 280;
        Theme = new ThemeSettingsViewModel(
            settings, ApplyTheme, ApplyNormalizationSetting, ApplyDiscordPresenceSetting,
            ApplyMonoOutputSetting);
        Theme.InitializeEqualizer(player.DefaultEqualizerBands, ApplyEqualizer, ApplyEffects);
        ApplyEqualizer();
        ApplyEffects();
        ApplyMonoOutputSetting();
        ApplyDiscordPresenceSetting();
        RefreshOutputDevices();

        LibraryPath = settings.LibraryPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");

        _player.TimePositionChanged += OnTimePositionChanged;
        _player.PlaybackEnded += OnPlaybackEnded;

        if (Directory.Exists(LibraryPath))
            LoadLibrary(LibraryPath);
    }

    public void LoadLibrary(string directory)
    {
        LibraryPath = directory;
        _library = LibraryScanner.Scan(directory);
        CancelNormalizationWarmup();
        _settings.LibraryPath = directory;
        SettingsService.Save(_settings);

        PopulateSources(directory);

        if (_library.Count == 0)
        {
            StatusText = "No supported audio files found in this folder.";
            return;
        }

        StatusText = "";
        if (!TryRestorePlaybackState(directory))
        {
            _activeSongs = _library;
            CurrentSourceName = "All Songs";
            IsBrowsingSources = true;
            PopulateLibraryRows(_activeSongs);
            _queue.Build(_activeSongs, Shuffled);
            _playbackStateReady = true;
            LoadCurrent(autoPlay: false);
        }
        StartNormalizationWarmup();
    }

    private bool TryRestorePlaybackState(string directory)
    {
        var state = _savedPlaybackState;
        _savedPlaybackState = null;
        if (state is null || !string.Equals(
                Path.GetFullPath(state.LibraryPath ?? ""),
                Path.GetFullPath(directory),
                StringComparison.OrdinalIgnoreCase))
            return false;

        var songsByPath = _library.ToDictionary(song => song.Path, StringComparer.OrdinalIgnoreCase);
        var savedActiveSongs = ResolveSongs(state.ActiveSongPaths, songsByPath);
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
        PopulateLibraryRows(_activeSongs);

        Shuffled = state.Shuffled;
        LoopMode = state.LoopMode;
        var restoredQueue = ResolveSongs(state.QueuePaths, songsByPath);
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
            _queue.Build(_activeSongs, Shuffled,
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

    private void PopulateSources(string directory)
    {
        LibrarySources.Clear();
        LibrarySources.Add(new LibrarySourceRow("All Songs", _library));

        var importedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in PlaylistScanner.FromSaved(_settings.Playlists, _library))
        {
            importedNames.Add(playlist.Name);
            LibrarySources.Add(new LibrarySourceRow(playlist.Name, playlist.Songs));
        }

        if (!Directory.Exists(directory))
            return;

        foreach (var playlist in PlaylistScanner.Scan(directory, _library))
        {
            // Prefer the imported copy when the same name still exists as a file under the library.
            if (importedNames.Contains(playlist.Name))
                continue;
            LibrarySources.Add(new LibrarySourceRow(playlist.Name, playlist.Songs));
        }
    }

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
            SongPaths = paths,
        });
        SettingsService.Save(_settings);

        PopulateSources(LibraryPath);

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
        PopulateLibraryRows(_activeSongs);
        SavePlaybackState(force: true);
    }

    [RelayCommand]
    private void BackToSources()
    {
        IsBrowsingSources = true;
        SavePlaybackState(force: true);
    }

    private void PopulateLibraryRows(IEnumerable<Song> songs)
    {
        LibraryRows.Clear();
        foreach (var song in songs)
        {
            var row = new SongRow(song);
            LibraryRows.Add(row);
        }
        RefreshCurrentHighlight();
    }

    /// <summary>Called when Avalonia materializes a visible virtualized song row.</summary>
    public void LoadRowArt(SongRow row)
    {
        if (row.IsArtRequested)
            return;

        row.IsArtRequested = true;
        _artProvider.GetThumbnailAsync(row.Song.Path, bitmap =>
        {
            if (row.IsArtRequested)
                row.ArtBitmap = bitmap;
        });
    }

    /// <summary>Releases an off-screen row while retaining a small shared thumbnail cache.</summary>
    public void ReleaseRowArt(SongRow row)
    {
        if (!row.IsArtRequested)
            return;
        row.IsArtRequested = false;
        row.ArtBitmap = null;
        _artProvider.ReleaseThumbnail(row.Song.Path);
    }

    partial void OnSearchTextChanged(string value)
    {
        var q = value.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _activeSongs
            : _activeSongs.Where(s =>
                s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Artist.Contains(q, StringComparison.OrdinalIgnoreCase));
        PopulateLibraryRows(filtered);
    }

    [RelayCommand]
    private void PlaySong(SongRow row)
    {
        _queue.PlayFromLibrary(row.Song, _activeSongs, Shuffled);
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
        _queue.AdvanceOrRebuild(_activeSongs, Shuffled);
        LoadCurrent();
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
            _queue.RestoreOrderKeepingCurrent(_activeSongs);
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
            ThemeService.ApplyFromArt(artPalette);
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
        _queue.ReshuffleKeepingCurrent(_activeSongs);
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
            : _activeSongs.FindIndex(song => string.Equals(
                song.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        var state = new SavedPlaybackState
        {
            LibraryPath = LibraryPath,
            SourceName = CurrentSourceName,
            IsBrowsingSources = IsBrowsingSources,
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

            _queue.AdvanceOrRebuild(_activeSongs, Shuffled);
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
        CancelNormalizationAnalysis();
        CancelNormalizationWarmup();
        _artProvider.Dispose();
        _loudnessNormalizer.Dispose();
        _discordPresence.Dispose();
        _player.Dispose();
    }
}
