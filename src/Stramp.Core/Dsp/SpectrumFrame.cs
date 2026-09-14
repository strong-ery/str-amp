namespace Stramp.Core.Dsp;

/// <summary>One analyzed window of audio: log-spaced bins for display, plus band energies for beat detection.</summary>
public sealed class SpectrumFrame
{
    public required float[] DisplayBins { get; init; }

    /// <summary>Average magnitude in the ~40-150Hz range — kicks/bass.</summary>
    public required float LowBandEnergy { get; init; }

    /// <summary>Average magnitude in the ~4-10kHz range — hi-hats/cymbals.</summary>
    public required float HighBandEnergy { get; init; }
}
