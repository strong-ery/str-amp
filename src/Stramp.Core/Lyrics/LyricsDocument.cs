namespace Stramp.Core.Lyrics;

/// <summary>A parsed .lrc file. Synced and unsynced files both land here; only the flag differs.</summary>
/// <param name="Lines">Display order: ascending time when synced, file order otherwise.</param>
/// <param name="IsSynced">True when at least one line carried a timestamp.</param>
public sealed record LyricsDocument(
    IReadOnlyList<LyricLine> Lines,
    bool IsSynced,
    string? Title = null,
    string? Artist = null)
{
    public static readonly LyricsDocument Empty = new([], IsSynced: false);

    public bool HasLines => Lines.Count > 0;
}
