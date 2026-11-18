using System.Text.Json;
using Stramp.Core.Settings;

namespace Stramp.Core.Playback;

/// <summary>Loads and atomically saves playback state separately from user preferences.</summary>
public sealed class PlaybackStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _statePath;

    public PlaybackStateStore(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(SettingsService.ConfigDirectory, "state.json");
    }

    public string StatePath => _statePath;

    public SavedPlaybackState? Load()
    {
        try
        {
            if (!File.Exists(_statePath))
                return null;

            using var stream = File.OpenRead(_statePath);
            var state = JsonSerializer.Deserialize<SavedPlaybackState>(stream);
            return state is { Version: 1 } ? state : null;
        }
        catch
        {
            // A partial/corrupt snapshot must never prevent the player from launching.
            return null;
        }
    }

    public void Save(SavedPlaybackState state)
    {
        var directory = Path.GetDirectoryName(_statePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("Playback state path must have a parent directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = _statePath + ".tmp";
        try
        {
            using (var stream = File.Create(temporaryPath))
                JsonSerializer.Serialize(stream, state, JsonOptions);
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
