using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>
/// The five post-equalizer enhancement sliders for the theme panel: Clarity, Ambience, Surround,
/// Dynamic Boost, and Bass Boost, in the order they run.
/// </summary>
public partial class ThemeSettingsViewModel
{
    [ObservableProperty]
    public partial bool EffectsEnabled { get; set; }

    /// <summary>The five enhancement effects, in the order they run.</summary>
    public ObservableCollection<AudioEffectViewModel> AudioEffects { get; } = [];

    partial void OnEffectsEnabledChanged(bool value)
    {
        _settings.EffectsEnabled = value;
        SettingsService.Save(_settings);
        _onEffectsChanged?.Invoke();
    }

    private void InitializeEffects()
    {
        AudioEffects.Clear();
        AudioEffects.Add(new AudioEffectViewModel(
            "Clarity", "Presence and air, from harmonics of the top end",
            _settings.ClarityAmount, OnEffectChanged));
        AudioEffects.Add(new AudioEffectViewModel(
            "Ambience", "A short reverb, for a sense of space",
            _settings.AmbienceAmount, OnEffectChanged));
        AudioEffects.Add(new AudioEffectViewModel(
            "Surround", "Stereo width, kept out of the bass so it stays mono-safe",
            _settings.SurroundAmount, OnEffectChanged));
        AudioEffects.Add(new AudioEffectViewModel(
            "Dynamic Boost", "Lifts quiet passages without raising the peaks",
            _settings.DynamicBoostAmount, OnEffectChanged));
        AudioEffects.Add(new AudioEffectViewModel(
            "Bass Boost", "Low shelf below about 150 Hz",
            _settings.BassBoostAmount, OnEffectChanged));
    }

    private void OnEffectChanged()
    {
        if (_suppressEqualizerApply || AudioEffects.Count < 5)
            return;

        // Reaching for an effect means the bands are no longer purely the preset's either.
        if (!_applyingPreset)
            SelectedEqualizerPreset = null;

        _settings.ClarityAmount = AudioEffects[0].Amount;
        _settings.AmbienceAmount = AudioEffects[1].Amount;
        _settings.SurroundAmount = AudioEffects[2].Amount;
        _settings.DynamicBoostAmount = AudioEffects[3].Amount;
        _settings.BassBoostAmount = AudioEffects[4].Amount;

        _onEffectsChanged?.Invoke();
    }

    /// <summary>Moves all five effect sliders at once and tells the player a single time.</summary>
    private void SetEffects(AudioEffectSettings effects)
    {
        if (AudioEffects.Count < 5)
            return;

        _suppressEqualizerApply = true;
        AudioEffects[0].SetQuietly(effects.Clarity);
        AudioEffects[1].SetQuietly(effects.Ambience);
        AudioEffects[2].SetQuietly(effects.Surround);
        AudioEffects[3].SetQuietly(effects.DynamicBoost);
        AudioEffects[4].SetQuietly(effects.BassBoost);
        _suppressEqualizerApply = false;

        OnEffectChanged();
    }

    /// <summary>Turns all five effects off, leaving the equalizer alone.</summary>
    [RelayCommand]
    private void ResetEffects() => SetEffects(AudioEffectSettings.None);
}
