using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.ViewModels;

/// <summary>
/// Simple on/off and single-value preferences for the theme panel: album-art visuals, audio
/// processing toggles, lyrics/library behavior, shortcuts, and Discord Rich Presence. Each is
/// saved to disk as soon as it changes.
/// </summary>
public partial class ThemeSettingsViewModel
{
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
}
