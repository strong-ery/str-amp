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
    public string? LibraryPath { get; set; }
    public double Volume { get; set; } = 100;
    public bool Shuffle { get; set; } = true;
    public LoopMode LoopMode { get; set; } = LoopMode.Off;
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 640;
    public bool LibraryPanelOpen { get; set; } = true;
    public bool UpNextPanelOpen { get; set; } = true;
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
    public double BottomVisualizerOpacity { get; set; } = 0.85;
    public bool ShowWaveformProgress { get; set; } = true;

    public bool EqualizerEnabled { get; set; }

    /// <summary>Applies cached per-track loudness gain during playback without modifying files.</summary>
    public bool AudioNormalizationEnabled { get; set; }
    public AudioNormalizationLevel AudioNormalizationLevel { get; set; } = AudioNormalizationLevel.Normal;

    /// <summary>Per-band gains in dB. Empty means "flat"; resized to the backend's band count on load.</summary>
    public List<double> EqualizerGains { get; set; } = [];
}
