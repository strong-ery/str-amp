using Stramp.Core.Library;
using Stramp.Core.Settings;

namespace Stramp.Core.Tests;

public class PlaylistScannerTests
{
    [Fact]
    public void ParsePaths_ResolvesRelativeEntries_AndSkipsComments()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track = Path.Combine(dir.FullName, "Artist - Title.mp3");
            File.WriteAllBytes(track, []);

            var playlist = Path.Combine(dir.FullName, "mix.m3u8");
            File.WriteAllText(playlist, """
                #EXTM3U
                #EXTINF:123,Artist - Title
                Artist - Title.mp3

                # comment
                """);

            var paths = PlaylistScanner.ParsePaths(playlist);

            Assert.Equal([Path.GetFullPath(track)], paths);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParsePaths_KeepsAbsolutePaths_AndDedupes()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track = Path.GetFullPath(Path.Combine(dir.FullName, "song.flac"));
            var playlist = Path.Combine(dir.FullName, "dupes.m3u");
            File.WriteAllText(playlist, $"{track}{Environment.NewLine}{track}{Environment.NewLine}");

            var paths = PlaylistScanner.ParsePaths(playlist);

            Assert.Equal([track], paths);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void FromSaved_ResolvesLibrarySongs_WithoutReadingM3u()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track = Path.Combine(dir.FullName, "A - B.mp3");
            File.WriteAllBytes(track, []);
            var song = LibraryScanner.ReadSong(track);

            var playlists = PlaylistScanner.FromSaved(
                [new() { Name = "Favorites", SongPaths = [Path.GetFullPath(track)] }],
                [song]);

            var playlist = Assert.Single(playlists);
            Assert.Equal("Favorites", playlist.Name);
            Assert.Same(song, Assert.Single(playlist.Songs));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ResolveSongs_ReadsMissingLibraryEntriesFromDisk()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track = Path.Combine(dir.FullName, "Solo - Track.mp3");
            File.WriteAllBytes(track, []);

            var songs = PlaylistScanner.ResolveSongs([track], []);

            var song = Assert.Single(songs);
            Assert.Equal("Solo", song.Artist);
            Assert.Equal("Track", song.Title);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
