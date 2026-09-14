using Stramp.Core.Models;

namespace Stramp.Core.Playback;

/// <summary>Owns the ordered play queue and current position. Shuffle semantics mirror the original:
/// rebuilding from the library reshuffles everything, "reshuffle" keeps the current track and
/// reshuffles the rest, and running off the end of the queue triggers a full rebuild.</summary>
public sealed class PlaybackQueue
{
    private readonly Random _rng = new();
    private List<Song> _songs = [];

    public IReadOnlyList<Song> Songs => _songs;
    public int Position { get; private set; }

    public Song? Current => Position >= 0 && Position < _songs.Count ? _songs[Position] : null;
    public bool HasNext => Position < _songs.Count - 1;

    /// <summary>Rebuilds the whole queue from the library, shuffling if requested.</summary>
    public void Build(IEnumerable<Song> library, bool shuffled)
    {
        _songs = library.ToList();
        if (shuffled)
            ShuffleInPlace(_songs);
        Position = 0;
    }

    /// <summary>Starts a fresh queue with `song` first, followed by the rest of the library.</summary>
    public void PlayFromLibrary(Song song, IEnumerable<Song> library, bool shuffled)
    {
        var rest = library.Where(s => s.Path != song.Path).ToList();
        if (shuffled)
            ShuffleInPlace(rest);
        _songs = [song, .. rest];
        Position = 0;
    }

    /// <summary>Keeps the current track in place and reshuffles the rest of the library behind it.</summary>
    public void ReshuffleKeepingCurrent(IEnumerable<Song> library)
    {
        var current = Current;
        if (current is null)
            return;
        PlayFromLibrary(current, library, shuffled: true);
    }

    /// <summary>Advances to the next queued track, or rebuilds the queue from the library if at the end.</summary>
    public void AdvanceOrRebuild(IEnumerable<Song> library, bool shuffled)
    {
        if (HasNext)
            Position++;
        else
            Build(library, shuffled);
    }

    public void Previous() => Position = Math.Max(Position - 1, 0);

    public void JumpTo(int index)
    {
        if (index >= 0 && index < _songs.Count)
            Position = index;
    }

    /// <summary>Removes the song at the given queue index. Adjusts position if it was before the current track.</summary>
    public void RemoveAt(int index)
    {
        if (index < 0 || index >= _songs.Count)
            return;

        _songs.RemoveAt(index);
        if (index < Position)
            Position--;
        else if (index == Position)
            Position = Math.Min(Position, _songs.Count - 1);
    }

    private void ShuffleInPlace(List<Song> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = _rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
