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
            Assert.Equal([@"C:\Music\b.flac", @"C:\Music\a.flac"], restored.QueuePaths);
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
