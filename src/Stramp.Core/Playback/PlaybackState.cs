namespace Stramp.Core.Playback;

/// <summary>A resumable snapshot of the active source, queue, and playhead.</summary>
public sealed class SavedPlaybackState
{
    public int Version { get; set; } = 1;
    public List<string> LibraryPaths { get; set; } = [];
    public string? LibraryPath { get; set; }
    public string SourceName { get; set; } = "All Songs";
    public bool IsBrowsingSources { get; set; }
    public List<string> ActiveSongPaths { get; set; } = [];
    public int ActiveSourcePosition { get; set; }
    public List<string> QueuePaths { get; set; } = [];
    public int QueuePosition { get; set; }
    public string? CurrentSongPath { get; set; }
    public int ShuffleSeed { get; set; }
    public bool Shuffled { get; set; }
    public LoopMode LoopMode { get; set; }
    public double PositionSeconds { get; set; }
    public bool WasPlaying { get; set; }
    public string? SortOption { get; set; }
    public DateTime SavedAtUtc { get; set; }
}
