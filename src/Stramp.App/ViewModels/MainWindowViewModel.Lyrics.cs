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
    /// <summary>
    /// Runs when the LRCLIB setting is toggled: turning it on searches for the track that is
    /// already up, rather than leaving the panel empty until the next one starts.
    /// </summary>
    private void ApplyLrcLibSetting()
    {
        if (_queue.Current is { } song)
            LoadLyrics(song);
    }

    /// <summary>Jumps playback to a clicked lyric. Unsynced lines carry no time and are ignored.</summary>
    [RelayCommand]
    private void SeekToLyric(LyricLineRow? line)
    {
        if (line?.TimeSeconds is { } seconds)
            EndSeek(Math.Clamp(seconds, 0, DurationSeconds));
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

}
