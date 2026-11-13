using System.Text.Json;

namespace Stramp.Core.Updates;

/// <summary>Checks GitHub for a release newer than the running build.</summary>
public sealed class UpdateChecker(GitHubReleaseClient? client = null) : IDisposable
{
    private const string Owner = "strong-ery";
    private const string Repo = "str-amp";

    private readonly GitHubReleaseClient _client = client ?? new GitHubReleaseClient();

    /// <summary>
    /// Null when already up to date, the release has no downloadable asset, or the check itself
    /// failed (no network, GitHub unreachable) — any of which should leave the user unbothered
    /// rather than surfaced as an error.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        GitHubRelease? release;
        try
        {
            release = await _client.GetLatestReleaseAsync(Owner, Repo, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }

        if (release is null || release.Draft || release.Prerelease || !AppVersion.IsNewerThanCurrent(release.TagName))
            return null;

        var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        if (asset is null)
            return null;

        return new UpdateInfo(release.TagName.TrimStart('v', 'V'), release.Body, release.HtmlUrl, asset.BrowserDownloadUrl);
    }

    public void Dispose() => _client.Dispose();
}
