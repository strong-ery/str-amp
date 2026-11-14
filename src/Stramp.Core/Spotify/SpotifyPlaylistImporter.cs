using Stramp.Core.Models;

namespace Stramp.Core.Spotify;

/// <summary>The outcome of importing a Spotify playlist: the local songs it matched.</summary>
/// <remarks>
/// <see cref="Songs"/> keeps the playlist's order and never contains a song twice, so it can be shorter
/// than <see cref="TotalTracks"/> even when every track matched.
/// </remarks>
public sealed record SpotifyImportResult(string PlaylistName, IReadOnlyList<Song> Songs, int TotalTracks);

/// <summary>Turns a Spotify playlist link into the matching songs from the local library.</summary>
/// <remarks>
/// Signs in through <see cref="SpotifyAuthorizer"/>, either on request or when an import finds no usable
/// saved sign-in, reads the playlist through <see cref="SpotifyApi"/>, and matches each track with
/// <see cref="SpotifyTrackMatcher"/>. Problems the user can fix arrive as <see cref="SpotifyException"/>;
/// network failures as <see cref="HttpRequestException"/>. One instance is meant to live for the app.
/// </remarks>
public sealed class SpotifyPlaylistImporter
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly SpotifyAuthorizer _authorizer;
    private readonly SpotifyApi _api;

    public SpotifyPlaylistImporter(Func<string?> clientId, Action<Uri> openBrowser)
    {
        _authorizer = new SpotifyAuthorizer(_http, clientId, openBrowser);
        _api = new SpotifyApi(_http);
    }

    public bool IsSignedIn => _authorizer.IsSignedIn;

    public void SignOut() => _authorizer.SignOut();

    // Opens the browser to sign in and returns the account's name. Reading the name also confirms
    // Spotify allows the account; one it refuses is signed straight back out.
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        var token = await _authorizer.SignInAsync(ct);
        try
        {
            return await _api.GetAccountNameAsync(token, ct);
        }
        catch (SpotifyException)
        {
            _authorizer.SignOut();
            throw;
        }
    }

    // The saved account's name without ever opening the browser; null when there is no usable sign-in.
    public async Task<string?> GetSavedAccountNameAsync(CancellationToken ct) =>
        await _authorizer.GetSavedAccessTokenAsync(ct) is { } token
            ? await _api.GetAccountNameAsync(token, ct)
            : null;

    public async Task<SpotifyImportResult> ImportAsync(
        string link, string? nameOverride, IReadOnlyList<Song> library, CancellationToken ct)
    {
        if (!SpotifyPlaylist.TryParseId(link, out var playlistId))
            throw new SpotifyException("That doesn't look like a Spotify playlist link.");

        var token = await _authorizer.GetAccessTokenAsync(ct);
        var playlist = await _api.GetPlaylistAsync(playlistId, token, ct);

        var matcher = new SpotifyTrackMatcher(library);
        var songs = playlist.Tracks.Select(matcher.Match).OfType<Song>().Distinct().ToList();

        var name = !string.IsNullOrWhiteSpace(nameOverride) ? nameOverride.Trim()
            : !string.IsNullOrWhiteSpace(playlist.Name) ? playlist.Name.Trim()
            : $"Spotify Playlist {playlistId[..6]}";
        return new SpotifyImportResult(name, songs, playlist.Tracks.Count);
    }
}
