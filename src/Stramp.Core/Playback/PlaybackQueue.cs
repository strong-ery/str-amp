using Stramp.Core.Models;

namespace Stramp.Core.Playback;

/// <summary>Owns the ordered play queue and current position. Shuffle semantics mirror the original:
/// rebuilding from the library reshuffles everything, "reshuffle" keeps the current track and
/// reshuffles the rest, and running off the end of the queue triggers a full rebuild.</summary>
public sealed class PlaybackQueue
{
    private List<Song> _songs = [];

    public IReadOnlyList<Song> Songs => _songs;
    public int Position { get; private set; }
    public int ShuffleSeed { get; private set; }

    public Song? Current => Position >= 0 && Position < _songs.Count ? _songs[Position] : null;
    public bool HasNext => Position < _songs.Count - 1;

    /// <summary>Rebuilds the whole queue from the library, shuffling if requested.</summary>
    public void Build(IEnumerable<Song> library, bool shuffled, int? shuffleSeed = null)
    {
        _songs = library.ToList();
        if (shuffled)
            ShuffleInPlace(_songs, shuffleSeed);
        else
            ShuffleSeed = 0;
        Position = 0;
    }

    /// <summary>Starts a fresh queue with `song` first, followed by the rest of the library.</summary>
    public void PlayFromLibrary(
        Song song, IEnumerable<Song> library, bool shuffled, int? shuffleSeed = null)
    {
        var rest = library.Where(s => s.Path != song.Path).ToList();
        if (shuffled)
            ShuffleInPlace(rest, shuffleSeed);
        else
            ShuffleSeed = 0;
        _songs = [song, .. rest];
        Position = 0;
    }

    /// <summary>Restores an exact persisted queue, including its current entry and shuffle seed.</summary>
    public void Restore(IEnumerable<Song> songs, int position, int shuffleSeed)
    {
        _songs = songs.ToList();
        Position = _songs.Count == 0 ? 0 : Math.Clamp(position, 0, _songs.Count - 1);
        ShuffleSeed = shuffleSeed;
    }

    /// <summary>Keeps the current track in place and reshuffles the rest of the library behind it.</summary>
    public void ReshuffleKeepingCurrent(IEnumerable<Song> library)
    {
        var current = Current;
        if (current is null)
            return;
        PlayFromLibrary(current, library, shuffled: true);
    }

    /// <summary>Restores library/playlist order without changing the currently playing track.</summary>
    public void RestoreOrderKeepingCurrent(IEnumerable<Song> library)
    {
        var current = Current;
        _songs = library.ToList();
        ShuffleSeed = 0;

        if (current is null)
        {
            Position = 0;
            return;
        }

        Position = _songs.FindIndex(song => song.Path == current.Path);
        if (Position >= 0)
            return;

        _songs.Insert(0, current);
        Position = 0;
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

    private void ShuffleInPlace(List<Song> list, int? shuffleSeed = null)
    {
        ShuffleSeed = shuffleSeed ?? Random.Shared.Next(1, int.MaxValue);
        var rng = new Random(ShuffleSeed);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
