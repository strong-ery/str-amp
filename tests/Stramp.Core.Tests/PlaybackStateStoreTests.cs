using Stramp.Core.Playback;

namespace Stramp.Core.Tests;

public class PlaybackStateStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stramp-state-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "state.json");
        try
        {
            var store = new PlaybackStateStore(path);
            store.Save(new SavedPlaybackState
            {
                LibraryPaths = [@"C:\Music", @"D:\More Music"],
                LibraryPath = @"C:\Music",
                SourceName = "Favorites",
                ActiveSongPaths = [@"C:\Music\a.flac", @"C:\Music\b.flac"],
                QueuePaths = [@"C:\Music\b.flac", @"C:\Music\a.flac"],
                QueuePosition = 1,
                CurrentSongPath = @"C:\Music\a.flac",
                ShuffleSeed = 1234,
                Shuffled = true,
                LoopMode = LoopMode.Playlist,
                PositionSeconds = 91.25,
                WasPlaying = true,
            });

            var restored = store.Load();

            Assert.NotNull(restored);
            Assert.Equal("Favorites", restored.SourceName);
            Assert.Equal(1, restored.QueuePosition);
            Assert.Equal(1234, restored.ShuffleSeed);
            Assert.Equal(91.25, restored.PositionSeconds);
            Assert.Equal([@"C:\Music", @"D:\More Music"], restored.LibraryPaths);
            Assert.Equal([@"C:\Music\b.flac", @"C:\Music\a.flac"], restored.QueuePaths);
            Assert.Equal("Favorites", restored.SourceName);
            Assert.True(restored.Shuffled);
            Assert.True(restored.WasPlaying);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveAndLoad_PreservesPlaylistSourceAndBrowsingMode()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stramp-state-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "state.json");
        try
        {
            var store = new PlaybackStateStore(path);
            store.Save(new SavedPlaybackState
            {
                LibraryPaths = [@"D:\Audio\Library"],
                SourceName = "Cyberpunk Mix",
                IsBrowsingSources = false,
                SortOption = "Playlist",
                ActiveSongPaths = [@"D:\Audio\Library\track1.mp3", @"D:\Audio\Library\track2.mp3"],
                QueuePaths = [@"D:\Audio\Library\track2.mp3", @"D:\Audio\Library\track1.mp3"],
                QueuePosition = 0,
                CurrentSongPath = @"D:\Audio\Library\track2.mp3",
                ShuffleSeed = 98765,
                Shuffled = true,
                LoopMode = LoopMode.Off,
                PositionSeconds = 45.5,
                WasPlaying = false,
            });

            var restored = store.Load();

            Assert.NotNull(restored);
            Assert.Equal("Cyberpunk Mix", restored.SourceName);
            Assert.False(restored.IsBrowsingSources);
            Assert.Equal("Playlist", restored.SortOption);
            Assert.Equal(0, restored.QueuePosition);
            Assert.Equal(98765, restored.ShuffleSeed);
            Assert.True(restored.Shuffled);
            Assert.Equal(@"D:\Audio\Library\track2.mp3", restored.CurrentSongPath);
            Assert.Equal(45.5, restored.PositionSeconds);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_CorruptSnapshot_ReturnsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"stramp-state-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "state.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "not json");

            Assert.Null(new PlaybackStateStore(path).Load());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
