using LibVLCSharp.Shared;
using Stramp.Core.Playback;

namespace Stramp.Audio;

/// <summary>IMediaPlayer implementation backed by libvlc (via LibVLCSharp). Works on Windows and Linux.</summary>
public sealed class LibVlcMediaPlayer : IMediaPlayer
{
    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private Media? _currentMedia;

    public event Action<double>? TimePositionChanged;
    public event Action? PlaybackEnded;

    public bool IsPlaying { get; private set; }

    public double TimePosition => _player.Time / 1000.0;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = (int)Math.Clamp(value, 0, 100);
    }

    static LibVlcMediaPlayer()
    {
        LibVLCSharp.Shared.Core.Initialize();
    }

    public LibVlcMediaPlayer()
    {
        _libVlc = new LibVLC(enableDebugLogs: false);
        _player = new MediaPlayer(_libVlc);

        _player.TimeChanged += (_, e) => TimePositionChanged?.Invoke(e.Time / 1000.0);
        _player.EndReached += (_, _) =>
        {
            IsPlaying = false;
            PlaybackEnded?.Invoke();
        };
    }

    public void Play(string path)
    {
        _currentMedia?.Dispose();
        _currentMedia = new Media(_libVlc, path, FromType.FromPath);
        _player.Play(_currentMedia);
        IsPlaying = true;
    }

    public void Pause()
    {
        _player.SetPause(true);
        IsPlaying = false;
    }

    public void Resume()
    {
        _player.SetPause(false);
        IsPlaying = true;
    }

    public bool TogglePause()
    {
        if (IsPlaying) Pause();
        else Resume();
        return IsPlaying;
    }

    public void Seek(double positionSeconds) => _player.Time = (long)(positionSeconds * 1000);

    public IReadOnlyList<float> EqualizerBands { get; } = ReadBandFrequencies();

    private static float[] ReadBandFrequencies()
    {
        try
        {
            // Band count/frequencies are instance members, so probe with a throwaway equalizer.
            using var probe = new Equalizer();
            var count = probe.BandCount;
            var bands = new float[count];
            for (uint i = 0; i < count; i++)
                bands[i] = probe.BandFrequency(i);
            return bands;
        }
        catch
        {
            return [];
        }
    }

    public void ApplyEqualizer(IReadOnlyList<double> gainsDb, bool enabled)
    {
        try
        {
            if (!enabled)
            {
                _player.UnsetEqualizer();
                return;
            }

            using var equalizer = new Equalizer();
            var count = Math.Min(gainsDb.Count, EqualizerBands.Count);
            for (var i = 0; i < count; i++)
                equalizer.SetAmp((float)Math.Clamp(gainsDb[i], -20, 20), (uint)i);

            _player.SetEqualizer(equalizer);
        }
        catch
        {
            // EQ is a nicety — never let it take playback down with it.
        }
    }

    public void Dispose()
    {
        _player.Dispose();
        _currentMedia?.Dispose();
        _libVlc.Dispose();
    }
}
