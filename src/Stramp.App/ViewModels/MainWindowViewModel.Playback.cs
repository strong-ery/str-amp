using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Library;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void PlaySong(SongRow row)
    {
        // Picking a song from the browsed source makes that source the playing one.
        SnapshotPlaybackSource();
        _queue.PlayFromLibrary(row.Song, SortedPlaybackSongs, Shuffled);
        StartNormalizationWarmup();
        LoadCurrent();
    }

    public void PlayDroppedFiles(IReadOnlyList<string> filePaths)
    {
        if (filePaths.Count == 0)
            return;

        var songs = filePaths.Select(LibraryScanner.ReadSong).ToList();
        _queue.PlayFromLibrary(songs[0], songs, false);
        StartNormalizationWarmup();
        LoadCurrent();
    }

    public void PlayDroppedFile(string filePath)
    {
        PlayDroppedFiles([filePath]);
    }

    // True when the queue is playing something neither from the source nor added to the queue by
    // hand, such as dropped-in files; skipping or reshuffling then starts the source again.
    private bool CurrentIsOutsideSource =>
        _queue.Current is { } song && !_queue.CurrentIsQueued
        && !_playbackSongs.Any(s => s.Path == song.Path);

    [RelayCommand]
    private void PlayPause()
    {
        var song = _queue.Current;
        if (song is null)
            return;

        if (_pendingPlaybackPath is not null)
            return;

        // Nothing handed to the player yet (fresh launch); start this track rather than toggling.
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
        AccrueListeningTime();
        lock (_listeningStatsLock)
        {
            // A fresh start of this track, whether it's a new song or a track-mode loop
            // replaying the same one: either way, it needs its own run at the play threshold.
            _currentTrackListenedSeconds = 0;
            _currentTrackPlayRecorded = false;
        }
        _pendingPlaybackPath = null;
        try
        {
            _player.Play(path);
            _playerHasCurrentTrack = true;
            IsPlaying = true;
            if (!_listeningStopwatch.IsRunning)
                _listeningStopwatch.Restart();
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
        catch (Exception ex)
        {
            _playerHasCurrentTrack = false;
            IsPlaying = false;
            _sincePositionReport.Reset();
            StatusText = $"Unable to play '{_queue.Current?.Title ?? Path.GetFileName(path)}': {ex.Message}";
            SavePlaybackState(force: true);
        }
    }

    [RelayCommand]
    private void Next()
    {
        if (CurrentIsOutsideSource && !_queue.NextIsQueued)
        {
            _queue.Build(SortedPlaybackSongs, Shuffled);
            LoadCurrent();
        }
        else
        {
            _queue.AdvanceOrRebuild(SortedPlaybackSongs, Shuffled);
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
            if (_queue.Current is null || CurrentIsOutsideSource)
            {
                _queue.Build(SortedPlaybackSongs, shuffled: false);
                if (IsPlaying)
                    LoadCurrent(autoPlay: true);
                else
                    LoadCurrent(autoPlay: false);
            }
            else
            {
                _queue.RestoreOrderKeepingCurrent(SortedPlaybackSongs);
            }
            RefreshQueueRows();
            StartNormalizationWarmup();
            SavePlaybackState(force: true);
        }
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
        if (_queue.Current is null || CurrentIsOutsideSource)
        {
            _queue.Build(SortedPlaybackSongs, shuffled: true);
            if (IsPlaying)
                LoadCurrent(autoPlay: true);
            else
                LoadCurrent(autoPlay: false);
        }
        else
        {
            _queue.ReshuffleKeepingCurrent(SortedPlaybackSongs);
        }
        RefreshQueueRows();
        StartNormalizationWarmup();
        SavePlaybackState(force: true);
    }

}
