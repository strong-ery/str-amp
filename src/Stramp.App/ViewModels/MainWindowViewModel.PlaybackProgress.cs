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
    public void BeginSeek()
    {
        if (!_isSeeking)
        {
            AccrueListeningTime();
            _listeningStopwatch.Reset();
        }
        _isSeeking = true;
    }

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
        if (IsPlaying)
            _listeningStopwatch.Restart();
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

        if (_listeningStopwatch.IsRunning && _listeningStopwatch.Elapsed >= TimeSpan.FromSeconds(1))
            AccrueListeningTime();
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

        if (IsPlaying && _listeningStopwatch.IsRunning && _listeningStopwatch.Elapsed >= TimeSpan.FromSeconds(1))
            AccrueListeningTime();
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

}
