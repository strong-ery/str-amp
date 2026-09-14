namespace Stramp.Core.Playback;

/// <summary>Loudness measurements used to derive a clipping-safe, constant playback gain.</summary>
public readonly record struct TrackLoudness(double IntegratedLufs, double TruePeakDbTp);

public enum AudioNormalizationLevel
{
    Quiet,
    Normal,
    Loud,
}

public static class LoudnessNormalization
{
    public const double TruePeakCeilingDbTp = -1;

    /// <summary>
    /// Returns one fixed gain for the entire track. The true-peak constraint can reduce a boost,
    /// but no limiter or dynamic compression is ever introduced.
    /// </summary>
    public static double CalculateGainDb(
        TrackLoudness loudness,
        AudioNormalizationLevel level = AudioNormalizationLevel.Normal)
    {
        if (!double.IsFinite(loudness.IntegratedLufs) || !double.IsFinite(loudness.TruePeakDbTp))
            return 0;

        var targetLufs = level switch
        {
            AudioNormalizationLevel.Quiet => -23,
            AudioNormalizationLevel.Loud => -9,
            _ => -14,
        };
        var loudnessGain = targetLufs - loudness.IntegratedLufs;
        var peakSafeGain = TruePeakCeilingDbTp - loudness.TruePeakDbTp;
        return Math.Clamp(Math.Min(loudnessGain, peakSafeGain), -60, 24);
    }
}
