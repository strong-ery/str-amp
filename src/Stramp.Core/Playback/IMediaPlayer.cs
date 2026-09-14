namespace Stramp.Core.Playback;

/// <summary>Platform-agnostic playback surface. Implemented per-backend in Stramp.Audio.</summary>
public interface IMediaPlayer : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>Playback position in seconds.</summary>
    double TimePosition { get; }

    /// <summary>Volume, 0-100.</summary>
    double Volume { get; set; }

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

    /// <summary>Center frequencies (Hz) of the equalizer bands, empty if the backend has no EQ.</summary>
    IReadOnlyList<float> EqualizerBands { get; }

    /// <summary>Applies per-band gains in dB (-20..+20). Passing enabled:false bypasses the EQ.</summary>
    void ApplyEqualizer(IReadOnlyList<double> gainsDb, bool enabled);
}
