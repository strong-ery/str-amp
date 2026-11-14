using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stramp.Core.Settings;

namespace Stramp.Core.Spotify;

/// <summary>Signs the user in to Spotify and keeps an access token for the Web API.</summary>
/// <remarks>
/// Uses the authorization code flow with PKCE, which needs only the client id of the user's own Spotify
/// app, read from settings at each request: the browser opens Spotify's sign-in page and returns to a
/// <see cref="SpotifyLoopbackReceiver"/>. Tokens are saved to spotify-auth.json beside the settings, so
/// later imports refresh silently and only sign in again once Spotify rejects the refresh token. That
/// app must list <see cref="SpotifyLoopbackReceiver.RedirectUri"/> as a redirect URI, and in development
/// mode only its owner and the accounts on its allowlist can sign in.
/// </remarks>
public sealed class SpotifyAuthorizer(HttpClient http, Func<string?> clientId, Action<Uri> openBrowser)
{
    private const string Scopes = "playlist-read-private playlist-read-collaborative";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly string TokenPath = Path.Combine(SettingsService.ConfigDirectory, "spotify-auth.json");

    public bool IsSignedIn => File.Exists(TokenPath);

    public void SignOut() => File.Delete(TokenPath);

    public async Task<string> GetAccessTokenAsync(CancellationToken ct) =>
        await GetSavedAccessTokenAsync(ct) ?? await SignInAsync(ct);

    // The saved token, refreshed if it has expired; null when signing in again is the only way.
    public async Task<string?> GetSavedAccessTokenAsync(CancellationToken ct)
    {
        if (LoadTokens() is not { } saved)
            return null;
        if (saved.ExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            return saved.AccessToken;

        var refreshed = await RequestTokensAsync(RefreshForm(saved.RefreshToken), saved.RefreshToken, ct);
        return refreshed?.AccessToken;
    }

    // Always opens the browser, replacing any saved sign-in; returns the new access token.
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        using var receiver = new SpotifyLoopbackReceiver();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);

        var id = ClientId();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        openBrowser(new Uri("https://accounts.spotify.com/authorize?" + Query(
            ("client_id", id), ("response_type", "code"), ("redirect_uri", SpotifyLoopbackReceiver.RedirectUri),
            ("scope", Scopes), ("state", state), ("code_challenge_method", "S256"), ("code_challenge", challenge))));

        string code;
        try
        {
            code = await receiver.ReceiveCodeAsync(state, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SpotifyException("Spotify sign-in timed out. Try again.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = SpotifyLoopbackReceiver.RedirectUri,
            ["client_id"] = id, ["code_verifier"] = verifier,
        };
        var tokens = await RequestTokensAsync(form, null, ct)
            ?? throw new SpotifyException("Spotify did not accept the sign-in. Make sure this account is allowed to use the app.");
        return tokens.AccessToken;
    }

    // Null when Spotify rejects the grant, which for a refresh means signing in again.
    private async Task<StoredTokens?> RequestTokensAsync(
        Dictionary<string, string> form, string? previousRefreshToken, CancellationToken ct)
    {
        using var response = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), ct);
        if ((int)response.StatusCode is >= 400 and < 500)
        {
            if (previousRefreshToken is not null)
                File.Delete(TokenPath);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var reply = await response.Content.ReadFromJsonAsync<TokenReply>(ct)
            ?? throw new SpotifyException("Spotify sent an empty sign-in reply.");

        // A refresh may omit the refresh token, in which case the previous one stays valid.
        var tokens = new StoredTokens(
            reply.AccessToken,
            reply.RefreshToken ?? previousRefreshToken ?? "",
            DateTime.UtcNow.AddSeconds(reply.ExpiresIn));
        Directory.CreateDirectory(SettingsService.ConfigDirectory);
        await File.WriteAllTextAsync(TokenPath, JsonSerializer.Serialize(tokens), ct);
        return tokens;
    }

    private Dictionary<string, string> RefreshForm(string refreshToken) => new()
    {
        ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = ClientId(),
    };

    private string ClientId() => clientId()?.Trim() is { Length: > 0 } id
        ? id
        : throw new SpotifyException("Add your Spotify Client ID in Settings first.");

    private static StoredTokens? LoadTokens()
    {
        try
        {
            return File.Exists(TokenPath) ? JsonSerializer.Deserialize<StoredTokens>(File.ReadAllText(TokenPath)) : null;
        }
        catch (JsonException)
        {
            // An unreadable token file only costs a fresh sign-in.
            return null;
        }
    }

    private static string Query(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record StoredTokens(string AccessToken, string RefreshToken, DateTime ExpiresUtc);

    private sealed record TokenReply(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
