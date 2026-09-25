namespace Stramp.Core.Settings;

/// <summary>A playlist imported once from an .m3u/.m3u8 — song paths are kept, the file is not re-read.</summary>
public sealed class SavedPlaylist
{
    public string Name { get; set; } = "";
    public string? FilePath { get; set; }
    public List<string> SongPaths { get; set; } = [];
}
