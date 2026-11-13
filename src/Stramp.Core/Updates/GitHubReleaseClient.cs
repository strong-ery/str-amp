using System.Net;
using System.Text.Json;

namespace Stramp.Core.Updates;

/// <summary>Looks up a GitHub repo's latest published release.</summary>
public sealed class GitHubReleaseClient : IDisposable
{
    private const string BaseAddress = "https://api.github.com/";

    /// <summary>GitHub rejects anonymous requests with no User-Agent at all.</summary>
    private const string ClientId = "str-amp (https://github.com/strong-ery/str-amp)";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    /// <param name="http">Supply a client to test against a stub; otherwise one is made here.</param>
    public GitHubReleaseClient(HttpClient? http = null)
    {
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = TimeSpan.FromSeconds(12),
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ClientId);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
    }

    /// <summary>
    /// The repo's newest published, non-draft, non-prerelease release, or null when it has none
    /// (a brand new repo, or every release so far is a draft/prerelease).
    /// </summary>
    public async Task<GitHubRelease?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"repos/{owner}/{repo}/releases/latest", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, ct);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsHttpClient)
            _http.Dispose();
    }
}
