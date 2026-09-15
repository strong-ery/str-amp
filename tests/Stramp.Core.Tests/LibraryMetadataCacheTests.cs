using Stramp.Core.Library;
using Stramp.Core.Models;

namespace Stramp.Core.Tests;

public class LibraryMetadataCacheTests
{
    [Fact]
    public void SaveAndLoad_UnchangedFileRestoresSongMetadata()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var audioPath = Path.Combine(directory.FullName, "track.flac");
            var cachePath = Path.Combine(directory.FullName, "metadata.json");
            File.WriteAllBytes(audioPath, [1, 2, 3]);
            var song = new Song
            {
                Path = audioPath,
                Title = "Cached title",
                Artist = "Cached artist",
                Album = "Cached album",
                Duration = TimeSpan.FromSeconds(123),
            };

            var cache = new LibraryMetadataCache(cachePath);
            cache.Put(new FileInfo(audioPath), song);
            cache.Save();

            var restoredCache = new LibraryMetadataCache(cachePath);
            Assert.True(restoredCache.TryGet(new FileInfo(audioPath), out var restored));
            Assert.Equal(song.Title, restored.Title);
            Assert.Equal(song.Artist, restored.Artist);
            Assert.Equal(song.Album, restored.Album);
            Assert.Equal(song.Duration, restored.Duration);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TryGet_ChangedFileInvalidatesCachedMetadata()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var audioPath = Path.Combine(directory.FullName, "track.flac");
            File.WriteAllBytes(audioPath, [1]);
            var cache = new LibraryMetadataCache(Path.Combine(directory.FullName, "metadata.json"));
            cache.Put(new FileInfo(audioPath), new Song
            {
                Path = audioPath,
                Title = "Title",
                Artist = "Artist",
            });

            File.WriteAllBytes(audioPath, [1, 2]);

            Assert.False(cache.TryGet(new FileInfo(audioPath), out _));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
