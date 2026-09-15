namespace Stramp.Core.Lyrics;

/// <summary>Where a set of lyrics came from, so the panel can say what it is showing.</summary>
public enum LyricsOrigin
{
    /// <summary>Nothing found anywhere.</summary>
    None,

    /// <summary>An .lrc sitting beside the track.</summary>
    LocalFile,

    /// <summary>A previous LRCLIB lookup, replayed from disk.</summary>
    Cache,

    /// <summary>Freshly downloaded from lrclib.net.</summary>
    LrcLib,

    /// <summary>LRCLIB has the track on file and says it has no words.</summary>
    Instrumental,

    /// <summary>LRCLIB could not be reached at all — worth retrying, unlike a plain miss.</summary>
    Unavailable,
}

public sealed record LyricsResult(LyricsDocument Document, LyricsOrigin Origin)
{
    public static readonly LyricsResult Empty = new(LyricsDocument.Empty, LyricsOrigin.None);

    public bool HasLines => Document.HasLines;
}
