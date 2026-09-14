namespace Stramp.Core.Playback;

/// <summary>
/// The built-in equalizer curves, converted from FXSound's <c>.fac</c> preset files.
///
/// Each carries its EQ curve at the band centres it was authored against, plus the five effect
/// amounts the same file specifies.
///
/// Some of these are genuine duplicates of each other, in the source files as much as here: Jazz,
/// Classical and Alternative Rock are identical in every field, as are Metal / R&amp;B and
/// Modern Rock / Pop. The 70's / 80's / Classic Rock / Modern Country / Trap group differs only in
/// where a few band centres sit. They are all kept under their own names anyway, since which name
/// someone reaches for is the point of having them.
/// </summary>
public static class EqualizerPresets
{
    /// <summary>Every built-in preset, in the order they are offered.</summary>
    public static IReadOnlyList<EqualizerPreset> All { get; } =
    [
        Preset("Music",
            [62.5f, 110f, 250f, 370f, 650f, 1200f, 2130f, 4550f, 6850f, 16000f],
            [0, 2, 2, 1, 0, 0, 0, -1, 0, 2],
            clarity: 50, ambience: 35, surround: 35, dynamicBoost: 20, bassBoost: 60),
        Preset("70's",
            [62.5f, 110f, 200f, 295f, 650f, 1200f, 2150f, 4550f, 6300f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1],
            clarity: 76, ambience: 0, surround: 89, dynamicBoost: 38, bassBoost: 76),
        Preset("80's",
            [62.5f, 110f, 200f, 295f, 650f, 1200f, 2120f, 4550f, 6300f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1],
            clarity: 76, ambience: 0, surround: 89, dynamicBoost: 38, bassBoost: 76),
        Preset("Classic Rock",
            [62.5f, 110f, 200f, 295f, 650f, 1200f, 2130f, 4550f, 6360f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1],
            clarity: 76, ambience: 0, surround: 89, dynamicBoost: 38, bassBoost: 76),
        Preset("Modern Rock",
            [62.5f, 90f, 230f, 370f, 650f, 1200f, 2125f, 5300f, 10000f, 12000f],
            [1.9685, 0, 0, -1, -2, -3, -3, -2, -1, 0],
            clarity: 38, ambience: 0, surround: 13, dynamicBoost: 89, bassBoost: 25),
        Preset("Alternative Rock",
            [62.5f, 115f, 250f, 450f, 630f, 1250f, 2700f, 5300f, 7500f, 13000f],
            [4.72441, 0, 1, 2, 0, -1, 0, -1, -2, 0],
            clarity: 50, ambience: 20, surround: 0, dynamicBoost: 60, bassBoost: 60),
        Preset("Metal",
            [62.5f, 109.43f, 266.54f, 293f, 738.37f, 1355.22f, 2567.15f, 4719.84f, 8573.18f, 16000f],
            [1.9685, 1, 1, 0, 0, 0, 0, 0, 0, 1],
            clarity: 25, ambience: 20, surround: 38, dynamicBoost: 38, bassBoost: 25),
        Preset("Pop",
            [62.5f, 110f, 230f, 370f, 650f, 1200f, 2150f, 5300f, 10000f, 12000f],
            [1.9685, 0, 0, -1, -2, -3, -3, -2, -1, 0],
            clarity: 38, ambience: 0, surround: 13, dynamicBoost: 89, bassBoost: 25),
        Preset("R&B",
            [62.5f, 109.43f, 266.54f, 293f, 738.37f, 1355.22f, 2567.15f, 4719.84f, 8573.18f, 16000f],
            [1.9685, 1, 1, 0, 0, 0, 0, 0, 0, 1],
            clarity: 25, ambience: 20, surround: 38, dynamicBoost: 38, bassBoost: 25),
        Preset("Trap",
            [62.5f, 100f, 180f, 290f, 650f, 1200f, 2200f, 4550f, 6363f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1],
            clarity: 76, ambience: 0, surround: 89, dynamicBoost: 38, bassBoost: 76),
        Preset("Modern Country",
            [62.5f, 110f, 185f, 285f, 625f, 1200f, 2130f, 4550f, 6360f, 16000f],
            [0, 2, 1, 3, 1, 1, 0, 2, -1, -1],
            clarity: 76, ambience: 0, surround: 89, dynamicBoost: 38, bassBoost: 76),
        Preset("Jazz",
            [62.5f, 115f, 250f, 450f, 630f, 1250f, 2700f, 5300f, 7500f, 13000f],
            [4.72441, 0, 1, 2, 0, -1, 0, -1, -2, 0],
            clarity: 50, ambience: 20, surround: 0, dynamicBoost: 60, bassBoost: 60),
        Preset("Classical",
            [62.5f, 115f, 250f, 450f, 630f, 1250f, 2700f, 5300f, 7500f, 13000f],
            [4.72441, 0, 1, 2, 0, -1, 0, -1, -2, 0],
            clarity: 50, ambience: 20, surround: 0, dynamicBoost: 60, bassBoost: 60),
    ];

    /// <param name="clarity">Effect amounts exactly as the <c>.fac</c> file stores them, which is
    /// FXSound's 0-10 slider scale multiplied by ten. They are divided back down here rather than
    /// pre-converted in the table above, so these numbers can still be diffed against the files.</param>
    private static EqualizerPreset Preset(
        string name, float[] frequencies, double[] gainsDb,
        double clarity = 0, double ambience = 0, double surround = 0,
        double dynamicBoost = 0, double bassBoost = 0)
    {
        if (frequencies.Length != gainsDb.Length)
            throw new ArgumentException($"Preset '{name}' has {frequencies.Length} frequencies but {gainsDb.Length} gains.");

        var points = new EqualizerPresetPoint[frequencies.Length];
        for (var i = 0; i < points.Length; i++)
            points[i] = new EqualizerPresetPoint(frequencies[i], gainsDb[i]);
        const double scale = AudioEffectSettings.PresetFileScale;
        return new EqualizerPreset(name, points, new AudioEffectSettings
        {
            Clarity = clarity / scale,
            Ambience = ambience / scale,
            Surround = surround / scale,
            DynamicBoost = dynamicBoost / scale,
            BassBoost = bassBoost / scale,
        });
    }
}
