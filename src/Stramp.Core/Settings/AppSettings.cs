using Stramp.Core.Playback;

namespace Stramp.Core.Settings;

public enum AlbumArtColorMode
{
    None,
    Inferred,
    Direct,
}

public sealed class AppSettings
{
    /// <summary>Folders combined into the music library.</summary>
    public List<string>? LibraryPaths { get; set; }

    /// <summary>Legacy single-folder setting, retained so older configs migrate cleanly.</summary>
    public string? LibraryPath { get; set; }

    /// <summary>Reuses tags for unchanged files so remote and other high-latency libraries load quickly.</summary>
    public bool CacheLibraryMetadata { get; set; } = true;

    /// <summary>Keep a user-scoped shortcut pointing at the current Stramp executable.</summary>
    public bool EnsureDesktopShortcut { get; set; }
    public bool EnsureStartMenuShortcut { get; set; }
    public double Volume { get; set; } = 100;

    /// <summary>Endpoint the user pinned playback to. Null follows the system default device.</summary>
    public string? OutputDeviceId { get; set; }
    public bool Shuffle { get; set; } = true;
    public LoopMode LoopMode { get; set; } = LoopMode.Off;
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 640;
    public bool LibraryPanelOpen { get; set; } = true;
    public bool UpNextPanelOpen { get; set; } = true;

    /// <summary>Whether the now-playing card is split to show the current track's .lrc lyrics.</summary>
    public bool LyricsPanelOpen { get; set; }

    /// <summary>
    /// Looks missing lyrics up on lrclib.net. Sends the playing track's artist, title, album and
    /// length to that service; turning it off keeps lyrics to .lrc files already on disk.
    /// </summary>
    public bool LrcLibLookupEnabled { get; set; } = true;
    public double LeftPanelWidth { get; set; } = 280;
    public double RightPanelWidth { get; set; } = 280;
    public List<string> RemovedSongPaths { get; set; } = [];

    /// <summary>Playlists imported from .m3u/.m3u8; persisted so the source file is not needed again.</summary>
    public List<SavedPlaylist> Playlists { get; set; } = [];

    public string PrimaryAccentColor { get; set; } = "#7C5CFF";
    public string SecondaryAccentColor { get; set; } = "#FF5C93";
    public string BackgroundColor { get; set; } = "#0E0E12";

    /// <summary>When on (the default), theme colors follow the current album art instead of the manual picks above.</summary>
    public bool DeriveColorsFromArt { get; set; } = true;

    /// <summary>Null only for settings files created before the three-way art color option existed.</summary>
    public AlbumArtColorMode? ArtColorMode { get; set; }

    public bool ShowRingVisualizer { get; set; } = true;
    public bool ShowBottomVisualizer { get; set; } = true;
    public bool AnimateAlbumArt { get; set; } = true;
    public bool DisableAnimations { get; set; }
    public bool CircularAlbumArt { get; set; } = true;
    public double BottomVisualizerOpacity { get; set; } = 0.85;
    public bool ShowWaveformProgress { get; set; } = true;

    public bool EqualizerEnabled { get; set; }

    public bool DiscordRichPresenceEnabled { get; set; }

    /// <summary>Null/empty uses str-amp's built-in Discord application (see DiscordPresenceService).
    /// Only needed to point at a different Discord Application, e.g. for local testing.</summary>
    public string? DiscordClientId { get; set; }

    /// <summary>Applies cached per-track loudness gain during playback without modifying files.</summary>
    public bool AudioNormalizationEnabled { get; set; }
    public AudioNormalizationLevel AudioNormalizationLevel { get; set; } = AudioNormalizationLevel.Normal;

    /// <summary>Sums the output to mono, so every channel carries the same signal.</summary>
    public bool MonoAudioEnabled { get; set; }

    /// <summary>When true, plays multi-channel audio (e.g. 5.1 surround) in surround when supported by the device. When false, folds down to stereo.</summary>
    public bool SurroundSoundEnabled { get; set; }

    /// <summary>Per-band gains in dB. Empty means "flat"; resized to the backend's band count on load.</summary>
    public List<double> EqualizerGains { get; set; } = [];

    /// <summary>
    /// Per-band centre frequencies in Hz. Empty means "use the backend's defaults", which is also
    /// what settings written before the bands became adjustable will say.
    /// </summary>
    public List<double> EqualizerFrequencies { get; set; } = [];

    /// <summary>Whether the five enhancement effects run at all, independently of the equalizer.</summary>
    public bool EffectsEnabled { get; set; } = true;

    /// <summary>Amounts for the five post-equalizer effects, on FXSound's 0-10 scale.</summary>
    public double ClarityAmount { get; set; }
    public double AmbienceAmount { get; set; }
    public double SurroundAmount { get; set; }
    public double DynamicBoostAmount { get; set; }
    public double BassBoostAmount { get; set; }
}
