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

    private readonly IMediaPlayer _player;
    private readonly PlaybackQueue _queue = new();
    private readonly AppSettings _settings;
    private readonly AlbumArtProvider _artProvider = new();
    private List<Song> _library = [];

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

    /// <summary>Colors pulled from the current cover, kept so toggling the option can re-apply them.</summary>
    private ArtPalette? _artPalette;

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
    public partial double Volume { get; set; }

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

    public MainWindowViewModel(IMediaPlayer player, AppSettings settings)
    {
        _player = player;
        _settings = settings;

        Shuffled = settings.Shuffle;
        LoopMode = settings.LoopMode;
        Volume = settings.Volume;
        _player.Volume = settings.Volume;
        IsLibraryOpen = settings.LibraryPanelOpen;
        IsUpNextOpen = settings.UpNextPanelOpen;
        LeftPanelWidth = settings.LeftPanelWidth > 0 ? settings.LeftPanelWidth : 280;
        RightPanelWidth = settings.RightPanelWidth > 0 ? settings.RightPanelWidth : 280;
        Theme = new ThemeSettingsViewModel(settings, ApplyTheme);
        Theme.InitializeEqualizer(player.EqualizerBands, ApplyEqualizer);
        ApplyEqualizer();

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
        _settings.LibraryPath = directory;
        SettingsService.Save(_settings);

        _activeSongs = _library;
        PopulateLibraryRows(_library);
        PopulateSources(directory);

        if (_library.Count == 0)
        {
            StatusText = "No supported audio files found in this folder.";
            return;
        }

        StatusText = "";
        _queue.Build(_activeSongs, Shuffled);
        LoadCurrent(autoPlay: false);
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
    }

    [RelayCommand]
    private void BackToSources() => IsBrowsingSources = true;

    private void PopulateLibraryRows(IEnumerable<Song> songs)
    {
        LibraryRows.Clear();
        foreach (var song in songs)
        {
            var row = new SongRow(song);
            LibraryRows.Add(row);
            _artProvider.GetArtAsync(row.Song.Path, bitmap => row.ArtBitmap = bitmap);
        }
        RefreshCurrentHighlight();
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
    }

    [RelayCommand]
    private void PlayPause()
    {
        var song = _queue.Current;
        if (song is null)
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
    }

    private void StartPlayback(string path)
    {
        _player.Play(path);
        _playerHasCurrentTrack = true;
        IsPlaying = true;
        _reportedPosition = 0;
        _sincePositionReport.Restart();
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

    private void ApplyEqualizer() =>
        _player.ApplyEqualizer(_settings.EqualizerGains, _settings.EqualizerEnabled);

    /// <summary>Re-applies the theme from whichever source is currently active (artwork or manual picks).</summary>
    private void ApplyTheme()
    {
        if (_settings.DeriveColorsFromArt && _artPalette is { } palette)
            ThemeService.ApplyFromArt(palette);
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
    }

    partial void OnLoopModeChanged(LoopMode value)
    {
        _settings.LoopMode = value;
        SettingsService.Save(_settings);
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
    }

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = value;
        _settings.Volume = value;
        SettingsService.Save(_settings);
    }

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
        _player.Seek(seconds);
        _reportedPosition = seconds;
        _sincePositionReport.Restart();
    }

    /// <summary>
    /// Called every animation frame by the view: advances the displayed position between the
    /// player's infrequent position reports so the progress bar glides instead of stepping.
    /// </summary>
    public void TickProgress()
    {
        if (_isSeeking || !IsPlaying || !_sincePositionReport.IsRunning)
            return;

        var estimated = Math.Min(_reportedPosition + _sincePositionReport.Elapsed.TotalSeconds, DurationSeconds);
        ProgressSeconds = estimated;
        ElapsedText = FormatTime(estimated);
    }

    private void LoadCurrent(bool autoPlay = true)
    {
        var song = _queue.Current;
        if (song is null)
            return;

        _isAdvancing = false;
        CurrentTitle = song.Title;
        CurrentArtist = song.Artist;
        DurationSeconds = Math.Max(1, song.Duration.TotalSeconds);
        TotalText = FormatTime(song.Duration.TotalSeconds);
        ProgressSeconds = 0;
        ElapsedText = "0:00";
        CurrentArtBitmap = null;
        _reportedPosition = 0;
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
            _artPalette = bitmap is null ? null : AlbumPalette.Extract(bitmap);
            ApplyTheme();
        });

        RefreshQueueRows();
        RefreshCurrentHighlight();
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
            _artProvider.GetArtAsync(row.Song.Path, bitmap => row.ArtBitmap = bitmap);
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

    public void Dispose() => _player.Dispose();
}
