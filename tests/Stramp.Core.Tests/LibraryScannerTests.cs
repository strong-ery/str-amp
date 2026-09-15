using Stramp.Core.Library;

namespace Stramp.Core.Tests;

public class LibraryScannerTests
{
    [Fact]
    public void Scan_FindsSupportedExtensions_AndIgnoresOthers()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "Artist - Title.mp3"), []);
            File.WriteAllBytes(Path.Combine(dir.FullName, "notes.txt"), []);

            var songs = LibraryScanner.Scan(dir.FullName);

            var song = Assert.Single(songs);
            Assert.Equal("Artist", song.Artist);
            Assert.Equal("Title", song.Title);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReadSong_NoSeparatorInFilename_FallsBackToUnknownArtist()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "JustATitle.mp3");
            File.WriteAllBytes(path, []);

            var song = LibraryScanner.ReadSong(path);

            Assert.Equal("Unknown", song.Artist);
            Assert.Equal("JustATitle", song.Title);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Scan_MultipleAndNestedRoots_CombinesWithoutDuplicates()
    {
        var first = Directory.CreateTempSubdirectory();
        var second = Directory.CreateTempSubdirectory();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(first.FullName, "nested"));
            File.WriteAllBytes(Path.Combine(nested.FullName, "One - Song.mp3"), []);
            File.WriteAllBytes(Path.Combine(second.FullName, "Two - Track.flac"), []);

            var songs = LibraryScanner.Scan([first.FullName, nested.FullName, second.FullName]);

            Assert.Equal(2, songs.Count);
            Assert.Contains(songs, song => song.Artist == "One");
            Assert.Contains(songs, song => song.Artist == "Two");
        }
        finally
        {
            first.Delete(recursive: true);
            second.Delete(recursive: true);
        }
    }

    [Fact]
    public void Scan_CancelledScan_StopsBeforeReadingFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            LibraryScanner.Scan([Path.GetTempPath()], cancellation.Token));
    }
}
