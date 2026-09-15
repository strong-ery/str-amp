using NAudio.Wave;
using Stramp.Core.Playback;

namespace Stramp.Audio.Effects;

/// <summary>
/// The enhancement stages, run in order, with the peak limiter on the end.
///
/// The order is not arbitrary. Bass boost and clarity are tone shaping, so they come first and the
/// later stages see the tone the listener actually gets. Ambience then puts that in a room, and
/// surround widens the room along with it — widening before the reverb would leave the reverb
/// sitting narrower than the track inside it. Dynamic boost runs second to last so it levels the
/// finished signal rather than a partial one, and the limiter is last because everything above it
/// can add level and only the limiter is allowed the final say on how much leaves.
///
/// Every stage is skipped outright while its amount is zero, so an untouched chain costs one pass
/// for the limiter and nothing else.
/// </summary>
internal sealed class AudioEffectChain : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BassBoostEffect _bassBoost;
    private readonly ClarityEffect _clarity;
    private readonly AmbienceEffect _ambience;
    private readonly SurroundEffect? _surround;
    private readonly DynamicBoostEffect _dynamicBoost;
    private readonly PeakLimiter _limiter;

    private readonly object _gate = new();
    private AudioEffectSettings? _pending;
    private bool _resetRequested;
    private bool _ambienceWasIdle = true;

    public WaveFormat WaveFormat => _source.WaveFormat;

    public AudioEffectChain(ISampleProvider source, AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        _source = source;
        var sampleRate = WaveFormat.SampleRate;
        var channels = WaveFormat.Channels;

        _bassBoost = new BassBoostEffect(sampleRate, channels);
        _clarity = new ClarityEffect(sampleRate, channels);
        _ambience = new AmbienceEffect(sampleRate, channels);
        _surround = SurroundEffect.SupportsLayout(channels) ? new SurroundEffect(sampleRate) : null;
        _dynamicBoost = new DynamicBoostEffect(sampleRate);
        _limiter = new PeakLimiter(sampleRate);

        Apply(settings.Clamped());
    }

    /// <summary>Stages new amounts; the audio thread picks them up on its next pass.</summary>
    public void Update(AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
            _pending = settings.Clamped();
    }

    /// <summary>Flushes every delay line and follower. For seeks and track changes.</summary>
    public void Reset()
    {
        lock (_gate)
            _resetRequested = true;
    }

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);

        AudioEffectSettings? pending;
        bool reset;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            reset = _resetRequested;
            _resetRequested = false;
        }

        if (pending is not null)
            Apply(pending);

        if (reset)
            ResetState();

        var channels = WaveFormat.Channels;

        if (!_bassBoost.IsIdle)
            _bassBoost.Process(buffer, read, channels);

        if (!_clarity.IsIdle)
            _clarity.Process(buffer, read, channels);

        if (!_ambience.IsIdle)
        {
            // Coming back from silent, the delay lines still hold whatever was in them when the
            // effect was switched off, which would otherwise arrive as a burst of stale room.
            if (_ambienceWasIdle)
                _ambience.Reset();
            _ambience.Process(buffer, read, channels);
        }
        _ambienceWasIdle = _ambience.IsIdle;

        if (_surround is not null && !_surround.IsIdle)
            _surround.Process(buffer, read);

        if (!_dynamicBoost.IsIdle)
            _dynamicBoost.Process(buffer, read, channels);

        // Always runs: the stages above are not the only thing that can push the signal over, and
        // an untouched chain still needs the guarantee that nothing leaves above full scale.
        if (_limiter.Process(buffer, read, channels))
            ResetState();

        return read;
    }

    private void Apply(AudioEffectSettings settings)
    {
        _bassBoost.SetAmount(settings.BassBoost);
        _clarity.SetAmount(settings.Clarity);
        _ambience.SetAmount(settings.Ambience);
        _surround?.SetAmount(settings.Surround);
        _dynamicBoost.SetAmount(settings.DynamicBoost);
    }

    private void ResetState()
    {
        _bassBoost.Reset();
        _clarity.Reset();
        _ambience.Reset();
        _surround?.Reset();
        _dynamicBoost.Reset();
        _limiter.Reset();
    }
}
