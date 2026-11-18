using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Lyrics;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    /// <summary>People tap a beat after they hear the line start, so a stamp lands this much early.</summary>
    private const double TapLeadSeconds = 0.12;

    private const double NudgeSeconds = 0.1;

    private readonly UserLyricsStore _userLyrics = new();
    private Song? _editorSong;
    private CancellationTokenSource? _publishCts;

    [ObservableProperty]
    public partial bool IsLyricsEditorOpen { get; set; }

    /// <summary>0 write the words, 1 time them, 2 publish.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorWriteStep))]
    [NotifyPropertyChangedFor(nameof(IsEditorSyncStep))]
    [NotifyPropertyChangedFor(nameof(IsEditorPublishStep))]
    public partial int LyricsEditorStep { get; set; }

    public bool IsEditorWriteStep => LyricsEditorStep == 0;
    public bool IsEditorSyncStep => LyricsEditorStep == 1;
    public bool IsEditorPublishStep => LyricsEditorStep == 2;

    [ObservableProperty]
    public partial string LyricsEditorTitle { get; set; } = "";

    /// <summary>The track has no words: saved locally and, if published, sent to LRCLIB as instrumental.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorTextEditable))]
    public partial bool EditorInstrumental { get; set; }

    public bool IsEditorTextEditable => !EditorInstrumental;

    /// <summary>True when the user already has their own copy saved, which can then be removed.</summary>
    [ObservableProperty]
    public partial bool EditorHasSavedCopy { get; set; }

    /// <summary>The raw words, one line each, edited in the first step.</summary>
    [ObservableProperty]
    public partial string LyricsEditorText { get; set; } = "";

    public ObservableCollection<LyricEditorLine> EditorLines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorSyncSummary))]
    public partial int EditorCursor { get; set; }

    [ObservableProperty]
    public partial LyricEditorLine? EditorSelectedLine { get; set; }

    /// <summary>The line the song is on, so the list can scroll to keep it in view.</summary>
    [ObservableProperty]
    public partial LyricEditorLine? EditorPlayingLine { get; set; }

    [ObservableProperty]
    public partial string PublishTrackName { get; set; } = "";

    [ObservableProperty]
    public partial string PublishArtistName { get; set; } = "";

    [ObservableProperty]
    public partial string PublishAlbumName { get; set; } = "";

    [ObservableProperty]
    public partial string PublishDurationText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPublish))]
    public partial bool IsPublishing { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPublishStatus))]
    public partial string PublishStatusText { get; set; } = "";

    public bool HasPublishStatus => PublishStatusText.Length > 0;

    [ObservableProperty]
    public partial bool PublishSynced { get; set; } = true;

    /// <summary>Whether any line has a time, which decides if a synced copy exists to publish.</summary>
    public bool EditorHasTimes => EditorLines.Any(line => line.IsStamped);

    public bool CanPublish => !IsPublishing;

    public string EditorSyncSummary
    {
        get
        {
            var stamped = EditorLines.Count(line => line.IsStamped);
            return $"{stamped} of {EditorLines.Count} lines timed";
        }
    }

    /// <summary>Opens the editor on the playing track, starting from whatever lyrics it already shows.</summary>
    [RelayCommand]
    private void OpenLyricsEditor()
    {
        if (_queue.Current is not { } song)
            return;

        _editorSong = song;
        LyricsEditorTitle = $"{song.Artist} – {song.Title}";

        EditorLines.Clear();
        EditorInstrumental = LyricsIsInstrumental;
        EditorHasSavedCopy = _userLyrics.Exists(song.Path);

        // Timed gaps (instrumental breaks) are kept; blank lines in plain text are just spacing.
        var words = new List<string>();
        foreach (var row in LyricLines)
        {
            if (string.IsNullOrWhiteSpace(row.RawText) && row.TimeSeconds is null)
            {
                words.Add("");
                continue;
            }
            EditorLines.Add(new LyricEditorLine(row.RawText, row.TimeSeconds));
            words.Add(row.RawText);
        }

        LyricsEditorText = string.Join('\n', words).Trim('\n');
        PublishStatusText = "";
        PublishTrackName = song.Title;
        PublishArtistName = song.Artist;
        PublishAlbumName = song.Album;
        PublishDurationText = Math.Round(song.Duration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        LyricsEditorStep = EditorLines.Count > 0 && !EditorInstrumental ? 1 : 0;
        if (LyricsEditorStep == 1)
            ResetEditorCursor();

        IsLyricsEditorOpen = true;
    }

    [RelayCommand]
    private void CloseLyricsEditor()
    {
        _publishCts?.Cancel();
        IsLyricsEditorOpen = false;
    }

    [RelayCommand]
    private void EditorBackToWrite()
    {
        // Keep what has been typed in the write step in step with the timed lines.
        LyricsEditorText = string.Join('\n', EditorLines.Select(line => line.Text)).Trim('\n');
        LyricsEditorStep = 0;
    }

    /// <summary>Moves from the words to the timing, keeping the stamps of lines whose words didn't change.</summary>
    [RelayCommand]
    private void EditorGoToSync()
    {
        var previous = new Dictionary<string, Queue<double?>>();
        foreach (var line in EditorLines)
        {
            if (!previous.TryGetValue(line.Text, out var times))
                previous[line.Text] = times = new Queue<double?>();
            times.Enqueue(line.TimeSeconds);
        }

        var words = LyricsEditorText.Split('\n').Select(text => text.Trim()).ToList();

        EditorLines.Clear();
        foreach (var text in words)
        {
            double? time = null;
            if (previous.TryGetValue(text, out var times) && times.Count > 0)
                time = times.Dequeue();

            // A blank line only survives as a timed instrumental gap; otherwise it is spacing.
            if (text.Length == 0 && time is null)
                continue;
            EditorLines.Add(new LyricEditorLine(text, time));
        }

        LyricsEditorStep = 1;
        ResetEditorCursor();
    }

    /// <summary>Puts the cursor on the first line still waiting for a time.</summary>
    private void ResetEditorCursor()
    {
        var first = EditorLines.ToList().FindIndex(line => !line.IsStamped);
        SetEditorCursor(first < 0 ? EditorLines.Count : first);
        OnPropertyChanged(nameof(EditorSyncSummary));
        OnPropertyChanged(nameof(EditorHasTimes));
    }

    private void SetEditorCursor(int index)
    {
        EditorCursor = Math.Clamp(index, 0, EditorLines.Count);
        for (var i = 0; i < EditorLines.Count; i++)
            EditorLines[i].IsCursor = i == EditorCursor;
    }

    /// <summary>Space or Enter: the next line just started. Stamps it and moves on.</summary>
    public void StampNextLine()
    {
        if (!IsEditorSyncStep || EditorCursor >= EditorLines.Count)
            return;

        EditorLines[EditorCursor].TimeSeconds = Math.Max(0, ProgressSeconds - TapLeadSeconds);
        SetEditorCursor(EditorCursor + 1);
        RefreshEditorSummary();
    }

    /// <summary>Backspace: forget the last stamp and jump the song back to just before it, so it can be retaken.</summary>
    public void StepBackLine()
    {
        if (!IsEditorSyncStep || EditorCursor <= 0)
            return;

        var index = EditorCursor - 1;
        var target = EditorLines[index].TimeSeconds;
        EditorLines[index].TimeSeconds = null;
        SetEditorCursor(index);
        if (target is { } seconds)
            EndSeek(Math.Clamp(seconds - 2, 0, DurationSeconds));
        RefreshEditorSummary();
    }

    /// <summary>Clicking a line plays from it if it has a time, and makes it the next to stamp.</summary>
    public void SelectEditorLine(LyricEditorLine line)
    {
        EditorSelectedLine = line;
        if (line.TimeSeconds is { } seconds)
            EndSeek(Math.Clamp(seconds - 1, 0, DurationSeconds));
        SetEditorCursor(EditorLines.IndexOf(line));
    }

    /// <summary>
    /// Drops an instrumental gap (a ♪ line) at the song's current position, in time order, so a
    /// break the words skip over can be marked as it plays. The next line to stamp stays put.
    /// </summary>
    [RelayCommand]
    public void AddEditorGap()
    {
        if (!IsEditorSyncStep)
            return;

        var time = Math.Max(0, ProgressSeconds);
        var gap = new LyricEditorLine("", time);

        var index = EditorCursor;
        for (var i = 0; i < EditorLines.Count; i++)
        {
            if (EditorLines[i].TimeSeconds is { } existing && existing > time)
            {
                index = i;
                break;
            }
        }

        index = Math.Clamp(index, 0, EditorLines.Count);
        EditorLines.Insert(index, gap);
        SetEditorCursor(index < EditorCursor || index == EditorCursor ? EditorCursor + 1 : EditorCursor);
        EditorSelectedLine = gap;
        RefreshEditorSummary();
    }

    /// <summary>Removes the selected line (a gap, or a lyric line that doesn't belong).</summary>
    [RelayCommand]
    public void DeleteEditorLine()
    {
        if (EditorSelectedLine is not { } line)
            return;

        var index = EditorLines.IndexOf(line);
        if (index < 0)
            return;

        EditorLines.RemoveAt(index);
        EditorSelectedLine = null;
        SetEditorCursor(index < EditorCursor ? EditorCursor - 1 : EditorCursor);
        RefreshEditorSummary();
    }

    [RelayCommand]
    private void NudgeEditorLine(string? direction)
    {
        if (EditorSelectedLine is not { TimeSeconds: { } time } line)
            return;

        var delta = direction == "-" ? -NudgeSeconds : NudgeSeconds;
        line.TimeSeconds = Math.Clamp(time + delta, 0, Math.Max(DurationSeconds, time + delta));
    }

    /// <summary>Shifts every stamped line, to fix a whole pass that was early or late.</summary>
    [RelayCommand]
    private void NudgeAllEditorLines(string? direction)
    {
        var delta = direction == "-" ? -NudgeSeconds : NudgeSeconds;
        foreach (var line in EditorLines)
        {
            if (line.TimeSeconds is { } time)
                line.TimeSeconds = Math.Max(0, time + delta);
        }
    }

    [RelayCommand]
    private void ClearEditorTimes()
    {
        foreach (var line in EditorLines)
            line.TimeSeconds = null;
        SetEditorCursor(0);
        RefreshEditorSummary();
    }

    [RelayCommand]
    private void EditorToggleStartPlayback() => PlayPause();

    private void RefreshEditorSummary()
    {
        OnPropertyChanged(nameof(EditorSyncSummary));
        OnPropertyChanged(nameof(EditorHasTimes));
    }

    /// <summary>Called as the song plays so the editor shows which line is on, like the panel does.</summary>
    private void UpdateEditorPlayingLine()
    {
        if (!IsLyricsEditorOpen || !IsEditorSyncStep)
            return;

        var now = -1;
        for (var i = 0; i < EditorLines.Count; i++)
        {
            if (EditorLines[i].TimeSeconds is { } time && time <= ProgressSeconds)
                now = i;
        }

        for (var i = 0; i < EditorLines.Count; i++)
        {
            var isNow = i == now;
            if (EditorLines[i].IsPlayingNow != isNow)
                EditorLines[i].IsPlayingNow = isNow;
        }

        var playing = now >= 0 ? EditorLines[now] : null;
        if (!ReferenceEquals(EditorPlayingLine, playing))
            EditorPlayingLine = playing;
    }

    /// <summary>Saves the lyrics as the user's own: timed if any line has a time, plain otherwise.</summary>
    [RelayCommand]
    private void SaveUserLyrics()
    {
        if (_editorSong is not { } song)
            return;

        var synced = false;
        try
        {
            if (EditorInstrumental)
            {
                _userLyrics.WriteInstrumental(song.Path);
            }
            else
            {
                var lines = CurrentEditorLines();
                if (lines.Count == 0 || lines.All(line => line.Text.Length == 0))
                {
                    StatusText = "There are no lyrics to save yet.";
                    return;
                }

                synced = lines.Any(line => line.TimeSeconds is not null);
                var text = synced
                    ? LrcWriter.WriteSynced(lines.Where(line => line.TimeSeconds is not null))
                    : LrcWriter.WritePlain(lines);
                _userLyrics.Write(song.Path, text);
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't save the lyrics: {ex.Message}";
            return;
        }

        CloseLyricsEditor();
        StatusText = EditorInstrumental ? "Marked as instrumental"
            : synced ? "Saved your synced lyrics" : "Saved your lyrics";
        if (_queue.Current?.Path == song.Path)
            LoadLyrics(song);
    }

    /// <summary>Throws away the user's own copy so the track goes back to a .lrc on disk or LRCLIB.</summary>
    [RelayCommand]
    private void RemoveUserLyrics()
    {
        if (_editorSong is not { } song)
            return;

        try
        {
            _userLyrics.Delete(song.Path);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't remove your lyrics: {ex.Message}";
            return;
        }

        CloseLyricsEditor();
        StatusText = "Removed your lyrics";
        if (_queue.Current?.Path == song.Path)
            LoadLyrics(song);
    }

    /// <summary>The edited lines in order. In the write step they come from the text box.</summary>
    private List<LyricLine> CurrentEditorLines()
    {
        if (IsEditorWriteStep)
        {
            return LyricsEditorText
                .Split('\n')
                .Select(text => text.Trim())
                .Select(text => new LyricLine(null, text))
                .ToList();
        }

        return EditorLines.Select(line => new LyricLine(line.TimeSeconds, line.Text)).ToList();
    }

    [RelayCommand]
    private void EditorGoToPublish()
    {
        if (!EditorInstrumental)
            SaveEditedTextIfWriting();
        PublishSynced = EditorHasTimes && !EditorInstrumental;
        PublishStatusText = "";
        LyricsEditorStep = 2;
    }

    private void SaveEditedTextIfWriting()
    {
        if (IsEditorWriteStep)
            EditorGoToSync();
    }

    [RelayCommand]
    private void EditorBackFromPublish() => LyricsEditorStep = EditorInstrumental ? 0 : 1;

    /// <summary>Uploads to lrclib.net. Only runs from the publish step, after the user confirmed the details.</summary>
    [RelayCommand]
    private async Task PublishToLrcLibAsync()
    {
        if (IsPublishing)
            return;

        var lines = EditorLines.Select(line => new LyricLine(line.TimeSeconds, line.Text)).ToList();
        if (!EditorInstrumental && lines.Count == 0)
        {
            PublishStatusText = "Add some lyrics first.";
            return;
        }

        var synced = PublishSynced && !EditorInstrumental && lines.All(line => line.TimeSeconds is not null);
        if (PublishSynced && !EditorInstrumental && !synced)
        {
            PublishStatusText = "Time every line before publishing synced lyrics, or switch to plain.";
            return;
        }

        if (string.IsNullOrWhiteSpace(PublishTrackName) || string.IsNullOrWhiteSpace(PublishArtistName)
            || !double.TryParse(PublishDurationText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var duration) || duration < 1)
        {
            PublishStatusText = "Title, artist and a duration in seconds are required.";
            return;
        }

        var submission = new LrcLibSubmission(
            PublishTrackName.Trim(),
            PublishArtistName.Trim(),
            PublishAlbumName.Trim(),
            duration,
            // Empty lyrics are how LRCLIB is told a track is instrumental.
            EditorInstrumental ? "" : LrcWriter.ToPlain(lines),
            synced ? LrcWriter.WriteSynced(lines) : "");

        _publishCts?.Cancel();
        var cts = _publishCts = new CancellationTokenSource();
        IsPublishing = true;
        PublishStatusText = "Solving LRCLIB's challenge and uploading…";

        try
        {
            using var publisher = new LrcLibPublisher();
            var result = await publisher.PublishAsync(submission, cts.Token);
            PublishStatusText = result.Message;
            if (result.Outcome == PublishOutcome.Published)
                StatusText = "Published your lyrics to lrclib.net";
        }
        catch (OperationCanceledException)
        {
            PublishStatusText = "Cancelled.";
        }
        finally
        {
            IsPublishing = false;
        }
    }
}
