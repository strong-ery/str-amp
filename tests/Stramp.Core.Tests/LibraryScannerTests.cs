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
}
