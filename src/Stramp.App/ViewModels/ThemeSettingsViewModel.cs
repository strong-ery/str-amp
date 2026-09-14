using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>One draggable EQ band.</summary>
public partial class EqualizerBandViewModel : ViewModelBase
{
    private readonly Action<double> _onGainChanged;

    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainLabel))]
    public partial double Gain { get; set; }

    /// <summary>
    /// The band's gain as shown above its slider. Always carries an explicit sign so a boost reads
    /// "+3" against a cut's "-3", and only shows a decimal when the value actually has one.
    /// </summary>
    public string GainLabel => Gain.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture);

    public EqualizerBandViewModel(float frequencyHz, double gain, Action<double> onGainChanged)
    {
        _onGainChanged = onGainChanged;
        Gain = gain;
        Label = frequencyHz >= 1000
            ? $"{frequencyHz / 1000:0.#}k"
            : $"{frequencyHz:0}";
    }

    partial void OnGainChanged(double value) => _onGainChanged(value);
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
    private readonly Action _onDiscordSettingsChanged;
    private Action? _onEqualizerChanged;
    private bool _suppressApply;

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
    public partial double BottomVisualizerOpacity { get; set; }

    [ObservableProperty]
    public partial bool ShowWaveformProgress { get; set; }

    [ObservableProperty]
    public partial bool EqualizerEnabled { get; set; }

    [ObservableProperty]
    public partial bool NormalizeAudio { get; set; }

    [ObservableProperty]
    public partial AudioNormalizationLevel NormalizationLevel { get; set; }

    public IReadOnlyList<AudioNormalizationLevel> NormalizationLevels { get; } =
        Enum.GetValues<AudioNormalizationLevel>();

    public ObservableCollection<EqualizerBandViewModel> EqualizerBands { get; } = [];

    [ObservableProperty]
    public partial bool DiscordRichPresenceEnabled { get; set; }

    [ObservableProperty]
    public partial string DiscordClientId { get; set; } = "";

    public ThemeSettingsViewModel(
        AppSettings settings,
        Action onManualColorsChanged,
        Action? onNormalizationChanged = null,
        Action? onDiscordSettingsChanged = null)
    {
        _settings = settings;
        _onManualColorsChanged = onManualColorsChanged;
        _onNormalizationChanged = onNormalizationChanged ?? (() => { });
        _onDiscordSettingsChanged = onDiscordSettingsChanged ?? (() => { });

        DiscordRichPresenceEnabled = settings.DiscordRichPresenceEnabled;
        DiscordClientId = settings.DiscordClientId ?? "";

        ArtColorMode = settings.ArtColorMode ??
            (settings.DeriveColorsFromArt ? AlbumArtColorMode.Inferred : AlbumArtColorMode.None);
        ShowRingVisualizer = settings.ShowRingVisualizer;
        ShowBottomVisualizer = settings.ShowBottomVisualizer;
        AnimateAlbumArt = settings.AnimateAlbumArt;
        BottomVisualizerOpacity = settings.BottomVisualizerOpacity;
        ShowWaveformProgress = settings.ShowWaveformProgress;
        EqualizerEnabled = settings.EqualizerEnabled;
        NormalizeAudio = settings.AudioNormalizationEnabled;
        NormalizationLevel = settings.AudioNormalizationLevel;
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

    partial void OnBottomVisualizerOpacityChanged(double value) =>
        _settings.BottomVisualizerOpacity = value; // saved when the dialog closes, not on every drag tick

    partial void OnShowWaveformProgressChanged(bool value)
    {
        _settings.ShowWaveformProgress = value;
        SettingsService.Save(_settings);
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

    /// <summary>Builds the band sliders once the player has told us what bands it supports.</summary>
    public void InitializeEqualizer(IReadOnlyList<float> bandFrequencies, Action onEqualizerChanged)
    {
        _onEqualizerChanged = onEqualizerChanged;
        EqualizerBands.Clear();

        if (bandFrequencies.Count == 0)
            return;

        while (_settings.EqualizerGains.Count < bandFrequencies.Count)
            _settings.EqualizerGains.Add(0);

        for (var i = 0; i < bandFrequencies.Count; i++)
        {
            var index = i;
            EqualizerBands.Add(new EqualizerBandViewModel(
                bandFrequencies[i],
                _settings.EqualizerGains[i],
                gain =>
                {
                    _settings.EqualizerGains[index] = gain;
                    _onEqualizerChanged?.Invoke();
                }));
        }
    }

    [RelayCommand]
    private void ResetEqualizer()
    {
        foreach (var band in EqualizerBands)
            band.Gain = 0;
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
