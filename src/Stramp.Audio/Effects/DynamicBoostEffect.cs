/*
 * Derived from FxSound:
 *   dsp/ptechDsp/Maximizer/Maxi32/Maxi32.c   (algorithm)
 *   dsp/ptutil/include/c_max.h               (constants and parameter ranges)
 *
 *   FxSound
 *   Copyright (C) 2025  FxSound LLC
 *   Original author: Paul F. Titchener
 *
 *   FxSound is free software: you can redistribute it and/or modify it under
 *   the terms of the GNU Affero General Public License as published by the Free
 *   Software Foundation, either version 3 of the License, or (at your option)
 *   any later version. See <http://www.gnu.org/licenses/>.
 *
 * Ported to C# for str-amp, which is AGPL-3.0-or-later for this reason.
 * See COPYRIGHT in the repository root.
 */

namespace Stramp.Audio.Effects;

/// <summary>
/// Loudness maximizer — FxSound labels this "increases overall volume and balance with responsive
/// processing", and both halves of that are literal.
///
/// <b>Volume</b> is a boost of up to 30 dB, but governed: a very slow level estimator (a one-pole
/// at 0.1 Hz, so it measures the track rather than the moment) is compared against a target, and
/// the boost is cut back to whatever lands the track on that target. Quiet material gets the full
/// boost, already-loud material gets almost none. That is the "balance": it levels tracks against
/// each other rather than simply making everything louder.
///
/// <b>Responsive</b> is the lookahead. The signal is delayed by 0.75 ms while the envelope
/// follower watches the input that has not been heard yet; when a peak is coming, the envelope
/// ramps linearly up to meet it, so the gain is already down by the time the peak arrives. A
/// limiter without lookahead has to either catch the peak late (and let it through) or clamp
/// instantly (and distort). This one does neither.
///
/// The level estimate is taken from the left channel alone and each channel keeps its own envelope,
/// both as in the original.
/// </summary>
internal sealed class DynamicBoostEffect
{
    /// <summary>Boost range in dB the control spans. c_max.h's DSP_MAXIMIZE_GAIN_BOOST_*.</summary>
    private const double MaxGainBoostDb = 30.0;

    /// <summary>Ceiling the lookahead limiter holds the output to; 0.966051 is about -0.3 dBFS.</summary>
    private const float MaxOutput = 0.966051f;

    /// <summary>
    /// Long-term level the boost aims the track at.
    ///
    /// Not FxSound's 0.32. That constant is explicitly a placeholder — their comment reads "in DFX
    /// this is currently set in the function dfxp_CommunicateFixedQnts_Opt(), so this is an
    /// initialization below that is overwritten by DFX", and that function is not in the
    /// open-sourced project. The placeholder is about -9.9 dBFS, which is *below* where a modern
    /// master already sits, so every governed boost collapsed onto the 1.06 floor and the stage
    /// measured +0.1 dB on real tracks. Their own comment anticipates this: "really hot songs have
    /// a level estimate that exceeds that".
    ///
    /// Raised so the stage does something on contemporary material. This is the one number here
    /// not taken from FxSound, and it is the first thing to change if the loudness does not match.
    /// </summary>
    private const float TargetLevel = 0.5f;

    /// <summary>Cutoff of the level estimator, in Hz. Deliberately far below audio.</summary>
    private const double LevelFilterCutoffHz = 0.1;

    /// <summary>Lookahead, in seconds. MAXI_LOOK_AHEAD_DELAY.</summary>
    private const double LookAheadSeconds = 0.00075;

    /// <summary>Upper bound on the lookahead in samples, as the original reserves.</summary>
    private const int MaxLookAheadSamples = 96;

    /// <summary>Floor on the governed boost, so it never turns into an attenuator.</summary>
    private const float MinGovernedBoost = 1.06f;

    /// <summary>Keeps the envelope from decaying into denormals. MAXI_ENVELOPE_BIAS.</summary>
    private const float EnvelopeBias = 1.0e-24f;

    /// <summary>Release time constant, from the original's beta of 0.997776 at 44.1 kHz.</summary>
    private const double ReleaseSeconds = 0.0102;

    private readonly int _channels;
    private readonly int _lookAhead;
    private readonly float _levelPole;
    private readonly float _levelGain;
    private readonly float _releaseBeta;

    private readonly float[][] _delay;
    private readonly int[] _position;
    private readonly float[] _envelope;
    private readonly float[] _maxAbs;
    private readonly float[] _delta;
    private readonly int[] _rampCount;

    private readonly SmoothedParameter _boost;
    private float _level;

    public DynamicBoostEffect(int sampleRate, int channels)
    {
        _channels = channels;
        _lookAhead = Math.Clamp((int)(LookAheadSeconds * sampleRate), 1, MaxLookAheadSamples);

        // The original's level filter design, verbatim: a one-pole placed by solving for the pole
        // that puts the cutoff at 0.1 Hz. At that frequency the naive 1 - exp(-w) form loses all
        // its precision, which is why it is written this way.
        var omega = 6.283185 * LevelFilterCutoffHz / sampleRate;
        var cosOmega = Math.Cos(omega);
        var pole = 2.0 - cosOmega - Math.Sqrt(cosOmega * cosOmega - 4.0 * cosOmega + 3.0);
        _levelPole = (float)pole;
        _levelGain = (float)(1.0 - pole);

        _releaseBeta = (float)Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));

        _delay = new float[channels][];
        for (var channel = 0; channel < channels; channel++)
            _delay[channel] = new float[_lookAhead];

        _position = new int[channels];
        _envelope = new float[channels];
        _maxAbs = new float[channels];
        _delta = new float[channels];
        _rampCount = new int[channels];

        _boost = new SmoothedParameter(sampleRate, 1);
    }

    /// <summary>True once the boost is back at unity, so the stage can be skipped.</summary>
    public bool IsIdle => _boost.IsSettled && _boost.Current <= 1;

    /// <summary>
    /// Sets the amount, 0 to 10. The 0-to-1 knob maps linearly onto 0 to 30 dB of boost, which the
    /// target level below then governs. At zero the stage is bypassed outright rather than left
    /// running at unity — the original relies on a separate per-effect on/off button for that, and
    /// its governed boost has a floor of +0.5 dB that would otherwise always be in circuit.
    /// </summary>
    public void SetAmount(double amount)
    {
        var knob = Math.Clamp(amount / 10, 0, 1);
        _boost.SetTarget(knob <= 0 ? 1 : (float)Math.Pow(10, knob * MaxGainBoostDb / 20));
    }

    public void Reset()
    {
        foreach (var line in _delay)
            Array.Clear(line);
        Array.Clear(_position);
        Array.Clear(_envelope);
        Array.Clear(_maxAbs);
        Array.Clear(_delta);
        Array.Clear(_rampCount);
        _level = 0;
    }

    public void Process(Span<float> buffer, int count, int channels)
    {
        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            var setting = _boost.Next();

            // Level estimate tracks the left channel only, as the original does.
            var reference = buffer[frame];
            _level = _level * _levelPole + reference * reference * _levelGain;
            var rms = MathF.Sqrt(_level);

            // Take the requested boost, unless it would carry the track past the target; then take
            // only what reaches the target, and never less than the floor.
            float boost;
            if (rms > 0 && setting * rms > TargetLevel)
                boost = MathF.Max(TargetLevel / rms, MinGovernedBoost);
            else
                boost = setting;

            for (var channel = 0; channel < channels && channel < _channels; channel++)
            {
                var index = frame + channel;
                var line = _delay[channel];
                var position = _position[channel];

                var delayed = line[position];
                line[position] = boost * MaxOutput * buffer[index];
                var arriving = MathF.Abs(line[position]);

                if (++position >= line.Length)
                    position = 0;
                _position[channel] = position;

                var envelope = _envelope[channel];

                if (_rampCount[channel] != 0)
                {
                    // Mid-ramp toward a peak that has not arrived yet. A larger one appearing
                    // restarts the ramp at the steeper slope of the two.
                    var outgoing = MathF.Abs(delayed);
                    if (outgoing > envelope)
                        envelope = outgoing;

                    if (arriving > _maxAbs[channel])
                    {
                        _maxAbs[channel] = arriving;
                        _rampCount[channel] = _lookAhead;
                        var slope = (arriving - envelope) / (_lookAhead + 1);
                        if (slope > _delta[channel])
                            _delta[channel] = slope;
                    }
                    else
                    {
                        _rampCount[channel]--;
                    }

                    envelope += _delta[channel];
                }
                else
                {
                    envelope = envelope * _releaseBeta + EnvelopeBias;

                    var outgoing = MathF.Abs(delayed);
                    if (outgoing > envelope)
                        envelope = outgoing;

                    if (arriving > envelope)
                    {
                        _maxAbs[channel] = arriving;
                        _delta[channel] = (arriving - envelope) / (_lookAhead + 1);
                        envelope += _delta[channel];
                        _rampCount[channel] = _lookAhead;
                    }
                }

                _envelope[channel] = envelope;

                buffer[index] = envelope > MaxOutput
                    ? delayed * MaxOutput / envelope
                    : delayed;
            }
        }
    }
}
