using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
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
    private const double BigNudgeSeconds = 1.0;
    private const int MaxUndoSteps = 100;

    private readonly UserLyricsStore _userLyrics = new();
    private Song? _editorSong;
    private CancellationTokenSource? _publishCts;

    // ── Undo / redo and the autosaved draft ─────────────────────────────────

    private sealed record EditorSnapshot(bool Instrumental, List<(string Text, double? Time)> Lines, int Selected)
    {
        public bool SameContentAs(EditorSnapshot other) =>
            Instrumental == other.Instrumental && Lines.SequenceEqual(other.Lines);
    }

    private sealed record DraftLine(string Text, double? Time);

    private sealed record EditorDraft(bool Instrumental, List<DraftLine> Lines);

    private readonly List<EditorSnapshot> _undoStack = [];
    private readonly List<EditorSnapshot> _redoStack = [];
    private CancellationTokenSource? _draftSaveCts;
    private bool _editorDirty;

    public bool CanUndoEditor => _undoStack.Count > 0;
    public bool CanRedoEditor => _redoStack.Count > 0;

    private EditorSnapshot CaptureEditor() => new(
        EditorInstrumental,
        EditorLines.Select(line => (line.Text, line.TimeSeconds)).ToList(),
        EditorSelectedLine is { } selected ? EditorLines.IndexOf(selected) : -1);

    private void RestoreEditor(EditorSnapshot snapshot)
    {
        EditorInstrumental = snapshot.Instrumental;
        EditorLines.Clear();
        foreach (var (text, time) in snapshot.Lines)
            EditorLines.Add(new LyricEditorLine(text, time));

        EditorSelectedLine = snapshot.Selected >= 0 && snapshot.Selected < EditorLines.Count
            ? EditorLines[snapshot.Selected]
            : null;
        RefreshEditorSummary();
    }

    /// <summary>Call just before any change to the lines, so it can be undone.</summary>
    private void PushUndo()
    {
        _undoStack.Add(CaptureEditor());
        if (_undoStack.Count > MaxUndoSteps)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        MarkEditorDirty();
        NotifyUndoState();
    }

    /// <summary>An edit that ended up changing nothing (cancelled, or an empty new line) leaves no step behind.</summary>
    private void DropUndoIfNoOp()
    {
        if (_undoStack.Count > 0 && _undoStack[^1].SameContentAs(CaptureEditor()))
        {
            _undoStack.RemoveAt(_undoStack.Count - 1);
            NotifyUndoState();
        }
    }

    private void NotifyUndoState()
    {
        OnPropertyChanged(nameof(CanUndoEditor));
        OnPropertyChanged(nameof(CanRedoEditor));
    }

    [RelayCommand]
    public void UndoEditor()
    {
        if (_undoStack.Count == 0)
            return;

        _redoStack.Add(CaptureEditor());
        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        RestoreEditor(snapshot);
        MarkEditorDirty();
        NotifyUndoState();
    }

    [RelayCommand]
    public void RedoEditor()
    {
        if (_redoStack.Count == 0)
            return;

        _undoStack.Add(CaptureEditor());
        var snapshot = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        RestoreEditor(snapshot);
        MarkEditorDirty();
        NotifyUndoState();
    }

    /// <summary>Saves the working copy a moment after the last change, so nothing is lost if the editor is closed.</summary>
    private void MarkEditorDirty()
    {
        _editorDirty = true;
        _draftSaveCts?.Cancel();
        var cts = _draftSaveCts = new CancellationTokenSource();
        _ = SaveDraftLaterAsync(cts.Token);
    }

    private async Task SaveDraftLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        FlushEditorDraft();
    }

    private void FlushEditorDraft()
    {
        _draftSaveCts?.Cancel();
        if (!_editorDirty || _editorSong is not { } song)
            return;

        _editorDirty = false;
        var draft = new EditorDraft(
            EditorInstrumental,
            EditorLines.Select(line => new DraftLine(line.Text, line.TimeSeconds)).ToList());
        _userLyrics.WriteDraft(song.Path, JsonSerializer.Serialize(draft));
    }

    private bool TryLoadDraft(Song song)
    {
        if (_userLyrics.ReadDraft(song.Path) is not { } json)
            return false;

        try
        {
            var draft = JsonSerializer.Deserialize<EditorDraft>(json);
            if (draft is null)
                return false;

            EditorLines.Clear();
            foreach (var line in draft.Lines)
                EditorLines.Add(new LyricEditorLine(line.Text, line.Time));
            EditorInstrumental = draft.Instrumental;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── State ───────────────────────────────────────────────────────────────

    [ObservableProperty]
    public partial bool IsLyricsEditorOpen { get; set; }

    /// <summary>1 edit words and timing, 2 publish.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorSyncStep))]
    [NotifyPropertyChangedFor(nameof(IsEditorPublishStep))]
    public partial int LyricsEditorStep { get; set; }

    public bool IsEditorSyncStep => LyricsEditorStep == 1;
    public bool IsEditorPublishStep => LyricsEditorStep == 2;

    [ObservableProperty]
    public partial string LyricsEditorTitle { get; set; } = "";

    /// <summary>The track has no words: saved locally and, if published, sent to LRCLIB as instrumental.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditorTextEditable))]
    public partial bool EditorInstrumental { get; set; }

    partial void OnEditorInstrumentalChanged(bool value) => MarkEditorDirty();

    public bool IsEditorTextEditable => !EditorInstrumental;

    /// <summary>True when the user already has their own copy saved, which can then be removed.</summary>
    [ObservableProperty]
    public partial bool EditorHasSavedCopy { get; set; }

    /// <summary>The editor opened on unsaved work from an earlier session.</summary>
    [ObservableProperty]
    public partial bool EditorRestoredDraft { get; set; }

    /// <summary>Pasted words waiting to become lines, shown only while there are none.</summary>
    [ObservableProperty]
    public partial string EditorPasteText { get; set; } = "";

    public bool EditorHasLines => EditorLines.Count > 0;
    public bool EditorIsEmpty => EditorLines.Count == 0;

    public ObservableCollection<LyricEditorLine> EditorLines { get; } = [];

    /// <summary>The line that keys act on. Stamping fills it in and moves on to the next.</summary>
    [ObservableProperty]
    public partial LyricEditorLine? EditorSelectedLine { get; set; }

    partial void OnEditorSelectedLineChanged(LyricEditorLine? oldValue, LyricEditorLine? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

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
            return $"{stamped} of {EditorLines.Count} timed";
        }
    }

    // ── Opening and closing ─────────────────────────────────────────────────

    /// <summary>The Edit / Write lyrics button in the lyrics panel: edits the playing track.</summary>
    [RelayCommand]
    private async Task OpenLyricsEditor()
    {
        if (_queue.Current is { } song)
            await OpenLyricsEditorFor(song);
    }

    /// <summary>Right-click "Edit lyrics…" on a song in the library or queue.</summary>
    [RelayCommand]
    private async Task EditLyricsForSong(SongRow? row)
    {
        if (row is not null)
            await OpenLyricsEditorFor(row.Song);
    }

    /// <summary>Opens the editor on any track, starting from unsaved work if there is some, else the lyrics it has.</summary>
    private async Task OpenLyricsEditorFor(Song song)
    {
        LyricsResult result;
        try
        {
            result = await _lyricsProvider.GetAsync(song, Theme.LrcLibLookup);
        }
        catch
        {
            result = LyricsResult.Empty;
        }

        _draftSaveCts?.Cancel();
        _editorSong = song;
        LyricsEditorTitle = $"{song.Artist} – {song.Title}";
        EditorHasSavedCopy = _userLyrics.Exists(song.Path);

        EditorRestoredDraft = TryLoadDraft(song);
        if (!EditorRestoredDraft)
        {
            EditorLines.Clear();
            EditorInstrumental = result.Origin == LyricsOrigin.Instrumental;
            foreach (var line in result.Document.Lines)
            {
                // Plain-text spacing is dropped; timed gaps (instrumental breaks) are kept.
                if (string.IsNullOrWhiteSpace(line.Text) && line.TimeSeconds is null)
                    continue;
                EditorLines.Add(new LyricEditorLine(line.Text, line.TimeSeconds));
            }
        }

        _undoStack.Clear();
        _redoStack.Clear();
        _editorDirty = false;
        NotifyUndoState();

        EditorPasteText = "";
        PublishStatusText = "";
        PublishTrackName = song.Title;
        PublishArtistName = song.Artist;
        PublishAlbumName = song.Album;
        PublishDurationText = Math.Round(song.Duration.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        LyricsEditorStep = 1;
        SelectFirstUnstamped();
        if (!EditorInstrumental)
            EnsureEditorSongPlaying();

        IsLyricsEditorOpen = true;
    }

    /// <summary>Drops the unsaved draft and reopens on what is actually saved.</summary>
    [RelayCommand]
    private async Task RevertEditorDraft()
    {
        if (_editorSong is not { } song)
            return;

        _draftSaveCts?.Cancel();
        _userLyrics.DeleteDraft(song.Path);
        await OpenLyricsEditorFor(song);
    }

    /// <summary>Timing needs the song playing, so a track edited from the list is started first.</summary>
    private void EnsureEditorSongPlaying()
    {
        if (_editorSong is not { } song || _queue.Current?.Path == song.Path)
            return;

        SnapshotPlaybackSource();
        IReadOnlyList<Song> source = SortedPlaybackSongs.ToList();
        if (!source.Any(candidate => candidate.Path == song.Path))
            source = [song];
        _queue.PlayFromLibrary(song, source, Shuffled);
        StartNormalizationWarmup();
        LoadCurrent();
    }

    [RelayCommand]
    private void CloseLyricsEditor()
    {
        _publishCts?.Cancel();
        FlushEditorDraft();
        IsLyricsEditorOpen = false;
    }

    // ── Selection ───────────────────────────────────────────────────────────

    private void SelectFirstUnstamped()
    {
        var first = EditorLines.ToList().FindIndex(line => !line.IsStamped);
        EditorSelectedLine = EditorLines.Count == 0 ? null : EditorLines[first < 0 ? 0 : first];
        RefreshEditorSummary();
    }

    /// <summary>Up / Down: moves the selection a line, jumping the song to it if it has a time.</summary>
    public void MoveEditorSelection(int delta)
    {
        if (EditorLines.Count == 0)
            return;

        var current = EditorSelectedLine is { } selected ? EditorLines.IndexOf(selected) : -1;
        if (current < 0)
            current = delta > 0 ? -1 : EditorLines.Count;

        SelectEditorLine(EditorLines[Math.Clamp(current + delta, 0, EditorLines.Count - 1)]);
    }

    /// <summary>Selects a line, and plays from just before it if it has a time.</summary>
    public void SelectEditorLine(LyricEditorLine line, bool seek = true)
    {
        EditorSelectedLine = line;
        if (seek && line.TimeSeconds is { } seconds)
            EndSeek(Math.Clamp(seconds - 1, 0, DurationSeconds));
    }

    // ── Editing the lines ───────────────────────────────────────────────────

    /// <summary>T: the selected line just started. Stamps it and moves on to the next.</summary>
    public void StampSelectedLine()
    {
        if (!IsEditorSyncStep || EditorLines.Count == 0)
            return;

        // Stamping a paused song would give every line the same moment, so the first T just starts it.
        if (!IsPlaying)
        {
            PlayPauseCommand.Execute(null);
            return;
        }

        if (EditorSelectedLine is null)
            SelectFirstUnstamped();

        if (EditorSelectedLine is not { } line)
            return;

        PushUndo();
        line.TimeSeconds = Math.Max(0, ProgressSeconds - TapLeadSeconds);

        var index = EditorLines.IndexOf(line);
        if (index + 1 < EditorLines.Count)
            SelectEditorLine(EditorLines[index + 1], seek: false);
        RefreshEditorSummary();
    }

    /// <summary>Turns pasted text into lines, one per non-blank row, after the existing ones.</summary>
    [RelayCommand]
    private void AddPastedLines()
    {
        var rows = EditorPasteText.Split('\n').Select(row => row.Trim()).Where(row => row.Length > 0).ToList();
        if (rows.Count == 0)
            return;

        PushUndo();
        foreach (var text in rows)
            EditorLines.Add(new LyricEditorLine(text));

        EditorPasteText = "";
        SelectFirstUnstamped();
    }

    /// <summary>Inserts an empty line after the selected one (or at the end) for the view to start editing.</summary>
    public LyricEditorLine AddEditorLine()
    {
        PushUndo();
        var line = new LyricEditorLine("");
        var at = EditorSelectedLine is { } selected && EditorLines.IndexOf(selected) is var i and >= 0
            ? i + 1
            : EditorLines.Count;
        EditorLines.Insert(at, line);
        EditorSelectedLine = line;
        line.BeginEditText();
        RefreshEditorSummary();
        return line;
    }

    /// <summary>
    /// B: drops an instrumental break (a ♪ line) at the song's current position, in time order, so
    /// a gap the words skip over can be marked as it plays.
    /// </summary>
    [RelayCommand]
    public void AddEditorGap()
    {
        if (!IsEditorSyncStep)
            return;

        PushUndo();
        var time = Math.Max(0, ProgressSeconds);
        var gap = new LyricEditorLine("", time);

        var index = EditorSelectedLine is { } selected && EditorLines.IndexOf(selected) is var i and >= 0
            ? i + 1
            : EditorLines.Count;
        for (var n = 0; n < EditorLines.Count; n++)
        {
            if (EditorLines[n].TimeSeconds is { } existing && existing > time)
            {
                index = n;
                break;
            }
        }

        EditorLines.Insert(Math.Clamp(index, 0, EditorLines.Count), gap);
        EditorSelectedLine = gap;
        RefreshEditorSummary();
    }

    /// <summary>Removes a line (a break, or a lyric line that doesn't belong). Selection stays at the same spot.</summary>
    public void DeleteEditorLine(LyricEditorLine? line = null)
    {
        line ??= EditorSelectedLine;
        if (line is null)
            return;

        var index = EditorLines.IndexOf(line);
        if (index < 0)
            return;

        PushUndo();
        EditorLines.RemoveAt(index);
        EditorSelectedLine = EditorLines.Count > 0 ? EditorLines[Math.Min(index, EditorLines.Count - 1)] : null;
        RefreshEditorSummary();
    }

    /// <summary>Moves a line's time, and lets you hear the result by playing from just before it.</summary>
    public void NudgeEditorLine(LyricEditorLine? line, double seconds)
    {
        line ??= EditorSelectedLine;
        if (line?.TimeSeconds is not { } time)
            return;

        PushUndo();
        var moved = Math.Clamp(time + seconds, 0, Math.Max(DurationSeconds, time + seconds));
        line.TimeSeconds = moved;
        EndSeek(Math.Clamp(moved - 1, 0, DurationSeconds));
    }

    /// <summary>Left / Right: nudges the selected line; Shift makes it a bigger step.</summary>
    public void NudgeSelectedBy(int direction, bool big) =>
        NudgeEditorLine(EditorSelectedLine, direction * (big ? BigNudgeSeconds : NudgeSeconds));

    /// <summary>Shifts every stamped line, to fix a whole pass that was early or late.</summary>
    [RelayCommand]
    private void NudgeAllEditorLines(string? direction)
    {
        PushUndo();
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
        PushUndo();
        foreach (var line in EditorLines)
            line.TimeSeconds = null;
        SelectFirstUnstamped();
    }

    /// <summary>Starts editing a line's words or time in place, as one undoable step.</summary>
    public void BeginEditingLine(LyricEditorLine line, bool time)
    {
        PushUndo();
        EditorSelectedLine = line;
        if (time)
            line.BeginEditTime();
        else
            line.BeginEditText();
    }

    /// <summary>Called after an in-place edit ends. A line left empty and untimed is just removed.</summary>
    public void EditorLineEdited(LyricEditorLine line)
    {
        if (line.Text.Length == 0 && !line.IsStamped && EditorLines.IndexOf(line) is var index and >= 0)
        {
            EditorLines.RemoveAt(index);
            if (ReferenceEquals(EditorSelectedLine, line))
                EditorSelectedLine = EditorLines.Count > 0
                    ? EditorLines[Math.Min(index, EditorLines.Count - 1)]
                    : null;
        }

        DropUndoIfNoOp();
        RefreshEditorSummary();
    }

    private void RefreshEditorSummary()
    {
        OnPropertyChanged(nameof(EditorSyncSummary));
        OnPropertyChanged(nameof(EditorHasTimes));
        OnPropertyChanged(nameof(EditorHasLines));
        OnPropertyChanged(nameof(EditorIsEmpty));
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

    // ── Saving and publishing ───────────────────────────────────────────────

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
                if (lines.Count == 0 || lines.All(line => line.Text.Length == 0 && line.TimeSeconds is null))
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

        // Saved, so the working draft has nothing more to protect.
        _draftSaveCts?.Cancel();
        _editorDirty = false;
        _userLyrics.DeleteDraft(song.Path);

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

        _draftSaveCts?.Cancel();
        _editorDirty = false;
        CloseLyricsEditor();
        StatusText = "Removed your lyrics";
        if (_queue.Current?.Path == song.Path)
            LoadLyrics(song);
    }

    /// <summary>The edited lines in order.</summary>
    private List<LyricLine> CurrentEditorLines() =>
        EditorLines.Select(line => new LyricLine(line.TimeSeconds, line.Text)).ToList();

    [RelayCommand]
    private void EditorGoToPublish()
    {
        PublishSynced = EditorHasTimes && !EditorInstrumental;
        PublishStatusText = "";
        LyricsEditorStep = 2;
    }

    [RelayCommand]
    private void EditorBackFromPublish() => LyricsEditorStep = 1;

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
            || !double.TryParse(PublishDurationText, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var duration) || duration < 1)
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
