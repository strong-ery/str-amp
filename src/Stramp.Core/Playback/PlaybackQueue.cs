using Stramp.Core.Models;

namespace Stramp.Core.Playback;

/// <summary>The ordered play queue and the position of the track that is playing in it.</summary>
/// <remarks>
/// The queue keeps played and upcoming songs in one list, with <see cref="Position"/> on the current
/// one. It is built from a source in order, or shuffled with a recorded seed so a saved queue
/// reproduces; reshuffling keeps the current track and shuffles the rest, and running off the end
/// rebuilds from the source.
/// Songs added with <see cref="AddToQueue"/> are marked as queued. Each one goes right after the
/// current track, or after the last queued song still to come, and the queued songs still to come
/// are carried over every rebuild, reshuffle and reorder, so they always play before the source
/// continues.
/// </remarks>
public sealed class PlaybackQueue
{
    private readonly record struct Entry(Song Song, bool Queued);

    private List<Entry> _entries = [];
    private List<Song>? _songs;

    public IReadOnlyList<Song> Songs => _songs ??= _entries.ConvertAll(entry => entry.Song);
    public int Position { get; private set; }
    public int ShuffleSeed { get; private set; }

    public Song? Current => IsInRange(Position) ? _entries[Position].Song : null;
    public bool HasNext => Position < _entries.Count - 1;

    // Queued means added with AddToQueue rather than drawn from the playing source.
    public bool CurrentIsQueued => IsInRange(Position) && _entries[Position].Queued;
    public bool NextIsQueued => IsInRange(Position + 1) && _entries[Position + 1].Queued;

    // The queued songs from the current track on, in play order: what Restore needs to re-mark them.
    public IEnumerable<string> QueuedPathsFromCurrent =>
        _entries.Skip(Position).Where(entry => entry.Queued).Select(entry => entry.Song.Path);

    public void Build(IEnumerable<Song> library, bool shuffled, int? shuffleSeed = null)
    {
        var queued = UpcomingQueued();
        var songs = library.ToList();
        if (shuffled)
            ShuffleInPlace(songs, shuffleSeed);
        else
            ShuffleSeed = 0;
        Replace(songs, 0, currentQueued: false, queued);
    }

    // Shuffled, the pick is hoisted to the front and the rest reshuffled; unshuffled, library order
    // is kept so the next track is the one that follows the pick rather than the top of the list.
    public void PlayFromLibrary(
        Song song, IEnumerable<Song> library, bool shuffled, int? shuffleSeed = null) =>
        StartFrom(song, library, shuffled, shuffleSeed, currentQueued: false);

    public void ReshuffleKeepingCurrent(IEnumerable<Song> library)
    {
        if (Current is { } current)
            StartFrom(current, library, shuffled: true, shuffleSeed: null, CurrentIsQueued);
    }

    public void RestoreOrderKeepingCurrent(IEnumerable<Song> library)
    {
        if (Current is { } current)
            StartFrom(current, library, shuffled: false, shuffleSeed: null, CurrentIsQueued);
        else
            Build(library, shuffled: false);
    }

    // Restores a saved queue. The current track is looked for by path nearest the saved position,
    // since songs that no longer resolve can only have moved it earlier.
    public void Restore(
        IEnumerable<Song> songs, int position, string? currentPath, int shuffleSeed,
        IEnumerable<string>? queuedPaths = null)
    {
        _entries = songs.Select(song => new Entry(song, false)).ToList();
        _songs = null;
        Position = Locate(position, currentPath);
        ShuffleSeed = shuffleSeed;
        MarkQueued(queuedPaths ?? []);
    }

    public void AddToQueue(Song song)
    {
        var lastQueued = _entries.FindLastIndex(entry => entry.Queued);
        var index = lastQueued > Position ? lastQueued + 1 : Position + 1;
        _entries.Insert(Math.Min(index, _entries.Count), new Entry(song, true));
        _songs = null;
    }

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
        if (IsInRange(index))
            Position = index;
    }

    public void RemoveAt(int index)
    {
        if (!IsInRange(index))
            return;

        _entries.RemoveAt(index);
        _songs = null;
        if (index < Position)
            Position--;
        else if (index == Position)
            Position = Math.Min(Position, _entries.Count - 1);
    }

    // Normally at most one entry matches, but a file can be queued more than once, by AddToQueue
    // or by appearing in several playlists merged into one source.
    public void RemoveAllMatching(string path)
    {
        for (var i = _entries.Count - 1; i >= 0; i--)
            if (string.Equals(_entries[i].Song.Path, path, StringComparison.OrdinalIgnoreCase))
                RemoveAt(i);
    }

    private void StartFrom(
        Song song, IEnumerable<Song> library, bool shuffled, int? shuffleSeed, bool currentQueued)
    {
        var queued = UpcomingQueued();
        if (!shuffled)
        {
            ShuffleSeed = 0;
            var songs = library.ToList();
            var position = IndexOf(songs, song);
            if (position < 0)
            {
                songs.Insert(0, song);
                position = 0;
            }
            Replace(songs, position, currentQueued, queued);
            return;
        }

        var rest = library.Where(s => s.Path != song.Path).ToList();
        ShuffleInPlace(rest, shuffleSeed);
        Replace([song, .. rest], 0, currentQueued, queued);
    }

    // Installs `songs` with `position` as the current track and puts the queued songs right behind it.
    private void Replace(List<Song> songs, int position, bool currentQueued, List<Song> queued)
    {
        _entries = songs.ConvertAll(song => new Entry(song, false));
        _songs = null;
        Position = position;
        if (IsInRange(position))
            _entries[position] = _entries[position] with { Queued = currentQueued };
        _entries.InsertRange(
            Math.Min(position + 1, _entries.Count), queued.ConvertAll(song => new Entry(song, true)));
    }

    private List<Song> UpcomingQueued() =>
        _entries.Skip(Position + 1).Where(entry => entry.Queued).Select(entry => entry.Song).ToList();

    private int Locate(int position, string? currentPath)
    {
        if (_entries.Count == 0)
            return 0;

        var start = Math.Clamp(position, 0, _entries.Count - 1);
        if (string.IsNullOrWhiteSpace(currentPath))
            return start;

        var index = _entries.FindLastIndex(start, entry => IsPath(entry, currentPath));
        if (index < 0)
            index = _entries.FindIndex(start, entry => IsPath(entry, currentPath));
        return index >= 0 ? index : start;
    }

    // Marks the first match of each path in turn, walking forward from the current track.
    private void MarkQueued(IEnumerable<string> paths)
    {
        var from = Position;
        foreach (var path in paths)
        {
            var index = _entries.FindIndex(from, entry => IsPath(entry, path));
            if (index < 0)
                continue;

            _entries[index] = _entries[index] with { Queued = true };
            from = index + 1;
        }
    }

    // Prefers the exact entry so duplicate paths in a playlist resolve to the row that was clicked.
    private static int IndexOf(List<Song> songs, Song song)
    {
        var exact = songs.FindIndex(s => ReferenceEquals(s, song));
        return exact >= 0 ? exact : songs.FindIndex(s => s.Path == song.Path);
    }

    private static bool IsPath(Entry entry, string path) =>
        string.Equals(entry.Song.Path, path, StringComparison.OrdinalIgnoreCase);

    private bool IsInRange(int index) => index >= 0 && index < _entries.Count;

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
