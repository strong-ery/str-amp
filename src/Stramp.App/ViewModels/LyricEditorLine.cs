using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Lyrics;

namespace Stramp.App.ViewModels;

/// <summary>One line in the lyrics editor's timing step: its words, and when it starts (if stamped yet).</summary>
public partial class LyricEditorLine(string text, double? time = null) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    [NotifyPropertyChangedFor(nameof(IsStamped))]
    public partial double? TimeSeconds { get; set; } = time;

    /// <summary>The next line waiting to be stamped.</summary>
    [ObservableProperty]
    public partial bool IsCursor { get; set; }

    /// <summary>The line the song is on right now, so you can see what you just did.</summary>
    [ObservableProperty]
    public partial bool IsPlayingNow { get; set; }

    public bool IsStamped => TimeSeconds is not null;

    /// <summary>A timed line with no words is an instrumental gap, shown as a note like the lyrics panel does.</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Text) ? "♪" : Text;

    public string TimeText => TimeSeconds is { } t ? LrcWriter.FormatTime(t) : "--:--.--";
}
