using Stramp.Core.Models;
using Stramp.Core.Playback;

namespace Stramp.Core.Tests;

public class PlaybackQueueTests
{
    private static Song Make(string artist, string title) =>
        new() { Path = $"/music/{artist}-{title}.mp3", Title = title, Artist = artist };

    private static List<Song> ThreeSongs() =>
    [
        Make("A", "One"),
        Make("B", "Two"),
        Make("C", "Three"),
    ];

    [Fact]
    public void Build_Unshuffled_KeepsLibraryOrder()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();

        queue.Build(library, shuffled: false);

        Assert.Equal(library, queue.Songs);
        Assert.Equal(0, queue.Position);
        Assert.Equal(library[0], queue.Current);
    }

    [Fact]
    public void Build_Shuffled_ContainsAllSongsExactlyOnce()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();

        queue.Build(library, shuffled: true);

        Assert.Equal(library.Count, queue.Songs.Count);
        Assert.Equal(library.OrderBy(s => s.Path), queue.Songs.OrderBy(s => s.Path));
    }

    [Fact]
    public void PlayFromLibrary_PutsChosenSongFirst()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();

        queue.PlayFromLibrary(library[2], library, shuffled: false);

        Assert.Equal(library[2], queue.Current);
        Assert.Equal(0, queue.Position);
        Assert.Equal(library.Count, queue.Songs.Count);
    }

    [Fact]
    public void AdvanceOrRebuild_MovesForwardWithinQueue()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();
        queue.Build(library, shuffled: false);

        queue.AdvanceOrRebuild(library, shuffled: false);

        Assert.Equal(1, queue.Position);
        Assert.Equal(library[1], queue.Current);
    }

    [Fact]
    public void AdvanceOrRebuild_AtEndOfQueue_RebuildsFromLibrary()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();
        queue.Build(library, shuffled: false);
        queue.JumpTo(library.Count - 1);

        queue.AdvanceOrRebuild(library, shuffled: false);

        Assert.Equal(0, queue.Position);
        Assert.Equal(library.Count, queue.Songs.Count);
    }

    [Fact]
    public void Previous_NeverGoesBelowZero()
    {
        var queue = new PlaybackQueue();
        queue.Build(ThreeSongs(), shuffled: false);

        queue.Previous();

        Assert.Equal(0, queue.Position);
    }

    [Fact]
    public void ReshuffleKeepingCurrent_KeepsCurrentSongAtFront()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();
        queue.Build(library, shuffled: false);
        queue.JumpTo(1);
        var current = queue.Current;

        queue.ReshuffleKeepingCurrent(library);

        Assert.Equal(0, queue.Position);
        Assert.Equal(current, queue.Current);
        Assert.Equal(library.Count, queue.Songs.Count);
    }

    [Fact]
    public void RestoreOrderKeepingCurrent_RestoresLibraryOrderAndCurrentPosition()
    {
        var queue = new PlaybackQueue();
        var library = ThreeSongs();
        queue.Build(library, shuffled: false);
        queue.JumpTo(1);
        var current = queue.Current;
        queue.ReshuffleKeepingCurrent(library);

        queue.RestoreOrderKeepingCurrent(library);

        Assert.Equal(library, queue.Songs);
        Assert.Equal(1, queue.Position);
        Assert.Equal(current, queue.Current);
    }

    [Fact]
    public void RemoveAt_BeforeCurrent_ShiftsPositionBack()
    {
        var queue = new PlaybackQueue();
        queue.Build(ThreeSongs(), shuffled: false);
        queue.JumpTo(2);

        queue.RemoveAt(0);

        Assert.Equal(1, queue.Position);
        Assert.Equal(2, queue.Songs.Count);
    }

    [Fact]
    public void RemoveAt_LastRemainingCurrent_ClampsPosition()
    {
        var queue = new PlaybackQueue();
        queue.Build(ThreeSongs(), shuffled: false);
        queue.JumpTo(2);

        queue.RemoveAt(2);

        Assert.Equal(1, queue.Position);
        Assert.Equal(2, queue.Songs.Count);
    }
}
