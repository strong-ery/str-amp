using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stramp.Core.Lyrics;

/// <summary>What is being uploaded. Title, artist, album and length are the identity LRCLIB keys on.</summary>
public sealed record LrcLibSubmission(
    string TrackName,
    string ArtistName,
    string AlbumName,
    double Duration,
    string PlainLyrics,
    string SyncedLyrics);

public enum PublishOutcome
{
    Published,

    /// <summary>LRCLIB rejected the upload (bad fields, or the track already exists).</summary>
    Rejected,

    /// <summary>Could not reach LRCLIB.</summary>
    Failed,
}

public sealed record PublishResult(PublishOutcome Outcome, string Message);

/// <summary>
/// Uploads lyrics to lrclib.net. Publishing is gated by a small proof-of-work: ask for a challenge,
/// find a nonce whose SHA-256(prefix + nonce) is not above the target, and send both as the token.
/// </summary>
public sealed class LrcLibPublisher : IDisposable
{
    private const string BaseAddress = "https://lrclib.net/";
    private const string ClientId = "str-amp (https://github.com/strong-ery/str-amp)";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    public LrcLibPublisher(HttpClient? http = null)
    {
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-Client", ClientId);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientId);
    }

    public async Task<PublishResult> PublishAsync(LrcLibSubmission submission, CancellationToken ct = default)
    {
        try
        {
            using var challengeResponse = await _http.PostAsync("api/request-challenge", null, ct);
            if (!challengeResponse.IsSuccessStatusCode)
                return new PublishResult(PublishOutcome.Failed, "LRCLIB would not hand out a challenge. Try again later.");

            var challenge = await challengeResponse.Content.ReadFromJsonAsync<Challenge>(ct);
            if (challenge?.Prefix is null || challenge.Target is null)
                return new PublishResult(PublishOutcome.Failed, "LRCLIB sent an unreadable challenge.");

            var nonce = await Task.Run(() => Solve(challenge.Prefix, challenge.Target, ct), ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/publish")
            {
                Content = JsonContent.Create(new PublishBody(
                    submission.TrackName, submission.ArtistName, submission.AlbumName,
                    Math.Round(submission.Duration), submission.PlainLyrics, submission.SyncedLyrics)),
            };
            request.Headers.TryAddWithoutValidation("X-Publish-Token", $"{challenge.Prefix}:{nonce}");

            using var response = await _http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return new PublishResult(PublishOutcome.Published, "Published to lrclib.net.");

            return new PublishResult(PublishOutcome.Rejected, await ReadErrorAsync(response, ct));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PublishResult(PublishOutcome.Failed, $"Could not reach lrclib.net: {ex.Message}");
        }
    }

    /// <summary>Finds the first nonce whose hash is at or below the target, compared as big-endian bytes.</summary>
    internal static long Solve(string prefix, string targetHex, CancellationToken ct)
    {
        var target = Convert.FromHexString(targetHex);
        var prefixBytes = Encoding.UTF8.GetBytes(prefix);
        var input = new byte[prefixBytes.Length + 20];
        prefixBytes.CopyTo(input, 0);
        var hash = new byte[32];

        for (long nonce = 0; ; nonce++)
        {
            if ((nonce & 0xFFFF) == 0)
                ct.ThrowIfCancellationRequested();

            var digits = nonce.ToString(CultureInfo.InvariantCulture);
            var written = Encoding.ASCII.GetBytes(digits, 0, digits.Length, input, prefixBytes.Length);

            SHA256.HashData(input.AsSpan(0, prefixBytes.Length + written), hash);
            if (MeetsTarget(hash, target))
                return nonce;
        }
    }

    internal static bool MeetsTarget(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> target)
    {
        var length = Math.Min(hash.Length, target.Length);
        for (var i = 0; i < length; i++)
        {
            if (hash[i] < target[i]) return true;
            if (hash[i] > target[i]) return false;
        }
        return true;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } detail)
                return $"LRCLIB said: {detail}";
        }
        catch
        {
            // Not JSON: fall through to the status code.
        }
        return $"LRCLIB rejected the upload ({(int)response.StatusCode}).";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _http.Dispose();
    }

    private sealed record Challenge(
        [property: JsonPropertyName("prefix")] string? Prefix,
        [property: JsonPropertyName("target")] string? Target);

    private sealed record PublishBody(
        [property: JsonPropertyName("trackName")] string TrackName,
        [property: JsonPropertyName("artistName")] string ArtistName,
        [property: JsonPropertyName("albumName")] string AlbumName,
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("plainLyrics")] string PlainLyrics,
        [property: JsonPropertyName("syncedLyrics")] string SyncedLyrics);
}
