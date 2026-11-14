using System.Text.RegularExpressions;

namespace Stramp.Core.Spotify;

/// <summary>One track of a Spotify playlist, as much of it as matching against a local library needs.</summary>
/// <remarks>
/// <see cref="Artists"/> lists every credited artist separately. <see cref="ReleaseDate"/> is Spotify's
/// string, which may be a year, a year and month, or a full date.
/// </remarks>
public sealed record SpotifyTrack(string Name, IReadOnlyList<string> Artists, string Album, string ReleaseDate);

/// <summary>A Spotify playlist's name and tracks, in playlist order.</summary>
/// <remarks>
/// Built by <see cref="SpotifyApi"/>. Podcast episodes and entries Spotify returns without a track are
/// left out, so <see cref="Tracks"/> can be shorter than the playlist shows in Spotify.
/// </remarks>
public sealed record SpotifyPlaylist(string Name, IReadOnlyList<SpotifyTrack> Tracks)
{
    private static readonly Regex IdPattern = new(@"(?:playlist[/:]|^)([a-zA-Z0-9]{22})(?:$|[/?#])");

    // Accepts an open.spotify.com link, a spotify:playlist: URI or a bare 22-character id.
    public static bool TryParseId(string? link, out string id)
    {
        var match = IdPattern.Match(link?.Trim() ?? "");
        id = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }
}
