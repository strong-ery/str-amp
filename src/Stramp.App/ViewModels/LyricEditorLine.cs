using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Lyrics;

namespace Stramp.App.ViewModels;

/// <summary>One line in the lyrics editor's timing step: its words, and when it starts (if stamped yet).</summary>
public partial class LyricEditorLine(string text, double? time = null) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    public partial string Text { get; set; } = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(IsStamped))]
    public partial double? TimeSeconds { get; set; } = time;

    /// <summary>The line keyboard shortcuts act on: stamp, nudge, delete.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>The line the song is on right now, so you can see what you just did.</summary>
    [ObservableProperty]
    public partial bool IsPlayingNow { get; set; }

    /// <summary>Which part is being edited in place (double-click): the words or the time.</summary>
    [ObservableProperty]
    public partial bool IsEditingText { get; set; }

    [ObservableProperty]
    public partial bool IsEditingTime { get; set; }

    /// <summary>What the in-place box holds while editing.</summary>
    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    public bool IsStamped => TimeSeconds is not null;

    /// <summary>A timed line with no words is an instrumental gap, shown as a note like the lyrics panel does.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Text) ? "♪" : Text;

    public string TimeText => TimeSeconds is { } t ? LrcWriter.FormatTime(t) : "--:--.--";

    public void BeginEditText()
    {
        EditText = Text;
        IsEditingTime = false;
        IsEditingText = true;
    }

    public void BeginEditTime()
    {
        EditText = TimeSeconds is { } t ? LrcWriter.FormatTime(t) : "";
        IsEditingText = false;
        IsEditingTime = true;
    }

    public void CancelEdit()
    {
        IsEditingText = false;
        IsEditingTime = false;
    }

    /// <summary>Applies the box. A time that can't be read is dropped, leaving the old one.</summary>
    public void CommitEdit()
    {
        if (IsEditingText)
        {
            Text = EditText.Trim();
        }
        else if (IsEditingTime)
        {
            if (string.IsNullOrWhiteSpace(EditText))
                TimeSeconds = null;
            else if (TryParseTime(EditText, out var seconds))
                TimeSeconds = seconds;
        }

        CancelEdit();
    }

    /// <summary>Reads "m:ss.xx", "m:ss" or plain seconds.</summary>
    internal static bool TryParseTime(string input, out double seconds)
    {
        seconds = 0;
        var parts = input.Trim().Split(':');
        if (parts.Length is < 1 or > 2)
            return false;

        var minutes = 0d;
        if (parts.Length == 2 && !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out minutes))
            return false;
        if (!double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rest))
            return false;

        seconds = minutes * 60 + rest;
        return seconds >= 0;
    }
}
