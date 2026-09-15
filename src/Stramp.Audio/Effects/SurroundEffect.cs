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
/// It is also deliberately full-band. Keeping bass out of the widening is the textbook advice, and
/// it is what str-amp's own version did, but it costs most of the effect — a lot of the shared
/// content lives low, so protecting it holds the image closed.
/// </summary>
internal sealed class SurroundEffect
{
    /// <summary>Side gain at full intensity is 1 + this. From Wide32.c's gainFactorSide.</summary>
    private const float SideRange = 3.0f;

    /// <summary>
    /// How far the middle is pulled down at full intensity, making room for the sides.
    /// Wide32.c's gainFactorCompensation, whose comment is "this decreases also the mono signal
    /// so live it to 0.3".
    /// </summary>
    private const float CentreDuck = 0.3f;

    private readonly SmoothedParameter _intensity;

    public SurroundEffect(int sampleRate) => _intensity = new SmoothedParameter(sampleRate);

    /// <summary>True once the width has settled back to none, so the stage can be skipped.</summary>
    public bool IsIdle => _intensity.IsSettled && _intensity.Current == 0;

    /// <summary>
    /// Sets the amount, 0 to 10. FxSound's DSP takes this as an intensity of 0 to 1; the parameter
    /// is smoothed on the way in, which the original does not do — it reloads parameters between
    /// buffers, where str-amp's sliders can move within one.
    /// </summary>
    public void SetAmount(double amount) => _intensity.SetTarget((float)amount / 10);

    public void Reset() => _intensity.SnapTo(_intensity.Current);

    /// <summary>
    /// Runs only on stereo; there is no side signal otherwise, and applying this to a surround
    /// layout channel-pairwise would scramble it rather than widen it.
    /// </summary>
    public static bool SupportsLayout(int channels) => channels == 2;

    public void Process(Span<float> buffer, int count)
    {
        for (var frame = 0; frame + 2 <= count; frame += 2)
        {
            var intensity = _intensity.Next();
            var width = 1 + SideRange * intensity;
            var centre = 1 - CentreDuck * intensity;

            var left = buffer[frame];
            var right = buffer[frame + 1];

            var mono = (left + right) * 0.5f;
            var leftSide = left - mono;
            var rightSide = right - mono;

            mono *= centre;
            buffer[frame] = mono + width * leftSide;
            buffer[frame + 1] = mono + width * rightSide;
        }
    }
}
