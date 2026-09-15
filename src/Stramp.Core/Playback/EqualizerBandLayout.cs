namespace Stramp.Core.Playback;

/// <summary>
/// Rules for where equalizer band centres may sit. The bands have to stay in ascending order: the
/// filter bank sizes each band's Q from the gap to its neighbours, and the UI draws them
/// left-to-right, so a band that overtakes its neighbour would make the number under a slider stop
/// describing the band that slider drives.
/// </summary>
public static class EqualizerBandLayout
{
    /// <summary>Lowest centre frequency a band may be placed at.</summary>
    public const double MinFrequencyHz = 20;

    /// <summary>Highest centre frequency a band may be placed at.</summary>
    public const double MaxFrequencyHz = 20000;

    /// <summary>
    /// Smallest ratio kept between neighbouring centres. Deliberately tiny — some presets really do
    /// place two bands about a sixth of an octave apart — but non-zero, so no two bands coincide.
    /// </summary>
    public const double MinSpacingRatio = 1.01;

    /// <summary>
    /// The value <paramref name="requested"/> may actually take for the band at
    /// <paramref name="index"/>, held inside the overall range and between whichever neighbours
    /// that band has. Only the one band moves: an edit is never allowed to displace the others.
    /// </summary>
    public static double Constrain(IReadOnlyList<double> frequencies, int index, double requested)
    {
        ArgumentNullException.ThrowIfNull(frequencies);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, frequencies.Count);

        if (!double.IsFinite(requested))
            requested = MinFrequencyHz;

        var lowest = index > 0
            ? Math.Max(MinFrequencyHz, frequencies[index - 1] * MinSpacingRatio)
            : MinFrequencyHz;
        var highest = index < frequencies.Count - 1
            ? Math.Min(MaxFrequencyHz, frequencies[index + 1] / MinSpacingRatio)
            : MaxFrequencyHz;

        // Neighbours crowded tighter than the minimum spacing leave no room at all; rather than
        // invert the range, pin the band to the bottom of it.
        return highest < lowest ? lowest : Math.Clamp(requested, lowest, highest);
    }
}
