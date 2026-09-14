using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>A named subset of the library — currently sourced from .m3u/.m3u8 files on disk.</summary>
public sealed class Playlist
{
    public required string Name { get; init; }
    public required IReadOnlyList<Song> Songs { get; init; }
}
