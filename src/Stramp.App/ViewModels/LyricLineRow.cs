using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Lyrics;

namespace Stramp.App.ViewModels;

/// <summary>
/// One row of the lyrics panel. Synced lines light up as playback reaches them and seek when
/// clicked; unsynced lines have no time, so they just render as a static block of text.
/// </summary>
public partial class LyricLineRow(LyricLine line) : ObservableObject
{
    public double? TimeSeconds { get; } = line.TimeSeconds;

    /// <summary>A timed line with no words is an instrumental gap — a note keeps the pacing visible.</summary>
    public string DisplayText { get; } =
        line.TimeSeconds is not null && string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;

    /// <summary>Only a timed line maps to a playback position, so only those are clickable.</summary>
    public bool IsSeekable => TimeSeconds is not null;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Lines already sung, dimmed further than the ones still to come.</summary>
    [ObservableProperty]
    public partial bool IsPast { get; set; }
}
