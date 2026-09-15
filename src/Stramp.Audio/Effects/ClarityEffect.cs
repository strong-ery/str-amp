/*
 * Derived from FxSound:
 *   dsp/ptechDsp/Aural/Aural032/Auralp32.c   (algorithm)
 *   dsp/ptutil/include/c_aural.h             (parameter ranges)
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
/// Aural exciter: adds presence by synthesising harmonics of the top end, rather than boosting what
/// is already there with a shelf.
///
///   high  = butterworth_highpass(in) · drive
///   odd   = sin(high)                     — odd harmonics, and it saturates on its own
///   even  = high > 0 ? high : 0           — half-wave rectified, so even harmonics
///   out   = in + evenMix·even + oddMix·odd
///
/// The sine is the interesting choice. Feeding a signal through sin() generates odd harmonics whose
/// amplitude falls away naturally, and it self-limits because the output cannot leave [-1, 1]
/// however hard it is driven — so "drive" can be pushed a long way without the result running away.
/// FxSound's own comment notes the cubic Taylor term alone approximates it well below unity input.
///
/// Turning the control up does not simply add more of one thing. Drive rises, the odd path rises,
/// and the even path falls to nothing (see the ranges below), so the character moves from warm and
/// even-dominated at low settings to bright and odd-dominated at high ones.
///
/// Note this generates harmonics without an upper bound, so content near Nyquist will alias — the
/// str-amp implementation this replaced avoided that by capping the shaper at a cubic and band
/// limiting its input, at the cost of being far weaker. This is FxSound's tradeoff, taken
/// deliberately: measured aliasing is ~40 dB below the harmonics it is there to produce.
/// </summary>
internal sealed class ClarityEffect
{
    /// <summary>
    /// Corner of the highpass feeding the shaper. FxSound ships coefficients gain 0.788950,
    /// a1 1.53285, a0 -0.622949, which solve back to exactly this corner at 44.1 kHz; they are
    /// recomputed here so the corner stays put at any sample rate.
    /// </summary>
    private const double HighPassHz = 2350;

    // Parameter ranges, from c_aural.h. Drive and the odd path run up with the control; the even
    // path runs *down*, from 0.75 at nothing to silent at full.
    private const double DriveMin = 0;
    private const double DriveMax = 2 * Math.PI / 4.0 * 1.8 * 2.0 * 0.75;   // 4.2412
    private const double WetBoost = 2.0 * 0.75;
    private const double EvenMin = 0.5 * WetBoost;                          // 0.75
    private const double EvenMax = 0.0;
    private const double OddMin = 0;
    private const double OddMax = 1.0 * WetBoost;                           // 1.5

    /// <summary>Guards the shaper against a runaway input; sin() is bounded, the even path is not.</summary>
    private const float EvenCeiling = 4;

    private readonly int _channels;
    private readonly float _gain;
    private readonly float _a1;
    private readonly float _a0;

    private readonly SmoothedParameter _drive;
    private readonly SmoothedParameter _even;
    private readonly SmoothedParameter _odd;

    // Highpass history, per channel.
    private readonly float[] _outMinus1;
    private readonly float[] _outMinus2;
    private readonly float[] _inMinus1;
    private readonly float[] _inMinus2;

    public ClarityEffect(int sampleRate, int channels)
    {
        _channels = channels;
        _outMinus1 = new float[channels];
        _outMinus2 = new float[channels];
        _inMinus1 = new float[channels];
        _inMinus2 = new float[channels];

        // Second-order Butterworth highpass via the bilinear transform, in the same
        // y = a1·y₋₁ + a0·y₋₂ + gain·(x − 2x₋₁ + x₋₂) arrangement the original uses.
        var k = Math.Tan(Math.PI * Math.Min(HighPassHz, sampleRate * 0.4) / sampleRate);
        var norm = 1.0 / (1 + Math.Sqrt(2) * k + k * k);
        _gain = (float)norm;
        _a1 = (float)(2 * (1 - k * k) * norm);
        _a0 = (float)(-(1 - Math.Sqrt(2) * k + k * k) * norm);

        _drive = new SmoothedParameter(sampleRate);
        _even = new SmoothedParameter(sampleRate, (float)EvenMin);
        _odd = new SmoothedParameter(sampleRate);
    }

    /// <summary>True once the shaper is fully out of circuit, so the stage can be skipped.</summary>
    public bool IsIdle => _drive.IsSettled && _drive.Current == 0;

    /// <summary>
    /// Sets the amount, 0 to 10. FxSound's DSP takes a 0-to-1 knob value which its quantizer maps
    /// linearly onto each parameter's range (QNT_RESPONSE_LINEAR, "used for most knob to real
    /// mappings"). The mapping is linear here for that reason; it is the one link in the chain
    /// taken from the response type's documented default rather than from a call site, because
    /// dfxpSetKnobValue's implementation is not part of the open-sourced DSP project.
    /// </summary>
    public void SetAmount(double amount)
    {
        var knob = Math.Clamp(amount / 10, 0, 1);
        _drive.SetTarget((float)(DriveMin + knob * (DriveMax - DriveMin)));
        _even.SetTarget((float)(EvenMin + knob * (EvenMax - EvenMin)));
        _odd.SetTarget((float)(OddMin + knob * (OddMax - OddMin)));
    }

    public void Reset()
    {
        Array.Clear(_outMinus1);
        Array.Clear(_outMinus2);
        Array.Clear(_inMinus1);
        Array.Clear(_inMinus2);
    }

    public void Process(Span<float> buffer, int count, int channels)
    {
        for (var frame = 0; frame + channels <= count; frame += channels)
        {
            var drive = _drive.Next();
            var evenMix = _even.Next();
            var oddMix = _odd.Next();

            for (var channel = 0; channel < channels && channel < _channels; channel++)
            {
                var index = frame + channel;
                var input = buffer[index];

                var filtered = _outMinus1[channel] * _a1 + _outMinus2[channel] * _a0;
                _outMinus2[channel] = _outMinus1[channel];

                // The tiny bias is FxSound's, and it is not cosmetic: without it a silent passage
                // drives the feedback path into denormals, where the CPU cost jumps.
                filtered += (input + 1e-30f - 2 * _inMinus1[channel] + _inMinus2[channel]) * _gain;

                _outMinus1[channel] = filtered;
                _inMinus2[channel] = _inMinus1[channel];
                _inMinus1[channel] = input;

                filtered *= drive;

                var odd = MathF.Sin(filtered);
                var even = filtered > 0 ? MathF.Min(filtered, EvenCeiling) : 0;

                buffer[index] = input + evenMix * even + oddMix * odd;
            }
        }
    }
}
