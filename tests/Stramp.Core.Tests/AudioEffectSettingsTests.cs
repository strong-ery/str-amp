using Stramp.Core.Playback;

namespace Stramp.Core.Tests;

public class AudioEffectSettingsTests
{
    [Fact]
    public void None_IsNeutral()
    {
        Assert.True(AudioEffectSettings.None.IsNeutral);
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0)]
    [InlineData(0, 1, 0, 0, 0)]
    [InlineData(0, 0, 1, 0, 0)]
    [InlineData(0, 0, 0, 1, 0)]
    [InlineData(0, 0, 0, 0, 1)]
    public void AnySingleEffect_MakesItNonNeutral(
        double clarity, double ambience, double surround, double dynamicBoost, double bassBoost)
    {
        var settings = new AudioEffectSettings
        {
            Clarity = clarity,
            Ambience = ambience,
            Surround = surround,
            DynamicBoost = dynamicBoost,
            BassBoost = bassBoost,
        };

        Assert.False(settings.IsNeutral);
    }

    [Fact]
    public void Clamped_HoldsEveryAmountInRange()
    {
        var settings = new AudioEffectSettings
        {
            Clarity = -50,
            Ambience = double.NaN,
            Surround = 9999,
            DynamicBoost = double.PositiveInfinity,
            BassBoost = 6,
        }.Clamped();

        Assert.Equal(0, settings.Clarity);
        Assert.Equal(0, settings.Ambience);
        Assert.Equal(AudioEffectSettings.MaxAmount, settings.Surround);
        Assert.Equal(0, settings.DynamicBoost);
        Assert.Equal(6, settings.BassBoost);
    }

    [Fact]
    public void StoredPresetValuesAboveOneHundred_AreNotOvershoots()
    {
        // "Transcription" stores 115 dynamic boost, which is 9.2 on this scale rather than an
        // overshoot -- the stored range is 0-125, not 0-100.
        Assert.Equal(9.2, 115 / AudioEffectSettings.PresetFileScale, 6);
        Assert.Equal(9.2, new AudioEffectSettings { DynamicBoost = 9.2 }.Clamped().DynamicBoost, 6);
    }

    [Fact]
    public void BuiltInPresets_CarryEffectAmountsInRange()
    {
        foreach (var preset in EqualizerPresets.All)
        {
            var effects = preset.EffectAmounts;
            foreach (var amount in new[]
                     {
                         effects.Clarity, effects.Ambience, effects.Surround,
                         effects.DynamicBoost, effects.BassBoost,
                     })
                Assert.InRange(amount, 0, AudioEffectSettings.MaxAmount);
        }
    }

    [Fact]
    public void BuiltInPresets_AreNotAllSilentlyNeutral()
    {
        // A wiring mistake that dropped the amounts would leave every preset flat; catch that.
        Assert.Contains(EqualizerPresets.All, p => !p.EffectAmounts.IsNeutral);
    }

    [Fact]
    public void PresetEffects_LandOnTheExpectedSlots()
    {
        // Slot order per DfxDspPreset.cpp: fidelity 0, surround 1, ambience 3, dynamic 4, bass 5.
        // Slots 1 and 3 are easy to transpose -- an earlier reading of this had them swapped --
        // so pin a preset where the two differ. "70's" stores 0 in slot 1 and 89 in slot 3.
        var seventies = EqualizerPresets.All.First(p => p.Name == "70's").EffectAmounts;

        Assert.Equal(0, seventies.Surround);
        Assert.Equal(89 / AudioEffectSettings.PresetFileScale, seventies.Ambience, 6);

        var rock = EqualizerPresets.All.First(p => p.Name == "Modern Rock").EffectAmounts;
        Assert.True(rock.DynamicBoost > rock.BassBoost,
            "the loudness-forward curve should ask for more dynamic boost than bass");
    }

    [Fact]
    public void PresetEffects_UseFxSoundsZeroToTenScale()
    {
        // The .fac files store 0-125 where the sliders run 0-10, and FXSound displays "Music" as
        // 4 / 3 / 3 / 2 / 5, which pins the divisor. The slot order is not guessable from these —
        // Music sets surround and ambience to the same number — and comes from DfxDspPreset.cpp.
        var music = EqualizerPresets.All.First(p => p.Name == "Music").EffectAmounts;

        Assert.Equal(4.0, music.Clarity, 6);
        Assert.Equal(2.8, music.Ambience, 6);
        Assert.Equal(2.8, music.Surround, 6);
        Assert.Equal(1.6, music.DynamicBoost, 6);
        Assert.Equal(4.8, music.BassBoost, 6);

        // What the UI prints must match the screenshot: 4, 3, 3, 2, 5.
        Assert.Equal([4, 3, 3, 2, 5], new[]
        {
            music.Clarity, music.Ambience, music.Surround, music.DynamicBoost, music.BassBoost,
        }.Select(a => (int)Math.Round(a)).ToArray());
    }

    /// <summary>
    /// Both readings observed in FXSound's own UI, as (value stored in the .fac file, number the
    /// slider shows). These are the only direct evidence for the 0-125 to 0-10 conversion, so they
    /// are pinned here: a change to <see cref="AudioEffectSettings.PresetFileScale"/> that stops
    /// reproducing them is wrong however reasonable it looks.
    /// </summary>
    [Theory]
    // "Music": clarity, ambience, surround, dynamic boost, bass boost.
    [InlineData(50, 4)]
    [InlineData(35, 3)]
    [InlineData(20, 2)]
    [InlineData(60, 5)]
    // "Bass Boost": the reading that rules out a divisor of 12 or below.
    [InlineData(30, 2)]
    [InlineData(75, 6)]
    public void StoredPresetValue_DisplaysAsFxSoundShowsIt(double stored, int displayed)
    {
        var amount = stored / AudioEffectSettings.PresetFileScale;

        Assert.InRange(amount, 0, AudioEffectSettings.MaxAmount);
        Assert.Equal(displayed, (int)Math.Floor(amount + 0.5));
    }

    [Fact]
    public void PresetFileScale_SitsInTheRangeThoseReadingsAllow()
    {
        // round(stored / d) == displayed bounds d to (12, 13.33] across the two screenshots.
        Assert.InRange(AudioEffectSettings.PresetFileScale, 12.0001, 13.3333);
    }

    [Fact]
    public void PresetEffects_StayWithinTheScale()
    {
        // Nothing imported should land on the old 0-100 reading of the same numbers.
        foreach (var preset in EqualizerPresets.All)
        {
            var e = preset.EffectAmounts;
            foreach (var amount in new[] { e.Clarity, e.Ambience, e.Surround, e.DynamicBoost, e.BassBoost })
                Assert.InRange(amount, 0, 10);
        }
    }

    [Fact]
    public void PresetWithoutEffects_ReportsThemAllOff()
    {
        var preset = new EqualizerPreset("bare", [new(1000, 0)]);

        Assert.True(preset.EffectAmounts.IsNeutral);
    }
}
