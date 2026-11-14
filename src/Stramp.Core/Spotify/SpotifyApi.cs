using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Stramp.Core.Spotify;

/// <summary>Reads the signed-in account's name and playlists from the Spotify Web API.</summary>
/// <remarks>
/// Fetches the playlist, then follows each page's <c>next</c> link until the list ends. Spotify only
/// returns the track list of playlists the signed-in user owns or collaborates on; for any other
/// playlist the list is missing or the request is refused, and both become a
/// <see cref="SpotifyException"/> that says so. An account the Spotify app does not allow is refused
/// on every request, which reading the account name reports. Recent API versions name the list <c>items</c> and
/// each entry's object <c>item</c>, older ones <c>tracks</c> and <c>track</c>; both are read.
/// </remarks>
public sealed class SpotifyApi(HttpClient http)
{
    private const string NotShared =
        "Spotify only shares the songs of playlists you own or collaborate on. Copy the songs to a playlist of your own and import that.";
    private const string NotAllowed =
        "This Spotify account isn't allowed to use Stramp yet. The Spotify app's owner has to add it in the Spotify developer dashboard.";

    // The display name, or the account id when the account has none.
    public async Task<string> GetAccountNameAsync(string accessToken, CancellationToken ct)
    {
        using var me = await GetJsonAsync("https://api.spotify.com/v1/me", accessToken, NotAllowed, ct);
        var name = Text(me.RootElement, "display_name");
        return name.Length > 0 ? name : Text(me.RootElement, "id");
    }

    public async Task<SpotifyPlaylist> GetPlaylistAsync(string playlistId, string accessToken, CancellationToken ct)
    {
        using var playlist = await GetJsonAsync(
            $"https://api.spotify.com/v1/playlists/{playlistId}?market=from_token", accessToken, NotShared, ct);
        var root = playlist.RootElement;
        var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        if (!TryGetEither(root, "items", "tracks", out var page) || page.ValueKind != JsonValueKind.Object)
            throw new SpotifyException(NotShared);

        var tracks = new List<SpotifyTrack>();
        var next = ReadPage(page, tracks);
        while (next is not null)
        {
            using var nextPage = await GetJsonAsync(next, accessToken, NotShared, ct);
            next = ReadPage(nextPage.RootElement, tracks);
        }

        return new SpotifyPlaylist(name, tracks);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string accessToken, string forbidden, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized)
            throw new SpotifyException("Spotify no longer accepts this sign-in. Sign out of Spotify and import again.");
        if (response.StatusCode is HttpStatusCode.Forbidden)
            throw new SpotifyException(forbidden);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            throw new SpotifyException("Spotify couldn't find that playlist. Check the link.");

        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    // Adds the page's tracks and returns the next page's URL, or null on the last page.
    private static string? ReadPage(JsonElement page, List<SpotifyTrack> tracks)
    {
        if (page.TryGetProperty("items", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray())
            {
                if (ReadTrack(entry) is { } track)
                    tracks.Add(track);
            }
        }

        return page.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
            ? next.GetString()
            : null;
    }

    private static SpotifyTrack? ReadTrack(JsonElement entry)
    {
        if (!TryGetEither(entry, "item", "track", out var track) || track.ValueKind != JsonValueKind.Object)
            return null;
        if (track.TryGetProperty("type", out var type) && type.GetString() != "track")
            return null;

        var name = Text(track, "name");
        if (name.Length == 0)
            return null;

        var artists = track.TryGetProperty("artists", out var artistList) && artistList.ValueKind == JsonValueKind.Array
            ? artistList.EnumerateArray().Select(a => Text(a, "name")).Where(a => a.Length > 0).ToList()
            : [];
        var album = track.TryGetProperty("album", out var albumElement) && albumElement.ValueKind == JsonValueKind.Object
            ? albumElement
            : default;

        return new SpotifyTrack(
            name,
            artists,
            album.ValueKind == JsonValueKind.Object ? Text(album, "name") : "",
            album.ValueKind == JsonValueKind.Object ? Text(album, "release_date") : "");
    }

    private static bool TryGetEither(JsonElement element, string current, string legacy, out JsonElement value) =>
        element.TryGetProperty(current, out value) || element.TryGetProperty(legacy, out value);

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
