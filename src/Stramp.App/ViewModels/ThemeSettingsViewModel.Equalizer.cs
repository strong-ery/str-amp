using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>One EQ band: a draggable gain and an editable centre frequency.</summary>
public partial class EqualizerBandViewModel : ViewModelBase
{
    /// <summary>
    /// Range a centre frequency may be typed into. The audible band, give or take — the player
    /// separately keeps bands ordered and below what the current sample rate can represent.
    /// </summary>
    public const double MinFrequencyHz = EqualizerBandLayout.MinFrequencyHz;
    public const double MaxFrequencyHz = EqualizerBandLayout.MaxFrequencyHz;

    private readonly Action<bool> _onChanged;

    /// <summary>
    /// Swallows change notifications. Starts on, because assigning the initial values below runs
    /// the same change handlers, and the owner cannot answer questions about a band it has not
    /// finished adding to its collection yet.
    /// </summary>
    private bool _suppressNotify = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainLabel))]
    public partial double Gain { get; set; }

    [ObservableProperty]
    public partial double Frequency { get; set; }

    /// <summary>
    /// The band's gain as shown above its slider. Always carries an explicit sign so a boost reads
    /// "+3" against a cut's "-3", and only shows a decimal when the value actually has one.
    /// </summary>
    public string GainLabel => Gain.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    /// <param name="onChanged">Called after a change; the argument is true when it was the
    /// centre frequency that moved rather than the gain.</param>
    public EqualizerBandViewModel(double frequencyHz, double gain, Action<bool> onChanged)
    {
        _onChanged = onChanged;
        Gain = gain;
        Frequency = frequencyHz;
        _suppressNotify = false;
    }

    /// <summary>Sets the centre without reporting it, for the owner's own bookkeeping.</summary>
    public void SetFrequencyQuietly(double value)
    {
        var wasSuppressed = _suppressNotify;
        _suppressNotify = true;
        Frequency = value;
        _suppressNotify = wasSuppressed;
    }

    partial void OnGainChanged(double value)
    {
        if (!_suppressNotify)
            _onChanged(false);
    }

    partial void OnFrequencyChanged(double value)
    {
        if (_suppressNotify)
            return;

        // A typed-in frequency can be anything, including blank or absurd. Snap it back into range
        // first; the owner then holds it between its neighbours.
        var clamped = double.IsFinite(value)
            ? Math.Clamp(value, MinFrequencyHz, MaxFrequencyHz)
            : MinFrequencyHz;

        if (Math.Abs(clamped - value) > 1e-9)
            SetFrequencyQuietly(clamped);

        _onChanged(true);
    }
}

/// <summary>One of the five post-equalizer enhancement effects, as a named 0-10 slider.</summary>
public partial class AudioEffectViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    /// <summary>
    /// Swallows change notifications. Starts on, because assigning the initial amount runs the
    /// same change handler, and the owner cannot answer for an effect it is still constructing.
    /// </summary>
    private bool _suppressNotify = true;

    public string Name { get; }
    public string Description { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountLabel))]
    public partial double Amount { get; set; }

    public AudioEffectViewModel(string name, string description, double amount, Action onChanged)
    {
        Name = name;
        Description = description;
        _onChanged = onChanged;
        Amount = amount;
        _suppressNotify = false;
    }

    /// <summary>
    /// Shown beside the slider, rounded to a whole number the way FXSound shows the same value.
    /// A preset's amount can sit between two of them — "Music" asks for 2.8 ambience — and is kept
    /// at full precision underneath rather than being snapped to what the label can print.
    /// </summary>
    public string AmountLabel => Amount < AudioEffectSettings.OffThreshold
        ? "off"
        : Amount.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Sets the amount without reporting it, for the owner's own bookkeeping.</summary>
    public void SetQuietly(double value)
    {
        var wasSuppressed = _suppressNotify;
        _suppressNotify = true;
        Amount = Math.Clamp(Math.Round(value, 2), 0, AudioEffectSettings.MaxAmount);
        _suppressNotify = wasSuppressed;
    }

    partial void OnAmountChanged(double value)
    {
        if (_suppressNotify)
            return;

        // Dragging lands on arbitrary decimals. Snap to whole numbers so a hand-set slider is
        // exactly the value its label prints; presets bypass this and keep their own precision.
        var snapped = double.IsFinite(value)
            ? Math.Clamp(Math.Round(value), 0, AudioEffectSettings.MaxAmount)
            : 0;

        if (Math.Abs(snapped - value) > 1e-9)
        {
            SetQuietly(snapped);
            _onChanged();
            return;
        }

        _onChanged();
    }
}

/// <summary>
/// Equalizer and post-effects state for the theme panel: bands, presets, the five enhancement
/// sliders, and the constraints that keep band centres ordered.
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

    [ObservableProperty]
    public partial bool EffectsEnabled { get; set; }

    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = [];

    /// <summary>Built-in curves offered above the band sliders.</summary>
    public IReadOnlyList<EqualizerPreset> EqualizerPresets { get; } =
        Stramp.Core.Playback.EqualizerPresets.All;

    /// <summary>
    /// The preset showing in the dropdown. Null whenever the bands do not match one — which is the
    /// case at startup, and again as soon as anything is adjusted by hand.
    /// </summary>
    [ObservableProperty]
    public partial EqualizerPreset? SelectedEqualizerPreset { get; set; }

    /// <summary>The five enhancement effects, in the order they run.</summary>
    public ObservableCollection<AudioEffectViewModel> AudioEffects { get; } = [];

    partial void OnEffectsEnabledChanged(bool value)
    {
        _settings.EffectsEnabled = value;
        SettingsService.Save(_settings);
        _onEffectsChanged?.Invoke();
    }

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
