namespace Stramp.Core.Lyrics;

/// <summary>
/// One line of lyrics. <paramref name="TimeSeconds"/> is null for unsynced files, which carry no
/// timestamps at all — those lines are shown as a static block instead of being highlighted.
/// </summary>
public sealed record LyricLine(double? TimeSeconds, string Text);
