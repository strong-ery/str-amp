using Stramp.Core.Models;
using Stramp.Integrations.Windows;

namespace Stramp.App.Services;

/// <summary>
/// Owns the Discord Rich Presence connection and turns playback state into an activity payload.
/// Ships pointed at str-amp's own Discord application, so any user just flips the toggle on — a
/// client ID is a public app identifier, not a credential, so it's fine to share across every
/// install (the same way every game or app with Rich Presence works). Settings can override it
/// with a different application for local testing.
/// </summary>
public sealed class DiscordPresenceService : IDisposable
{
    public const string DefaultClientId = "1549171497908572170";

    // Must match an "Art Asset" key name uploaded to this client ID in the Discord Developer Portal
    // (discord.com/developers/applications -> your app -> Rich Presence -> Art Assets). Discord has
    // no API for pushing arbitrary local album art into a presence card, so this is a static logo.
    private const string LargeImageKey = "stramp_logo";
    private const string LargeImageText = "str-amp";

    private DiscordRichPresence? _client;
    private string? _runningClientId;

    /// <summary>Starts, restarts, or stops the client to match current settings. Cheap to call repeatedly.</summary>
    public void Configure(string? clientId, bool enabled)
    {
        var effectiveClientId = string.IsNullOrWhiteSpace(clientId) ? DefaultClientId : clientId;

        if (!enabled)
        {
            Shutdown();
            return;
        }

        if (_client is not null && _runningClientId == effectiveClientId)
            return;

        Shutdown();
        _client = new DiscordRichPresence(effectiveClientId);
        _client.Start();
        _runningClientId = effectiveClientId;
    }

    /// <summary>
    /// Publishes the current track while it's playing. Paused playback clears the activity so the
    /// status only shows while music is actually playing.
    /// </summary>
    public void UpdateNowPlaying(Song song, bool isPlaying, double positionSeconds, double durationSeconds)
    {
        if (_client is null)
            return;

        if (!isPlaying)
        {
            _client.ClearActivity();
            return;
        }

        var start = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(positionSeconds);
        var end = start + TimeSpan.FromSeconds(Math.Max(durationSeconds, positionSeconds));

        _client.SetActivity(new DiscordActivity(
            Details: song.Title,
            State: song.Artist,
            LargeImageKey: LargeImageKey,
            LargeImageText: LargeImageText,
            StartTimestamp: start,
            EndTimestamp: end));
    }

    public void Clear() => _client?.ClearActivity();

    private void Shutdown()
    {
        _client?.Dispose();
        _client = null;
        _runningClientId = null;
    }

    public void Dispose() => Shutdown();
}
