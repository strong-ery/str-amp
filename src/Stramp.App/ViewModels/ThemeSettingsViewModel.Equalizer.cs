using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>
/// Equalizer state for the theme panel: the band sliders, the built-in curve presets, and the
/// constraints that keep band centres ordered. Effect-slider state lives alongside this in
/// <see cref="ThemeSettingsViewModel.Effects"/>.cs; presets drive both from here.
/// </summary>
public partial class ThemeSettingsViewModel
{
    /// <summary>Slider limits, matching the range the player clamps gains to.</summary>
    private const double MinBandGainDb = -20;
    private const double MaxBandGainDb = 20;

    private Action? _onEqualizerChanged;
    private Action? _onEffectsChanged;
    private bool _suppressEqualizerApply;
    private bool _applyingPreset;
    private double[] _defaultFrequencies = [];

    [ObservableProperty]
    public partial bool EqualizerEnabled { get; set; }

    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = [];

    /// <summary>Built-in curves offered above the band sliders.</summary>
    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; } =
        Stramp.Core.Playback.EqualizerPresets.All;

    /// <summary>
    /// The preset showing in the dropdown. Null whenever the bands do not match one, which is the
    /// case at startup, and again as soon as anything is adjusted by hand.
    /// </summary>
    [ObservableProperty]
    public partial EqualizerPreset? SelectedEqualizerPreset { get; set; }

    partial void OnEqualizerEnabledChanged(bool value)
    {
        _settings.EqualizerEnabled = value;
        SettingsService.Save(_settings);
        _onEqualizerChanged?.Invoke();
    }

    /// <summary>Builds the band controls once the player has told us what bands it defaults to.</summary>
    public void InitializeEqualizer(
        IReadOnlyList<float> defaultBandFrequencies, Action onEqualizerChanged, Action onEffectsChanged)
    {
        _onEqualizerChanged = onEqualizerChanged;
        _onEffectsChanged = onEffectsChanged;
        InitializeEffects();
        _defaultFrequencies = [.. defaultBandFrequencies.Select(f => (double)f)];
        EqualizerBands.Clear();

        var count = _defaultFrequencies.Length;
        if (count == 0)
            return;

        // Settings written before the bands became adjustable carry gains but no frequencies, and
        // the band count can change between versions; fill either from the backend's defaults.
        Resize(_settings.EqualizerGains, count, _ => 0);
        Resize(_settings.EqualizerFrequencies, count, i => _defaultFrequencies[i]);

        for (var i = 0; i < count; i++)
        {
            var index = i;
            EqualizerBands.Add(new EqualizerBandViewModel(
                _settings.EqualizerFrequencies[i],
                _settings.EqualizerGains[i],
                frequencyMoved => OnBandChanged(index, frequencyMoved)));
        }
    }

    /// <summary>Writes the bands back to settings and hands the whole layout to the player.</summary>
    private void OnBandChanged(int index, bool frequencyMoved)
    {
        // Each band closes over its own index, and re-initialising replaces the whole collection;
        // ignore anything arriving from a band that is no longer the one at that position.
        if (_suppressEqualizerApply || index >= EqualizerBands.Count ||
            _settings.EqualizerGains.Count < EqualizerBands.Count ||
            _settings.EqualizerFrequencies.Count < EqualizerBands.Count)
            return;

        if (frequencyMoved)
            ConstrainBand(index);

        // Adjusting anything by hand means the bands are no longer the preset's curve, so the
        // dropdown should stop claiming they are.
        if (!_applyingPreset)
            SelectedEqualizerPreset = null;

        for (var i = 0; i < EqualizerBands.Count; i++)
        {
            _settings.EqualizerGains[i] = EqualizerBands[i].Gain;
            _settings.EqualizerFrequencies[i] = EqualizerBands[i].Frequency;
        }

        _onEqualizerChanged?.Invoke();
    }

    /// <summary>Holds an edited centre between its neighbours; see <see cref="EqualizerBandLayout"/>.</summary>
    private void ConstrainBand(int index)
    {
        var band = EqualizerBands[index];
        var clamped = Math.Round(
            EqualizerBandLayout.Constrain([.. EqualizerBands.Select(b => b.Frequency)], index, band.Frequency), 2);

        if (Math.Abs(clamped - band.Frequency) > 1e-9)
            band.SetFrequencyQuietly(clamped);
    }

    partial void OnSelectedEqualizerPresetChanged(EqualizerPreset? value)
    {
        // Null means the selection was cleared because the bands drifted off the curve, not that
        // the user picked something; there is nothing to load in that case.
        if (value is null || _applyingPreset)
            return;

        _applyingPreset = true;
        try
        {
            ApplyEqualizerPreset(value);
        }
        finally
        {
            _applyingPreset = false;
        }
    }

    private void ApplyEqualizerPreset(EqualizerPreset preset)
    {
        if (EqualizerBands.Count == 0)
            return;

        // The bundled presets each carry their own band centres, so when the shapes line up we can
        // reproduce the curve exactly by moving the bands as well. Otherwise the curve is resampled
        // onto wherever the bands currently sit.
        if (preset.Points.Count == EqualizerBands.Count)
            SetBands(
                [.. preset.Points.Select(p => (double)p.Hz)],
                [.. preset.Points.Select(p => p.GainDb)]);
        else
            SetBands(null, preset.GainsFor([.. EqualizerBands.Select(b => (float)b.Frequency)]));

        SetEffects(preset.EffectAmounts);

        // Picking a curve is a request to hear it; leaving it staged behind a switch that is still
        // off would just look broken.
        EqualizerEnabled = true;
    }

    /// <summary>Returns every band to the centre frequency the backend started with.</summary>
    [RelayCommand]
    private void ResetEqualizerBands() => SetBands(_defaultFrequencies, null);

    [RelayCommand]
    private void ResetEqualizer() => SetBands(null, new double[EqualizerBands.Count]);

    /// <summary>
    /// Moves the bands, then tells the player a single time. Setting them one by one would re-solve
    /// and retune the whole filter bank once per band for what is one click.
    /// </summary>
    /// <param name="frequencies">New centre frequencies, or null to leave them where they are.</param>
    /// <param name="gains">New gains in dB, or null to leave them as they are.</param>
    private void SetBands(IReadOnlyList<double>? frequencies, IReadOnlyList<double>? gains)
    {
        _suppressEqualizerApply = true;
        for (var i = 0; i < EqualizerBands.Count; i++)
        {
            // Set quietly: a preset is a complete, already-ordered layout, so the neighbour
            // clamp that guards single edits would only fight it half-applied.
            if (frequencies is not null && i < frequencies.Count)
                EqualizerBands[i].SetFrequencyQuietly(Math.Clamp(
                    Math.Round(frequencies[i], 2),
                    EqualizerBandViewModel.MinFrequencyHz,
                    EqualizerBandViewModel.MaxFrequencyHz));

            if (gains is not null && i < gains.Count)
                EqualizerBands[i].Gain = Math.Round(Math.Clamp(gains[i], MinBandGainDb, MaxBandGainDb), 1);
        }
        _suppressEqualizerApply = false;

        OnBandChanged(0, frequencyMoved: false);
    }

    private static void Resize(List<double> values, int count, Func<int, double> fallback)
    {
        while (values.Count < count)
            values.Add(fallback(values.Count));
        if (values.Count > count)
            values.RemoveRange(count, values.Count - count);
    }
}
