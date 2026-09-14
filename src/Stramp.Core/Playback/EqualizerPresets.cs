namespace Stramp.Core.Playback;

/// <summary>
/// The built-in equalizer curves, converted from FXSound's <c>.fac</c> preset files.
///
/// Only the EQ section of those files is represented here. An FXSound preset also carries its
/// fidelity, ambience, surround, dynamic-boost and bass-boost settings, and a good deal of what
/// distinguishes one preset from another over there lives in those effects rather than in the
/// curve. Several of these are identical or near-identical once the EQ is taken on its own —
/// notably 70's / 80's / Classic Rock / Modern Country / Trap, Jazz / Classical / Alternative
/// Rock, Metal / R&amp;B, and Modern Rock / Pop.
/// </summary>
public static class EqualizerPresets
{
    /// <summary>Every built-in preset, in the order they are offered.</summary>
    public static IReadOnlyList<EqualizerPreset> All { get; } =
    [
        Preset("Music",
            [62.5f, 110f, 250f, 370f, 650f, 1200f, 2130f, 4550f, 6850f, 16000f],
            [0, 2, 2, 1, 0, 0, 0, -1, 0, 2]),
        Preset("70's",
            [62.5f, 110f, 200f, 295f, 650f, 1200f, 2150f, 4550f, 6300f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1]),
        Preset("80's",
            [62.5f, 110f, 200f, 295f, 650f, 1200f, 2120f, 4550f, 6300f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1]),
        Preset("Classic Rock",
            [62.5f, 110f, 200f, 295f, 650f, 1200f, 2130f, 4550f, 6360f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1]),
        Preset("Modern Rock",
            [62.5f, 90f, 230f, 370f, 650f, 1200f, 2125f, 5300f, 10000f, 12000f],
            [1.9685, 0, 0, -1, -2, -3, -3, -2, -1, 0]),
        Preset("Alternative Rock",
            [62.5f, 115f, 250f, 450f, 630f, 1250f, 2700f, 5300f, 7500f, 13000f],
            [4.72441, 0, 1, 2, 0, -1, 0, -1, -2, 0]),
        Preset("Metal",
            [62.5f, 109.43f, 266.54f, 293f, 738.37f, 1355.22f, 2567.15f, 4719.84f, 8573.18f, 16000f],
            [1.9685, 1, 1, 0, 0, 0, 0, 0, 0, 1]),
        Preset("Pop",
            [62.5f, 110f, 230f, 370f, 650f, 1200f, 2150f, 5300f, 10000f, 12000f],
            [1.9685, 0, 0, -1, -2, -3, -3, -2, -1, 0]),
        Preset("R&B",
            [62.5f, 109.43f, 266.54f, 293f, 738.37f, 1355.22f, 2567.15f, 4719.84f, 8573.18f, 16000f],
            [1.9685, 1, 1, 0, 0, 0, 0, 0, 0, 1]),
        Preset("Trap",
            [62.5f, 100f, 180f, 290f, 650f, 1200f, 2200f, 4550f, 6363f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1]),
        Preset("Modern Country",
            [62.5f, 110f, 185f, 285f, 625f, 1200f, 2130f, 4550f, 6360f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1]),
        Preset("Jazz",
            [62.5f, 115f, 250f, 450f, 630f, 1250f, 2700f, 5300f, 7500f, 13000f],
            [4.72441, 0, 1, 2, 0, -1, 0, -1, -2, 0]),
        Preset("Classical",
            [62.5f, 115f, 250f, 450f, 630f, 1250f, 2700f, 5300f, 7500f, 13000f],
            [4.72441, 0, 1, 2, 0, -1, 0, -1, -2, 0]),
    ];

    private static EqualizerPreset Preset(string name, float[] frequencies, double[] gainsDb)
    {
        if (frequencies.Length != gainsDb.Length)
            throw new ArgumentException($"Preset '{name}' has {frequencies.Length} frequencies but {gainsDb.Length} gains.");

        var points = new EqualizerPresetPoint[frequencies.Length];
        for (var i = 0; i < points.Length; i++)
            points[i] = new EqualizerPresetPoint(frequencies[i], gainsDb[i]);
        return new EqualizerPreset(name, points);
    }
}
