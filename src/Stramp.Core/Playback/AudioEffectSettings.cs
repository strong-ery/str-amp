namespace Stramp.Core.Playback;

/// <summary>
/// The five enhancement effects that sit after the equalizer, each as an amount where 0 is off and
/// 10 is the full effect — the same 0-10 scale FXSound puts on its sliders, so a number here means
/// what the same number means over there.
///
/// The names, the scale and the processing all come from FxSound: str-amp hands these five values
/// to its DSP library, so a number here does what the same number does over there. They were once
/// str-amp's own implementations of the five effects, which measured close and still did not sound
/// the same; the library removed the guesswork. See Stramp.Audio's DfxEffectProcessor.
///
/// The preset files store the same scale spread over 0-125 rather than 0-10, which is why some of
/// them carry values above 100: FXSound's "Transcription" stores 115 dynamic boost, and that is
/// 9.2 here, not an overshoot. <see cref="PresetFileScale"/> converts between the two.
/// </summary>
public sealed record AudioEffectSettings
{
    /// <summary>Largest amount any effect accepts; the top of FXSound's slider.</summary>
    public const double MaxAmount = 10;

    /// <summary>
    /// Divisor from the preset files' stored range to this one. The files run 0-125 where the
    /// sliders run 0-10, so the stored 50 in "Music" is the 4 FXSound displays, not a 5.
    /// </summary>
    public const double PresetFileScale = 12.5;

    /// <summary>Amounts below this count as off, and the effect is bypassed entirely.</summary>
    public const double OffThreshold = 0.005;

    /// <summary>High-frequency harmonic excitement: presence and air.</summary>
    public double Clarity { get; init; }

    /// <summary>Short reverb, for a sense of space around the track.</summary>
    public double Ambience { get; init; }

    /// <summary>Stereo widening, applied above the bass so the image stays mono-safe.</summary>
    public double Surround { get; init; }

    /// <summary>Upward compression: lifts quiet passages without pushing peaks any higher.</summary>
    public double DynamicBoost { get; init; }

    /// <summary>Low-end lift.</summary>
    public double BassBoost { get; init; }

    /// <summary>All five off.</summary>
    public static AudioEffectSettings None { get; } = new();

    /// <summary>True when nothing is doing anything and the whole chain can be skipped.</summary>
    public bool IsNeutral =>
        Clarity < OffThreshold && Ambience < OffThreshold && Surround < OffThreshold &&
        DynamicBoost < OffThreshold && BassBoost < OffThreshold;

    /// <summary>The same settings with every amount forced into range and finite.</summary>
    public AudioEffectSettings Clamped() => new()
    {
        Clarity = Clamp(Clarity),
        Ambience = Clamp(Ambience),
        Surround = Clamp(Surround),
        DynamicBoost = Clamp(DynamicBoost),
        BassBoost = Clamp(BassBoost),
    };

    private static double Clamp(double amount) =>
        double.IsFinite(amount) ? Math.Clamp(amount, 0, MaxAmount) : 0;
}
