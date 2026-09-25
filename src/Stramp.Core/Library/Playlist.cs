using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>A named subset of songs — from an imported playlist or an .m3u/.m3u8 under the library.</summary>
public sealed class Playlist
{
    public required string Name { get; init; }
    public string? FilePath { get; init; }
    public required IReadOnlyList<Song> Songs { get; init; }
}
