using Stramp.Core.Models;

namespace Stramp.Core.Playback;

/// <summary>
/// OS-level "now playing" integration seam (Windows SMTC, Linux MPRIS2, ...).
/// Implementations live in Stramp.Integrations.* and are picked at startup by platform.
/// A no-op implementation is valid and expected where no integration exists yet.
/// </summary>
public interface INowPlayingIntegration
{
    /// <summary>Raised when the OS/desktop asks us to toggle play/pause.</summary>
    event Action? PlayPauseRequested;

    /// <summary>Raised when the OS/desktop asks us to skip to the next track.</summary>
    event Action? NextRequested;

    /// <summary>Raised when the OS/desktop asks us to go to the previous track.</summary>
    event Action? PreviousRequested;

    /// <summary>Raised when the OS/desktop asks us to change volume (0-100).</summary>
    event Action<double>? VolumeRequested;

    void Start();
    void Update(Song song, bool paused, double volume);
    void Clear();
}
