using System.Net;
using System.Text.Json;
using Stramp.Core.Models;

namespace Stramp.Core.Lyrics;

/// <summary>
/// Looks tracks up on lrclib.net. The exact-signature endpoint is tried first, and a title/artist
/// search only runs when that misses — most often because the local file's duration or album tag
/// disagrees with what was uploaded.
/// </summary>
public sealed class LrcLibClient : IDisposable
{
    private const string BaseAddress = "https://lrclib.net/";

    /// <summary>LRCLIB asks clients to identify themselves so it can contact abusive callers.</summary>
    private const string ClientId = "str-amp (https://github.com/strong-ery/str-amp)";

    /// <summary>How far a search candidate's length may sit from the local file's and still match.</summary>
    private const double DurationToleranceSeconds = 4;

    /// <summary>The range LRCLIB will accept a duration in; anything else is a validation error.</summary>
    private const double MinIndexedSeconds = 1;
    private const double MaxIndexedSeconds = 3600;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <param name="http">Supply a client to test against a stub; otherwise one is made here.</param>
    public LrcLibClient(HttpClient? http = null)
    {
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(12),
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("Lrclib-Client", ClientId);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientId);
    }

    /// <summary>
    /// Returns the best match for a track, or null when LRCLIB has nothing for it. Network and
    /// server failures are raised, so the caller can tell "no such track" from "could not ask".
    /// </summary>
    public async Task<LrcLibTrack?> FindAsync(Song song, CancellationToken ct = default)
    {
        var exact = await GetExactAsync(song, ct);
        if (exact is not null)
            return exact;

        var candidates = await SearchAsync(song, ct);
        return ChooseBest(candidates, song.Duration.TotalSeconds);
    }

    /// <summary>The signature lookup: an all-fields match against what LRCLIB has on file.</summary>
    private async Task<LrcLibTrack?> GetExactAsync(Song song, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("artist_name", song.Artist)
            .Add("track_name", song.Title)
            .Add("album_name", song.Album)
            .Add("duration", RoundedDuration(song));

        using var response = await _http.GetAsync($"api/get?{query}", ct);

        // Any 4xx means this signature isn't a match and the search should have its turn: 404 for
        // the routine case where a tag differs slightly, 400 for a track LRCLIB will not index at
        // all, such as one over an hour long. Only a server-side failure is worth raising.
        if ((int)response.StatusCode is >= 400 and < 500)
            return null;

        response.EnsureSuccessStatusCode();
        var track = await ReadAsync<LrcLibTrack>(response, ct);
        return track is not null && HasSomethingToShow(track) ? track : null;
    }

    private async Task<IReadOnlyList<LrcLibTrack>> SearchAsync(Song song, CancellationToken ct)
    {
        var query = new QueryBuilder()
            .Add("track_name", song.Title)
            .Add("artist_name", song.Artist);

        using var response = await _http.GetAsync($"api/search?{query}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        return await ReadAsync<List<LrcLibTrack>>(response, ct) ?? [];
    }

    /// <summary>
    /// Ranks search hits: a timestamped upload beats a plain one, and among those the closest
    /// length wins. A candidate whose length is far off is a different recording, so it is dropped.
    /// </summary>
    internal static LrcLibTrack? ChooseBest(IReadOnlyList<LrcLibTrack> candidates, double durationSeconds)
    {
        var usable = candidates.Where(HasSomethingToShow);

        // Duration is zero when the tags gave us nothing to compare against.
        if (durationSeconds > 0)
        {
            usable = usable
                .Where(track => track.Duration <= 0
                    || Math.Abs(track.Duration - durationSeconds) <= DurationToleranceSeconds)
                .OrderByDescending(track => track.HasSynced)
                .ThenBy(track => track.Duration > 0
                    ? Math.Abs(track.Duration - durationSeconds)
                    : double.MaxValue);
        }
        else
        {
            usable = usable.OrderByDescending(track => track.HasSynced);
        }

        return usable.FirstOrDefault();
    }

    /// <summary>An instrumental counts: "this track has no words" is an answer worth caching.</summary>
    private static bool HasSomethingToShow(LrcLibTrack track) =>
        track.Instrumental || track.HasSynced || track.HasPlain;

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    /// <summary>
    /// The length as LRCLIB wants it: whole seconds, which is also all a container's tags are good
    /// for, and omitted entirely when it falls outside the range the service will index.
    /// </summary>
    private static string RoundedDuration(Song song)
    {
        var seconds = Math.Round(song.Duration.TotalSeconds);
        return seconds is >= MinIndexedSeconds and <= MaxIndexedSeconds
            ? ((int)seconds).ToString()
            : "";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsHttpClient)
            _http.Dispose();
    }

    /// <summary>Collects non-empty, escaped query parameters.</summary>
    private sealed class QueryBuilder
    {
        private readonly List<string> _parts = [];

        public QueryBuilder Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                _parts.Add($"{name}={Uri.EscapeDataString(value.Trim())}");
            return this;
        }

        public override string ToString() => string.Join('&', _parts);
    }
}
