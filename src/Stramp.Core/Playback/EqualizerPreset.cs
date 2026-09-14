namespace Stramp.Core.Playback;

/// <summary>
/// A named equalizer curve, stored as the frequency/gain points it was authored at rather than as
/// gains for one particular set of sliders. The presets came from FXSound, where every preset
/// carries its own band frequencies, so almost none of them line up with ours — and pinning the
/// numbers to today's band list would silently misplace them if that list ever changed.
/// <see cref="GainsFor"/> resamples the curve onto whichever bands the player actually exposes.
/// </summary>
/// <param name="Name">Display name.</param>
/// <param name="Points">Curve points, ascending by frequency.</param>
/// <param name="Effects">Amounts for the five enhancement effects that sit after the equalizer.</param>
public sealed record EqualizerPreset(
    string Name,
    IReadOnlyList<EqualizerPresetPoint> Points,
    AudioEffectSettings? Effects = null)
{
    /// <summary>The effect amounts this preset asks for; all off when it names none.</summary>
    public AudioEffectSettings EffectAmounts => Effects ?? AudioEffectSettings.None;

    /// <summary>
    /// The curve sampled at each of <paramref name="bandFrequencies"/>, interpolated linearly in
    /// log frequency — the axis the bands are spaced on and the one the curve was drawn against.
    /// Frequencies outside the curve hold its end value rather than extrapolating off it.
    /// </summary>
    public double[] GainsFor(IReadOnlyList<float> bandFrequencies)
    {
        ArgumentNullException.ThrowIfNull(bandFrequencies);

        var gains = new double[bandFrequencies.Count];
        if (Points.Count == 0)
            return gains;

        for (var band = 0; band < gains.Length; band++)
            gains[band] = GainAt(bandFrequencies[band]);
        return gains;
    }

    private double GainAt(float frequency)
    {
        if (Points.Count == 1 || frequency <= Points[0].Hz)
            return Points[0].GainDb;
        if (frequency >= Points[^1].Hz)
            return Points[^1].GainDb;

        for (var i = 0; i < Points.Count - 1; i++)
        {
            var low = Points[i];
            var high = Points[i + 1];
            if (frequency > high.Hz)
                continue;

            var span = Math.Log2(high.Hz / low.Hz);
            if (span <= 0)
                return high.GainDb;

            var position = Math.Log2(frequency / low.Hz) / span;
            return low.GainDb + position * (high.GainDb - low.GainDb);
        }

        return Points[^1].GainDb;
    }
}

/// <summary>One point on an <see cref="EqualizerPreset"/> curve.</summary>
/// <param name="Hz">Centre frequency the gain was authored at.</param>
/// <param name="GainDb">Gain in decibels.</param>
public readonly record struct EqualizerPresetPoint(float Hz, double GainDb);
