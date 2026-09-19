using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
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

    /// <summary>The amount as shown beside the name.</summary>
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

/// <summary>Backs the in-app theme panel: manual color picks, presets, and album-art color mode.</summary>
public partial class ThemeSettingsViewModel : ViewModelBase
{
    private static readonly Color DefaultPrimary = Color.Parse("#7C5CFF");
    private static readonly Color DefaultSecondary = Color.Parse("#FF5C93");
    private static readonly Color DefaultBackground = Color.Parse("#0E0E12");

    private readonly AppSettings _settings;
    private readonly Action _onManualColorsChanged;
    private readonly Action _onNormalizationChanged;
    private readonly Action _onMonoAudioChanged;
    private readonly Action _onSurroundSoundChanged;
    private readonly Action _onLrcLibLookupChanged;
    private readonly Action _onLibraryMetadataCacheChanged;
    private readonly Action<bool> _onDesktopShortcutChanged;
    private readonly Action<bool> _onStartMenuShortcutChanged;
    private readonly Action _onDiscordSettingsChanged;
    private Action? _onEqualizerChanged;
    private Action? _onEffectsChanged;
    private bool _suppressApply;
    private bool _suppressEqualizerApply;
    private bool _applyingPreset;
    private double[] _defaultFrequencies = [];

    /// <summary>Slider limits, matching the range the player clamps gains to.</summary>
    private const double MinBandGainDb = -20;
    private const double MaxBandGainDb = 20;


    public ColorChannelEditor Primary { get; }
    public ColorChannelEditor Secondary { get; }
    public ColorChannelEditor Background { get; }

    public IReadOnlyList<ThemePreset> Presets { get; } =
    [
        new("Nebula", "#7C5CFF", "#FF5C93", "#0E0E12"),
        new("Sunset", "#FF8A3D", "#FF3D77", "#140E12"),
        new("Mint", "#3DDC97", "#36C5F0", "#0A1310"),
        new("Ember", "#FF5252", "#FFB74D", "#140D0D"),
        new("Ice", "#5CC8FF", "#A78BFA", "#0B1016"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesManualColors))]
    public partial AlbumArtColorMode ArtColorMode { get; set; }

    public IReadOnlyList<AlbumArtColorMode> ArtColorModes { get; } =
        Enum.GetValues<AlbumArtColorMode>();

    public bool UsesManualColors => ArtColorMode == AlbumArtColorMode.None;

    [ObservableProperty]
    public partial bool ShowRingVisualizer { get; set; }

    [ObservableProperty]
    public partial bool ShowBottomVisualizer { get; set; }

    [ObservableProperty]
    public partial bool AnimateAlbumArt { get; set; }

    [ObservableProperty]
    public partial bool DisableAnimations { get; set; }

    [ObservableProperty]
    public partial bool CircularAlbumArt { get; set; }

    [ObservableProperty]
    public partial double BottomVisualizerOpacity { get; set; }

    [ObservableProperty]
    public partial bool ShowWaveformProgress { get; set; }

    [ObservableProperty]
    public partial bool EqualizerEnabled { get; set; }

    [ObservableProperty]
    public partial bool EffectsEnabled { get; set; }

    [ObservableProperty]
    public partial bool NormalizeAudio { get; set; }

    [ObservableProperty]
    public partial AudioNormalizationLevel NormalizationLevel { get; set; }

    [ObservableProperty]
    public partial bool MonoAudio { get; set; }

    [ObservableProperty]
    public partial bool SurroundSound { get; set; }

    public IReadOnlyList<AudioNormalizationLevel> NormalizationLevels { get; } =
        Enum.GetValues<AudioNormalizationLevel>();

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

    /// <summary>Whether missing lyrics are looked up on lrclib.net.</summary>
    [ObservableProperty]
    public partial bool LrcLibLookup { get; set; }

    [ObservableProperty]
    public partial bool CacheLibraryMetadata { get; set; }

    [ObservableProperty]
    public partial bool EnsureDesktopShortcut { get; set; }

    [ObservableProperty]
    public partial bool EnsureStartMenuShortcut { get; set; }

    [ObservableProperty]
    public partial bool DiscordRichPresenceEnabled { get; set; }

    [ObservableProperty]
    public partial string DiscordClientId { get; set; } = "";

    public ThemeSettingsViewModel(
        AppSettings settings,
        Action onManualColorsChanged,
        Action? onNormalizationChanged = null,
        Action? onDiscordSettingsChanged = null,
        Action? onMonoAudioChanged = null,
        Action? onSurroundSoundChanged = null,
        Action? onLrcLibLookupChanged = null,
        Action? onLibraryMetadataCacheChanged = null,
        Action<bool>? onDesktopShortcutChanged = null,
        Action<bool>? onStartMenuShortcutChanged = null)
    {
        _settings = settings;
        _onManualColorsChanged = onManualColorsChanged;
        _onNormalizationChanged = onNormalizationChanged ?? (() => { });
        _onDiscordSettingsChanged = onDiscordSettingsChanged ?? (() => { });
        _onMonoAudioChanged = onMonoAudioChanged ?? (() => { });
        _onSurroundSoundChanged = onSurroundSoundChanged ?? (() => { });
        _onLrcLibLookupChanged = onLrcLibLookupChanged ?? (() => { });
        _onLibraryMetadataCacheChanged = onLibraryMetadataCacheChanged ?? (() => { });
        _onDesktopShortcutChanged = onDesktopShortcutChanged ?? (_ => { });
        _onStartMenuShortcutChanged = onStartMenuShortcutChanged ?? (_ => { });

        DiscordRichPresenceEnabled = settings.DiscordRichPresenceEnabled;
        DiscordClientId = settings.DiscordClientId ?? "";

        ArtColorMode = settings.ArtColorMode ??
            (settings.DeriveColorsFromArt ? AlbumArtColorMode.Inferred : AlbumArtColorMode.None);
        ShowRingVisualizer = settings.ShowRingVisualizer;
        ShowBottomVisualizer = settings.ShowBottomVisualizer;
        AnimateAlbumArt = settings.AnimateAlbumArt;
        DisableAnimations = settings.DisableAnimations;
        CircularAlbumArt = settings.CircularAlbumArt;
        BottomVisualizerOpacity = settings.BottomVisualizerOpacity;
        ShowWaveformProgress = settings.ShowWaveformProgress;
        EqualizerEnabled = settings.EqualizerEnabled;
        EffectsEnabled = settings.EffectsEnabled;
        NormalizeAudio = settings.AudioNormalizationEnabled;
        NormalizationLevel = settings.AudioNormalizationLevel;
        MonoAudio = settings.MonoAudioEnabled;
        SurroundSound = settings.SurroundSoundEnabled;
        LrcLibLookup = settings.LrcLibLookupEnabled;
        CacheLibraryMetadata = settings.CacheLibraryMetadata;
        EnsureDesktopShortcut = settings.EnsureDesktopShortcut;
        EnsureStartMenuShortcut = settings.EnsureStartMenuShortcut;
        Primary = new ColorChannelEditor(ParseOrDefault(settings.PrimaryAccentColor, DefaultPrimary), ApplyLive);
        Secondary = new ColorChannelEditor(ParseOrDefault(settings.SecondaryAccentColor, DefaultSecondary), ApplyLive);
        Background = new ColorChannelEditor(ParseOrDefault(settings.BackgroundColor, DefaultBackground), ApplyLive);
    }

    public ThemeSettingsViewModel() : this(new AppSettings(), () => { })
    {
    }

    partial void OnArtColorModeChanged(AlbumArtColorMode value)
    {
        _settings.ArtColorMode = value;
        _settings.DeriveColorsFromArt = value != AlbumArtColorMode.None;
        SettingsService.Save(_settings);
        _onManualColorsChanged();
    }

    partial void OnShowRingVisualizerChanged(bool value)
    {
        _settings.ShowRingVisualizer = value;
        SettingsService.Save(_settings);
    }

    partial void OnShowBottomVisualizerChanged(bool value)
    {
        _settings.ShowBottomVisualizer = value;
        SettingsService.Save(_settings);
    }

    partial void OnAnimateAlbumArtChanged(bool value)
    {
        _settings.AnimateAlbumArt = value;
        SettingsService.Save(_settings);
    }

    partial void OnDisableAnimationsChanged(bool value)
    {
        _settings.DisableAnimations = value;
        SettingsService.Save(_settings);
    }

    partial void OnCircularAlbumArtChanged(bool value)
    {
        _settings.CircularAlbumArt = value;
        SettingsService.Save(_settings);
    }

    partial void OnBottomVisualizerOpacityChanged(double value) =>
        _settings.BottomVisualizerOpacity = value; // saved when the dialog closes, not on every drag tick

    partial void OnShowWaveformProgressChanged(bool value)
    {
        _settings.ShowWaveformProgress = value;
        SettingsService.Save(_settings);
    }

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

    partial void OnNormalizeAudioChanged(bool value)
    {
        _settings.AudioNormalizationEnabled = value;
        SettingsService.Save(_settings);
        _onNormalizationChanged();
    }

    partial void OnNormalizationLevelChanged(AudioNormalizationLevel value)
    {
        _settings.AudioNormalizationLevel = value;
        SettingsService.Save(_settings);
        _onNormalizationChanged();
    }

    partial void OnMonoAudioChanged(bool value)
    {
        _settings.MonoAudioEnabled = value;
        SettingsService.Save(_settings);
        _onMonoAudioChanged();
    }

    partial void OnSurroundSoundChanged(bool value)
    {
        _settings.SurroundSoundEnabled = value;
        SettingsService.Save(_settings);
        _onSurroundSoundChanged();
    }

    partial void OnLrcLibLookupChanged(bool value)
    {
        _settings.LrcLibLookupEnabled = value;
        SettingsService.Save(_settings);
        _onLrcLibLookupChanged();
    }

    partial void OnCacheLibraryMetadataChanged(bool value)
    {
        _settings.CacheLibraryMetadata = value;
        SettingsService.Save(_settings);
        _onLibraryMetadataCacheChanged();
    }

    partial void OnEnsureDesktopShortcutChanged(bool value)
    {
        _settings.EnsureDesktopShortcut = value;
        SettingsService.Save(_settings);
        _onDesktopShortcutChanged(value);
    }

    partial void OnEnsureStartMenuShortcutChanged(bool value)
    {
        _settings.EnsureStartMenuShortcut = value;
        SettingsService.Save(_settings);
        _onStartMenuShortcutChanged(value);
    }

    partial void OnDiscordRichPresenceEnabledChanged(bool value)
    {
        _settings.DiscordRichPresenceEnabled = value;
        SettingsService.Save(_settings);
        _onDiscordSettingsChanged();
    }

    partial void OnDiscordClientIdChanged(string value)
    {
        _settings.DiscordClientId = value;
        SettingsService.Save(_settings);
        _onDiscordSettingsChanged();
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

    private void ApplyLive()
    {
        if (_suppressApply)
            return;

        _settings.PrimaryAccentColor = Primary.Color.ToString();
        _settings.SecondaryAccentColor = Secondary.Color.ToString();
        _settings.BackgroundColor = Background.Color.ToString();
        _onManualColorsChanged();
    }

    [RelayCommand]
    private void ApplyPreset(ThemePreset preset)
    {
        _suppressApply = true;
        Primary.SetColor(Color.Parse(preset.Primary));
        Secondary.SetColor(Color.Parse(preset.Secondary));
        Background.SetColor(Color.Parse(preset.Background));
        _suppressApply = false;

        // Picking a preset is an explicit manual choice, so stop following the artwork.
        ArtColorMode = AlbumArtColorMode.None;
        ApplyLive();
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        _suppressApply = true;
        Primary.SetColor(DefaultPrimary);
        Secondary.SetColor(DefaultSecondary);
        Background.SetColor(DefaultBackground);
        _suppressApply = false;
        ArtColorMode = AlbumArtColorMode.Inferred;
        DisableAnimations = false;
        ApplyLive();
    }

    public void SaveToDisk() => SettingsService.Save(_settings);

    private static Color ParseOrDefault(string value, Color fallback)
    {
        try
        {
            return Color.Parse(value);
        }
        catch
        {
            return fallback;
        }
    }
}

public sealed record ThemePreset(string Name, string Primary, string Secondary, string Background)
{
    public IBrush PrimaryBrush => new SolidColorBrush(Color.Parse(Primary));
    public IBrush SecondaryBrush => new SolidColorBrush(Color.Parse(Secondary));
}

/// <summary>One editable color: bound to the visual picker, with a hex box and preview swatch.</summary>
public partial class ColorChannelEditor : ViewModelBase
{
    private readonly Action _onChanged;
    private bool _updating;

    [ObservableProperty]
    public partial Color Color { get; set; }

    [ObservableProperty]
    public partial string Hex { get; set; } = "#000000";

    [ObservableProperty]
    public partial IBrush PreviewBrush { get; set; } = Brushes.Black;

    public ColorChannelEditor(Color initial, Action onChanged)
    {
        _onChanged = onChanged;
        SetColor(initial);
    }

    public void SetColor(Color color)
    {
        _updating = true;
        Color = color;
        Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        PreviewBrush = new SolidColorBrush(color);
        _updating = false;
    }

    partial void OnColorChanged(Color value)
    {
        if (_updating)
            return;

        _updating = true;
        Hex = $"#{value.R:X2}{value.G:X2}{value.B:X2}";
        PreviewBrush = new SolidColorBrush(value);
        _updating = false;

        _onChanged();
    }

    partial void OnHexChanged(string value)
    {
        if (_updating)
            return;

        try
        {
            SetColor(Avalonia.Media.Color.Parse(value));
            _onChanged();
        }
        catch
        {
            // Invalid/partial hex while typing — ignore until it parses.
        }
    }
}
