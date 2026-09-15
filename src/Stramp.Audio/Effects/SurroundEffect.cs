/*
 * Derived from FxSound -- dsp/ptechDsp/wide/Wide32/Wide32.c
 *
 *   FxSound
 *   Copyright (C) 2025  FxSound LLC
 *   Contributors: www.theremino.com (2025)
 *   Original author: Paul F. Titchener (Wide32.c, 1999)
 *
 *   FxSound is free software: you can redistribute it and/or modify it under
 *   the terms of the GNU Affero General Public License as published by the Free
 *   Software Foundation, either version 3 of the License, or (at your option)
 *   any later version. See <http://www.gnu.org/licenses/>.
 *
 * Ported to C# for str-amp, which is AGPL-3.0-or-later for this reason.
 * See COPYRIGHT in the repository root.
 */

using NAudio.Dsp;

namespace Stramp.Audio.Effects;

/// <summary>
/// Stereo widening, on the mid/side decomposition: what both speakers share stays in the middle,
/// what differs between them is amplified.
///
///   mid = (L+R)/2,  side = L - mid  (which is (L-R)/2)
///   L' = mid·centre + side·width,   R' = mid·centre - side·width
///
/// Two things here are FxSound's, and they are what makes it read as wide rather than merely wider.
/// The side gain runs to <b>4x</b>, not the 2x that looks like a safe amount on paper. And the
/// middle is pulled <i>down</i> as the sides come up: raising the sides alone leaves the centre as
/// loud as it was, so the image stretches without ever really opening.
///
/// FxSound applies this across the whole spectrum. str-amp does not, and the reason is measurable:
/// widening the band a lead vocal lives in lifts the reverb already printed on that vocal — which
/// is decorrelated, and therefore side — by about 6 dB relative to the dry voice, which is centred.
/// The singer audibly steps backwards into her own reverb. See <see cref="VocalFloorHz"/>.
/// </summary>
internal sealed class SurroundEffect
{
    /// <summary>Side gain at full intensity is 1 + this. From Wide32.c's gainFactorSide.</summary>
    private const float SideRange = 3.0f;

    /// <summary>
    /// How far the middle is pulled down at full intensity, making room for the sides.
    /// Wide32.c's gainFactorCompensation, whose comment is "this decreases also the mono signal
    /// so live it to 0.3". Applied only to the part of the signal actually being widened.
    /// </summary>
    private const float CentreDuck = 0.3f;

    /// <summary>
    /// Widening applies above this only; below it, both mid and side pass through untouched.
    ///
    /// This is a deliberate departure from FxSound, which widens everything. A lead vocal and its
    /// reverb occupy the same frequencies, but not the same place in the image: the voice is
    /// centred and the reverb is not. Widening there raises the reverb and lowers the voice at the
    /// same time, and on a sparse, vocal-led mix the result is a singer standing further back in a
    /// bigger room. Above this frequency the same processing reads as air rather than distance.
    ///
    /// Set to 0 to restore FxSound's full-band behaviour exactly.
    /// </summary>
    private const float VocalFloorHz = 3000;

    private readonly SmoothedParameter _intensity;
    private readonly Crossover? _mid;
    private readonly Crossover? _side;

    public SurroundEffect(int sampleRate)
    {
        _intensity = new SmoothedParameter(sampleRate);

        if (VocalFloorHz > 0)
        {
            _mid = new Crossover(sampleRate, VocalFloorHz);
            _side = new Crossover(sampleRate, VocalFloorHz);
        }
    }

    /// <summary>True once the width has settled back to none, so the stage can be skipped.</summary>
    public bool IsIdle => _intensity.IsSettled && _intensity.Current == 0;

    /// <summary>
    /// Sets the amount, 0 to 10. FxSound's DSP takes this as an intensity of 0 to 1; the parameter
    /// is smoothed on the way in, which the original does not do — it reloads parameters between
    /// buffers, where str-amp's sliders can move within one.
    /// </summary>
    public void SetAmount(double amount) => _intensity.SetTarget((float)amount / 10);

    public void Reset()
    {
        _intensity.SnapTo(_intensity.Current);
        _mid?.Reset();
        _side?.Reset();
    }

    /// <summary>
    /// Runs only on stereo; there is no side signal otherwise, and applying this to a surround
    /// layout channel-pairwise would scramble it rather than widen it.
    /// </summary>
    public static bool SupportsLayout(int channels) => channels == 2;

    /// <summary>
    /// Linkwitz-Riley split, whose halves sum back to a flat magnitude.
    ///
    /// The obvious shortcut — take a highpass and call the remainder "everything else" — does not
    /// work: a Butterworth highpass shifts phase, so subtracting it from the original partially
    /// cancels rather than cleanly separating. The first cut of this made the effect *narrower*
    /// the further it was turned up, measured at 0.48 down to 0.41 side-to-mid.
    /// </summary>
    private sealed class Crossover(int sampleRate, float crossoverHz)
    {
        private readonly BiQuadFilter[] _low =
        [
            BiQuadFilter.LowPassFilter(sampleRate, crossoverHz, 0.7071f),
            BiQuadFilter.LowPassFilter(sampleRate, crossoverHz, 0.7071f),
        ];

        private readonly BiQuadFilter[] _high =
        [
            BiQuadFilter.HighPassFilter(sampleRate, crossoverHz, 0.7071f),
            BiQuadFilter.HighPassFilter(sampleRate, crossoverHz, 0.7071f),
        ];

        public void Reset()
        {
            foreach (var f in _low) f.ResetState();
            foreach (var f in _high) f.ResetState();
        }

        public (float Low, float High) Split(float input) =>
            (_low[1].Transform(_low[0].Transform(input)),
             _high[1].Transform(_high[0].Transform(input)));
    }

    public void Process(Span<float> buffer, int count)
    {
        for (var frame = 0; frame + 2 <= count; frame += 2)
        {
            var intensity = _intensity.Next();
            var width = 1 + SideRange * intensity;
            var centre = 1 - CentreDuck * intensity;

            var left = buffer[frame];
            var right = buffer[frame + 1];

            var mid = (left + right) * 0.5f;
            var side = (left - mid);

            if (_mid is null || _side is null)
            {
                // FxSound's behaviour: the whole spectrum is widened.
                buffer[frame] = mid * centre + width * side;
                buffer[frame + 1] = mid * centre - width * side;
                continue;
            }

            var (midLow, midHigh) = _mid.Split(mid);
            var (sideLow, sideHigh) = _side.Split(side);

            var widenedMid = midLow + midHigh * centre;
            var widenedSide = sideLow + sideHigh * width;

            buffer[frame] = widenedMid + widenedSide;
            buffer[frame + 1] = widenedMid - widenedSide;
        }
    }
}
