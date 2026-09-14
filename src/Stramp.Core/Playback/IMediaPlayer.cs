namespace Stramp.Core.Playback;

/// <summary>Platform-agnostic playback surface. Implemented per-backend in Stramp.Audio.</summary>
public interface IMediaPlayer : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>Playback position in seconds.</summary>
    double TimePosition { get; }

    /// <summary>Volume, 0-100.</summary>
    double Volume { get; set; }

    /// <summary>Constant per-track loudness gain in dB. This is separate from user volume.</summary>
    double NormalizationGainDb { get; set; }

    /// <summary>Fires with the current time position (seconds) as playback progresses.</summary>
    event Action<double>? TimePositionChanged;

    /// <summary>Fires when the current track finishes playing on its own (not via manual stop).</summary>
    event Action? PlaybackEnded;

    void Play(string path);
    void Pause();
    void Resume();

    /// <summary>Toggles play/pause. Returns true if now playing.</summary>
    bool TogglePause();

    /// <summary>Seeks to an absolute position in seconds.</summary>
    void Seek(double positionSeconds);

    /// <summary>
    /// Center frequencies (Hz) the equalizer starts out with, empty if the backend has no EQ. The
    /// band count is fixed by this list; the centres themselves are the caller's to change.
    /// </summary>
    IReadOnlyList<float> DefaultEqualizerBands { get; }

    /// <summary>
    /// Applies the equalizer: where each band sits (Hz) and its gain in dB (-20..+20). Pass an
    /// empty band list to keep the backend's defaults, or enabled:false to bypass the EQ.
    /// </summary>
    void ApplyEqualizer(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled);
}
