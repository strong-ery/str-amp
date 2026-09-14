using NAudio.Dsp;

namespace Stramp.Audio.Effects;

/// <summary>
/// Low-end lift, as a low-shelf filter in the direct path.
///
/// The obvious alternative — keep the signal untouched and add a low-passed copy on top — does not
/// work, and measurably so. A lowpass steep enough to stay out of the low mids has swung a full
/// 180° by its corner frequency, so the copy arrives inverted and subtracts instead of adding: the
/// first cut of this effect boosted 30 Hz by 11 dB and cut 100 Hz by 6 dB. A shelf has no such
/// problem because there is no second path to disagree with.
///
/// The cost is that changing the gain means changing filter coefficients, and
/// <see cref="BiQuadFilter"/> clears its delay line when it does, which clicks. So the gain change
/// is crossfaded between two filters, and a change arriving mid-crossfade waits rather than
/// restarting one — the same treatment the equalizer's bands get.
/// </summary>
internal sealed class BassBoostEffect
{
    /// <summary>Shelf corner. Low enough that the lift reads as weight rather than boxiness.</summary>
    private const float CornerHz = 120;

    /// <summary>Shelf slope. 1.0 is as steep as this shape goes without overshooting.</summary>
    private const float Slope = 0.9f;

    /// <summary>Boost at the bottom when the amount is at 10.</summary>
    private const float MaxBoostDb = 12;

    /// <summary>Length of the crossfade that retunes the shelf after the amount changes.</summary>
    private const float RetuneSeconds = 0.02f;

    private const float GainEpsilon = 1e-4f;

    private readonly CrossfadingBiQuadFilter[] _shelves;
    private readonly int _sampleRate;
    private readonly int _crossfadeFrames;

    private float _activeGainDb;
    private float _targetGainDb;
    private int _tailFrames;

    public BassBoostEffect(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _crossfadeFrames = Math.Max(1, (int)(RetuneSeconds * sampleRate));

        _shelves = new CrossfadingBiQuadFilter[channels];
        for (var channel = 0; channel < channels; channel++)
            _shelves[channel] = new CrossfadingBiQuadFilter(Build(0), Build(0), _crossfadeFrames);
    }

    /// <summary>True once the shelf is flat and settled, so the stage can be skipped.</summary>
    public bool IsIdle => _targetGainDb == 0 && _activeGainDb == 0 && _tailFrames <= 0;

    /// <summary>Sets the amount, 0 to 10 (and a little beyond).</summary>
    public void SetAmount(double amount) => _targetGainDb = (float)amount / 10 * MaxBoostDb;

    public void Reset()
    {
        foreach (var shelf in _shelves)
            shelf.Reset();
        _tailFrames = 0;
    }

    public void Process(Span<float> buffer, int count, int channels)
    {
        Retune();

        for (var frame = 0; frame + channels <= count; frame += channels)
            for (var channel = 0; channel < channels && channel < _shelves.Length; channel++)
                buffer[frame + channel] = _shelves[channel].Transform(buffer[frame + channel]);

        if (_tailFrames > 0)
            _tailFrames = Math.Max(0, _tailFrames - count / Math.Max(channels, 1));
    }

    private void Retune()
    {
        // Wait for a crossfade in flight: restarting one snaps the response back to where it
        // started, and a dragged slider sends changes far faster than 20 ms apart.
        if (_tailFrames > 0 || MathF.Abs(_targetGainDb - _activeGainDb) < GainEpsilon)
            return;

        foreach (var shelf in _shelves)
        {
            // A shelf has no in-place setter, so the standby filter is replaced wholesale.
            shelf.ReplaceStandby(Build(_targetGainDb));
            shelf.BeginCrossfade();
        }

        _activeGainDb = _targetGainDb;
        _tailFrames = _crossfadeFrames;
    }

    private BiQuadFilter Build(float gainDb) =>
        BiQuadFilter.LowShelf(_sampleRate, CornerHz, Slope, gainDb);
}
