using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>
/// The owner-notification callbacks <see cref="ThemeSettingsViewModel"/> needs. Settings this
/// panel owns outright (colors, visualizer toggles, ...) are applied and saved internally; these
/// are the handful the owner must still react to itself — e.g. to restart a device, or push a
/// value to something outside this view model. All but <see cref="OnManualColorsChanged"/> are
/// optional and default to doing nothing.
/// </summary>
public sealed record ThemeSettingsCallbacks(
    Action OnManualColorsChanged,
    Action? OnNormalizationChanged = null,
    Action? OnDiscordSettingsChanged = null,
    Action? OnMonoAudioChanged = null,
    Action? OnSurroundSoundChanged = null,
    Action? OnLrcLibLookupChanged = null,
    Action? OnLibraryMetadataCacheChanged = null,
    Action<bool>? OnDesktopShortcutChanged = null,
    Action<bool>? OnStartMenuShortcutChanged = null);

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
    public partial bool DisableAnimations { get; set; }

    [ObservableProperty]
    public partial bool CircularAlbumArt { get; set; }

    [ObservableProperty]
    public partial double BottomVisualizerOpacity { get; set; }

    [ObservableProperty]
    public partial bool ShowWaveformProgress { get; set; }

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

    public ThemeSettingsViewModel(AppSettings settings, ThemeSettingsCallbacks callbacks)
    {
        _settings = settings;
        _onManualColorsChanged = callbacks.OnManualColorsChanged;
        _onNormalizationChanged = callbacks.OnNormalizationChanged ?? (() => { });
        _onDiscordSettingsChanged = callbacks.OnDiscordSettingsChanged ?? (() => { });
        _onMonoAudioChanged = callbacks.OnMonoAudioChanged ?? (() => { });
        _onSurroundSoundChanged = callbacks.OnSurroundSoundChanged ?? (() => { });
        _onLrcLibLookupChanged = callbacks.OnLrcLibLookupChanged ?? (() => { });
        _onLibraryMetadataCacheChanged = callbacks.OnLibraryMetadataCacheChanged ?? (() => { });
        _onDesktopShortcutChanged = callbacks.OnDesktopShortcutChanged ?? (_ => { });
        _onStartMenuShortcutChanged = callbacks.OnStartMenuShortcutChanged ?? (_ => { });

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

    public ThemeSettingsViewModel() : this(new AppSettings(), new ThemeSettingsCallbacks(() => { }))
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
