using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>
/// The owner-notification callbacks <see cref="ThemeSettingsViewModel"/> needs. Settings this
/// panel owns outright (colors, visualizer toggles, ...) are applied and saved internally; these
/// are the handful the owner must still react to itself, e.g. to restart a device, or push a
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
        ListeningStatsEnabled = settings.ListeningStatsEnabled;
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
