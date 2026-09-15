/*
 * Derived from FxSound:
 *   dsp/ptechDsp/Lex/Lex32/Lex32.c   (algorithm)
 *   dsp/ptutil/include/c_lex.h       (delay lengths, tap positions, parameter ranges)
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
/// Plate reverb of the Dattorro/Griesinger kind — the "Lex" in FxSound's source, after the Lexicon
/// units the topology comes from.
///
/// The input is pre-delayed, rolled off by a one-pole (the "bandwidth" control), then smeared by
/// four allpass diffusers in series. What comes out is injected into a <i>tank</i>: two chains of
/// allpass-then-delay wired in a figure of eight, each feeding the other's input. The tail never
/// repeats because the two loops have different lengths and neither is a whole multiple of the
/// other, and it darkens as it decays because a one-pole sits in each loop (the "damping"
/// control). Nineteen taps are drawn from points inside both chains and summed into left and right
/// with fixed signs — that, rather than any explicit stereo processing, is what makes the tail
/// wide: each output hears a different set of points in the same room.
///
/// FxSound builds this with PT_DSP_BUILD=PT_DSP_DFX, which compiles out the delay modulation that
/// the DSP/FX product used, so the tank here is deliberately unmodulated.
///
/// The one structural departure: the original keeps every stage in a single shared buffer with a
/// pointer that walks through it, advancing by each stage's length in turn. That saves memory on a
/// DSP card with very little. Here each stage owns its own delay line, which is the same filter
/// with the same lengths, taps and coefficients, just laid out legibly.
/// </summary>
internal sealed class AmbienceEffect
{
    // Input diffuser lengths, in seconds. These are scaled by sample rate only — the room size
    // does not apply to them in the original.
    private const double Lat1Seconds = 4.77 / 1000.0;
    private const double Lat2Seconds = 3.595 / 1000.0;
    private const double Lat3Seconds = 12.73 / 1000.0;
    private const double Lat4Seconds = 9.31 / 1000.0;

    // Tank lengths and tap positions, in seconds, scaled by both sample rate and room size.
    private const double Lat5Seconds = 22.6 / 1000.0;
    private const double D1Tap1 = 10.1 / 1000.0, D1Tap2 = 66.9 / 1000.0;
    private const double D1Tap3 = 121.9 / 1000.0, D1Length = 149.6 / 1000.0;
    private const double Lat6Tap1 = 6.28 / 1000.0, Lat6Tap2 = 41.26 / 1000.0;
    private const double Lat6Length = 60.5 / 1000.0;
    private const double D2Tap1 = 35.8 / 1000.0, D2Tap2 = 89.8 / 1000.0, D2Length = 125.0 / 1000.0;

    private const double Lat7Seconds = 30.5 / 1000.0;
    private const double D3Tap1 = 10.1 / 1000.0, D3Tap2 = 70.9 / 1000.0;
    private const double D3Tap3 = 99.9 / 1000.0, D3Length = 141.7 / 1000.0;
    private const double Lat8Tap1 = 11.25 / 1000.0, Lat8Tap2 = 64.3 / 1000.0;
    private const double Lat8Length = 89.2 / 1000.0;
    private const double D4Tap1 = 4.065 / 1000.0, D4Tap2 = 67.1 / 1000.0, D4Length = 106.3 / 1000.0;

    // Tuning, all from the original's initialisation.
    private const float Decay = 0.565664f;
    private const float RoomSize = 1.0f;
    private const float Lat1Coeff = 0.75f;
    private const float Lat3Coeff = 0.625f;
    private const float Lat5Coeff = 0.70f;
    private const float Lat6Coeff = 0.5f;
    private const float Damping = 0.5f;
    private const float Bandwidth = 0.5f;
    private const int PreDelaySamples = 1;

    /// <summary>Output trim the original applies to both taps sums: 0.6 * 0.5.</summary>
    private const float OutputTrim = 0.6f * 0.5f;

    /// <summary>Keeps a decaying tail out of denormal arithmetic. The original's DSP_DENORM_BIAS.</summary>
    private const float DenormalBias = 1.0e-20f;

    private readonly DelayLine _preDelay;
    private readonly DelayLine _lat1, _lat2, _lat3, _lat4;
    private readonly DelayLine _lat5, _d1, _lat6, _d2;
    private readonly DelayLine _lat7, _d3, _lat8, _d4;

    private readonly int _d1Tap1, _d1Tap2, _d1Tap3;
    private readonly int _lat6Tap1, _lat6Tap2;
    private readonly int _d2Tap1, _d2Tap2;
    private readonly int _d3Tap1, _d3Tap2, _d3Tap3;
    private readonly int _lat8Tap1, _lat8Tap2;
    private readonly int _d4Tap1, _d4Tap2;

    private readonly SmoothedParameter _wet;

    private float _bandwidthState;
    private float _dampingState1;
    private float _dampingState2;
    private float _tankFeedback;      // D4's tail, feeding the first chain
    private float _crossFeedback;     // D2's tail, feeding the second

    public AmbienceEffect(int sampleRate, int channels)
    {
        int Fixed(double seconds) => Math.Max(1, (int)(seconds * sampleRate));
        int Scaled(double seconds) => Math.Max(1, (int)(seconds * RoomSize * sampleRate));

        _preDelay = new DelayLine(PreDelaySamples + 1);
        _lat1 = new DelayLine(Fixed(Lat1Seconds));
        _lat2 = new DelayLine(Fixed(Lat2Seconds));
        _lat3 = new DelayLine(Fixed(Lat3Seconds));
        _lat4 = new DelayLine(Fixed(Lat4Seconds));

        _lat5 = new DelayLine(Scaled(Lat5Seconds));
        _d1 = new DelayLine(Scaled(D1Length));
        _lat6 = new DelayLine(Scaled(Lat6Length));
        _d2 = new DelayLine(Scaled(D2Length));

        _lat7 = new DelayLine(Scaled(Lat7Seconds));
        _d3 = new DelayLine(Scaled(D3Length));
        _lat8 = new DelayLine(Scaled(Lat8Length));
        _d4 = new DelayLine(Scaled(D4Length));

        _d1Tap1 = Scaled(D1Tap1); _d1Tap2 = Scaled(D1Tap2); _d1Tap3 = Scaled(D1Tap3);
        _lat6Tap1 = Scaled(Lat6Tap1); _lat6Tap2 = Scaled(Lat6Tap2);
        _d2Tap1 = Scaled(D2Tap1); _d2Tap2 = Scaled(D2Tap2);
        _d3Tap1 = Scaled(D3Tap1); _d3Tap2 = Scaled(D3Tap2); _d3Tap3 = Scaled(D3Tap3);
        _lat8Tap1 = Scaled(Lat8Tap1); _lat8Tap2 = Scaled(Lat8Tap2);
        _d4Tap1 = Scaled(D4Tap1); _d4Tap2 = Scaled(D4Tap2);

        _wet = new SmoothedParameter(sampleRate);
    }

    /// <summary>True once the tail has been faded fully out, so the stage can be skipped.</summary>
    public bool IsIdle => _wet.IsSettled && _wet.Current == 0;

    /// <summary>
    /// Sets the amount, 0 to 10. The reverb itself runs at FxSound's fixed tuning; the control is
    /// the wet gain into the mix.
    ///
    /// The wet gain's own scale is the inferred part: the original takes it from the host through
    /// kerWetDry, and that mapping lives outside the open-sourced DSP project. Treating the knob as
    /// the wet gain directly puts the bundled presets between 0 and 0.71, which against the fixed
    /// 0.3 output trim below lands them as a room rather than a hall.
    /// </summary>
    public void SetAmount(double amount) => _wet.SetTarget((float)Math.Clamp(amount / 10, 0, 1));

    public void Reset()
    {
        foreach (var line in new[] { _preDelay, _lat1, _lat2, _lat3, _lat4, _lat5, _d1, _lat6, _d2, _lat7, _d3, _lat8, _d4 })
            line.Clear();
        _bandwidthState = 0;
        _dampingState1 = 0;
        _dampingState2 = 0;
        _tankFeedback = 0;
        _crossFeedback = 0;
    }

    public void Process(Span<float> buffer, int count, int channels)
    {
        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            var wet = _wet.Next();

            // The tank is mono in, stereo out: the two outputs differ because they tap different
            // points of the same room, not because two rooms are run.
            var input = channels >= 2
                ? (buffer[frame] + buffer[frame + 1]) * 0.5f
                : buffer[frame];
            input += DenormalBias;

            var preDelayed = _preDelay.Process(input, PreDelaySamples);

            // Input bandwidth: a one-pole rolling off what enters the tank.
            _bandwidthState = preDelayed * (1 - Bandwidth) + Bandwidth * _bandwidthState;
            var diffused = _bandwidthState;

            diffused = Diffuse(_lat1, diffused, Lat1Coeff);
            diffused = Diffuse(_lat2, diffused, Lat1Coeff);
            diffused = Diffuse(_lat3, diffused, Lat3Coeff);
            diffused = Diffuse(_lat4, diffused, Lat3Coeff);

            // ---- first chain, fed by the second chain's tail --------------------------------
            var node = diffused + Decay * _tankFeedback;

            // The tank's leading allpass uses the opposite sign convention to the diffusers.
            node = DiffuseInverted(_lat5, node, Lat5Coeff);

            var d1Tap1 = _d1.Read(_d1Tap1);
            var d1Tap2 = _d1.Read(_d1Tap2);
            var d1Tap3 = _d1.Read(_d1Tap3);
            var d1Out = _d1.Process(node);

            var left = -d1Tap2;
            var right = d1Tap1 + d1Tap3;

            _dampingState1 = d1Out * (1 - Damping) + Damping * _dampingState1;
            node = _dampingState1 * Decay;

            var lat6Tap1 = _lat6.Read(_lat6Tap1);
            var lat6Tap2 = _lat6.Read(_lat6Tap2);
            node = Diffuse(_lat6, node, Lat6Coeff);
            left -= lat6Tap1;
            right -= lat6Tap2;

            var d2Tap1 = _d2.Read(_d2Tap1);
            var d2Tap2 = _d2.Read(_d2Tap2);
            var d2Out = _d2.Process(node);
            left -= d2Tap1;
            right += d2Tap2;

            // ---- second chain, fed by the first chain's tail --------------------------------
            node = diffused + Decay * _crossFeedback;
            node = DiffuseInverted(_lat7, node, Lat5Coeff);

            var d3Tap1 = _d3.Read(_d3Tap1);
            var d3Tap2 = _d3.Read(_d3Tap2);
            var d3Tap3 = _d3.Read(_d3Tap3);
            var d3Out = _d3.Process(node);

            left += d3Tap1 + d3Tap3;
            right -= d3Tap2;

            _dampingState2 = d3Out * (1 - Damping) + Damping * _dampingState2;
            node = _dampingState2 * Decay;

            var lat8Tap1 = _lat8.Read(_lat8Tap1);
            var lat8Tap2 = _lat8.Read(_lat8Tap2);
            node = Diffuse(_lat8, node, Lat6Coeff);
            left -= lat8Tap2;
            right -= lat8Tap1;

            var d4Tap1 = _d4.Read(_d4Tap1);
            var d4Tap2 = _d4.Read(_d4Tap2);
            var d4Out = _d4.Process(node);
            left += d4Tap2;
            right -= d4Tap1;

            // Each chain's tail is the other's input on the next sample; that cross-coupling is
            // what makes one tank out of two loops instead of two separate reverbs.
            _crossFeedback = d2Out;
            _tankFeedback = d4Out;

            left *= OutputTrim;
            right *= OutputTrim;

            if (!float.IsFinite(left) || !float.IsFinite(right))
            {
                Reset();
                continue;
            }

            buffer[frame] += wet * left;
            if (channels >= 2)
                buffer[frame + 1] += wet * right;
        }
    }

    /// <summary>Lattice allpass as the input diffusers and the trailing tank stages use it.</summary>
    private static float Diffuse(DelayLine line, float input, float coefficient)
    {
        var delayed = line.Peek();
        var stored = input - coefficient * delayed;
        line.Process(stored);
        return delayed + coefficient * stored;
    }

    /// <summary>
    /// The same lattice with both signs flipped, as the tank's leading allpass uses. The original
    /// writes it as dl_in = x + k·dl_out, out = dl_out − k·dl_in, and the difference is not
    /// cosmetic: it inverts the stage's contribution to the loop.
    /// </summary>
    private static float DiffuseInverted(DelayLine line, float input, float coefficient)
    {
        var delayed = line.Peek();
        var stored = input + coefficient * delayed;
        line.Process(stored);
        return delayed - coefficient * stored;
    }

    /// <summary>A circular delay, readable at any offset up to its length.</summary>
    private sealed class DelayLine(int length)
    {
        private readonly float[] _buffer = new float[Math.Max(length, 1)];
        private int _position;

        public void Clear()
        {
            Array.Clear(_buffer);
            _position = 0;
        }

        /// <summary>The oldest sample, without advancing.</summary>
        public float Peek() => _buffer[_position];

        /// <summary>The sample <paramref name="offset"/> ago, without advancing.</summary>
        public float Read(int offset)
        {
            var index = _position - offset;
            if (index < 0)
                index += _buffer.Length;
            return _buffer[Math.Clamp(index, 0, _buffer.Length - 1)];
        }

        /// <summary>Writes a sample and returns the one it displaced.</summary>
        public float Process(float input)
        {
            var output = _buffer[_position];
            _buffer[_position] = input;
            if (++_position >= _buffer.Length)
                _position = 0;
            return output;
        }

        /// <summary>Writes a sample and returns the one <paramref name="offset"/> ago.</summary>
        public float Process(float input, int offset)
        {
            var output = Read(offset);
            _buffer[_position] = input;
            if (++_position >= _buffer.Length)
                _position = 0;
            return output;
        }
    }
}
