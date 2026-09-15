namespace Stramp.Core.Models;

/// <summary>A single track in the library.</summary>
public sealed class Song
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public TimeSpan Duration { get; init; }

    /// <summary>Album tag, or empty when the file has none. Sharpens online lyric matching.</summary>
    public string Album { get; init; } = "";

    public override string ToString() => $"{Artist} - {Title}";
}
