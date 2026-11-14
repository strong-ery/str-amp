using CommunityToolkit.Mvvm.Input;
using Stramp.App.Services;
using Stramp.Core.Playback;
using Stramp.Core.Settings;
using Stramp.Integrations.Windows;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    [RelayCommand]
    private void ToggleLibraryOpen() => IsLibraryOpen = !IsLibraryOpen;

    [RelayCommand]
    private void ToggleUpNextOpen() => IsUpNextOpen = !IsUpNextOpen;

    [RelayCommand]
    private void ToggleLyricsOpen() => IsLyricsOpen = !IsLyricsOpen;

    private void ApplyLibraryMetadataCacheSetting()
    {
        if (_settings.CacheLibraryMetadata && _libraryPaths.Count > 0)
            _ = ReloadLibraryAsync(restorePlaybackState: false, preservePlayback: true);
    }

    private void ApplyDesktopShortcutSetting(bool enabled)
    {
        if (!WindowsShortcutService.SetDesktopShortcut(enabled))
            StatusText = enabled
                ? "Couldn't create the Desktop shortcut."
                : "Couldn't remove the Desktop shortcut.";
    }

    private void ApplyStartMenuShortcutSetting(bool enabled)
    {
        if (!WindowsShortcutService.SetStartMenuShortcut(enabled))
            StatusText = enabled
                ? "Couldn't create the Start Menu shortcut."
                : "Couldn't remove the Start Menu shortcut.";
    }

    [RelayCommand]
    private void EqualizeSidePanels()
    {
        var avg = (LeftPanelWidth + RightPanelWidth) / 2.0;
        avg = Math.Clamp(avg, 180, 500);
        LeftPanelWidth = avg;
        RightPanelWidth = avg;
        IsLibraryOpen = true;
        IsUpNextOpen = true;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        AccrueListeningTime();
        UpdateListeningStatsDisplay();
        IsSettingsOpen = true;
    }

    [RelayCommand]
    private void CloseSettings()
    {
        IsSettingsOpen = false;
        AccrueListeningTime();
        Theme.SaveToDisk();
        lock (_listeningStatsLock)
        {
            _listeningHistoryStore.Save(_listeningHistory);
        }
    }

    private void ApplyEffects() =>
        _player.ApplyEffects(!_settings.EffectsEnabled ? AudioEffectSettings.None : new AudioEffectSettings
        {
            Clarity = _settings.ClarityAmount,
            Ambience = _settings.AmbienceAmount,
            Surround = _settings.SurroundAmount,
            DynamicBoost = _settings.DynamicBoostAmount,
            BassBoost = _settings.BassBoostAmount,
        });

    private void ApplyEqualizer() =>
        _player.ApplyEqualizer(
            [.. _settings.EqualizerFrequencies.Select(hz => (float)hz)],
            _settings.EqualizerGains,
            _settings.EqualizerEnabled);

    private void ApplyMonoOutputSetting() => _player.MonoOutput = _settings.MonoAudioEnabled;

    private void ApplySurroundSoundSetting() => _player.SurroundSoundEnabled = _settings.SurroundSoundEnabled;

    private void ApplyDiscordPresenceSetting()
    {
        _discordPresence.Configure(_settings.DiscordClientId, _settings.DiscordRichPresenceEnabled);
        UpdateDiscordPresence();
    }

    /// <summary>Pushes the current track/playback-state to Discord. A no-op when the integration
    /// is off or not configured; see <see cref="DiscordPresenceService"/>.</summary>
    private void UpdateDiscordPresence()
    {
        if (_queue.Current is { } song)
            _discordPresence.UpdateNowPlaying(song, IsPlaying, ProgressSeconds, DurationSeconds);
        else
            _discordPresence.Clear();
    }

    // Re-applies the theme from whichever source is currently active: artwork or manual picks.
    private void ApplyTheme()
    {
        var palette = _settings.ArtColorMode switch
        {
            AlbumArtColorMode.Inferred when CurrentArtBitmap is not null =>
                _inferredArtPalette ??= AlbumPalette.ExtractInferred(CurrentArtBitmap),
            AlbumArtColorMode.Direct when CurrentArtBitmap is not null =>
                _directArtPalette ??= AlbumPalette.ExtractDirect(CurrentArtBitmap),
            _ => null,
        };

        if (palette is { } artPalette)
            ThemeService.ApplyFromArt(artPalette, _settings);
        else
            ThemeService.Apply(_settings);
    }

    partial void OnIsLibraryOpenChanged(bool value)
    {
        _settings.LibraryPanelOpen = value;
        SettingsService.Save(_settings);
    }

    partial void OnIsUpNextOpenChanged(bool value)
    {
        _settings.UpNextPanelOpen = value;
        SettingsService.Save(_settings);
    }

    partial void OnIsLyricsOpenChanged(bool value)
    {
        _settings.LyricsPanelOpen = value;
        SettingsService.Save(_settings);
    }

    partial void OnLeftPanelWidthChanged(double value)
    {
        _settings.LeftPanelWidth = value;
        SettingsService.Save(_settings);
    }

    partial void OnRightPanelWidthChanged(double value)
    {
        _settings.RightPanelWidth = value;
        SettingsService.Save(_settings);
    }

}
