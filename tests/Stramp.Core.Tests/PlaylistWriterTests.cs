using Stramp.Core.Library;
using Stramp.Core.Models;
using Stramp.Core.Settings;

namespace Stramp.Core.Tests;

public class PlaylistWriterTests
{
    [Fact]
    public void ToM3u8_FormatsExtendedM3uCorrectly()
    {
        var songs = new List<Song>
        {
            new()
            {
                Path = @"C:\Music\Artist1\Song1.mp3",
                Title = "Song One",
                Artist = "Artist One",
                Album = "Album One",
                Duration = TimeSpan.FromSeconds(215),
            },
            new()
            {
                Path = @"C:\Music\Artist2\Song2.flac",
                Title = "Song Two",
                Artist = "Artist Two",
                Album = "Album Two",
                Duration = TimeSpan.FromSeconds(180.4),
            },
        };

        var content = PlaylistWriter.ToM3u8(songs);

        Assert.StartsWith("#EXTM3U", content);
        Assert.Contains("#EXTINF:215,Artist One - Song One", content);
        Assert.Contains(@"C:\Music\Artist1\Song1.mp3", content);
        Assert.Contains("#EXTINF:180,Artist Two - Song Two", content);
        Assert.Contains(@"C:\Music\Artist2\Song2.flac", content);
    }

    [Fact]
    public void ToM3u8_HandlesMissingArtistOrTitle_FallsBackGracefully()
    {
        var songs = new List<Song>
        {
            new()
            {
                Path = @"C:\Music\StandaloneSong.wav",
                Title = "",
                Artist = "",
                Duration = TimeSpan.Zero,
            },
            new()
            {
                Path = @"C:\Music\OnlyTitle.mp3",
                Title = "Just Title",
                Artist = "",
                Duration = TimeSpan.FromSeconds(50),
            },
        };

        var content = PlaylistWriter.ToM3u8(songs);

        Assert.StartsWith("#EXTM3U", content);
        Assert.Contains("#EXTINF:-1,StandaloneSong", content);
        Assert.Contains(@"C:\Music\StandaloneSong.wav", content);
        Assert.Contains("#EXTINF:50,Just Title", content);
    }

    [Fact]
    public void ToM3u8FromPaths_FormatsPathsCorrectly()
    {
        var paths = new[]
        {
            @"C:\Music\Track1.mp3",
            @"C:\Music\Track2.flac",
        };

        var content = PlaylistWriter.ToM3u8FromPaths(paths);

        Assert.StartsWith("#EXTM3U", content);
        Assert.Contains("#EXTINF:-1,Track1", content);
        Assert.Contains(@"C:\Music\Track1.mp3", content);
        Assert.Contains("#EXTINF:-1,Track2", content);
        Assert.Contains(@"C:\Music\Track2.flac", content);
    }

    [Fact]
    public void Write_And_PlaylistScannerParsePaths_RoundTrips()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track1 = Path.Combine(dir.FullName, "track1.mp3");
            var track2 = Path.Combine(dir.FullName, "track2.mp3");
            File.WriteAllBytes(track1, []);
            File.WriteAllBytes(track2, []);

            var songs = new List<Song>
            {
                LibraryScanner.ReadSong(track1),
                LibraryScanner.ReadSong(track2),
            };

            var playlistFile = Path.Combine(dir.FullName, "output.m3u8");
            PlaylistWriter.Write(playlistFile, songs);

            Assert.True(File.Exists(playlistFile));

            var parsedPaths = PlaylistScanner.ParsePaths(playlistFile);
            Assert.Equal(2, parsedPaths.Count);
            Assert.Equal(Path.GetFullPath(track1), parsedPaths[0]);
            Assert.Equal(Path.GetFullPath(track2), parsedPaths[1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Append_AddsTrackToExistingM3u8()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track1 = Path.Combine(dir.FullName, "track1.mp3");
            var track2 = Path.Combine(dir.FullName, "track2.mp3");
            File.WriteAllBytes(track1, []);
            File.WriteAllBytes(track2, []);

            var song1 = LibraryScanner.ReadSong(track1);
            var song2 = LibraryScanner.ReadSong(track2);

            var playlistFile = Path.Combine(dir.FullName, "output.m3u8");
            PlaylistWriter.Write(playlistFile, [song1]);

            PlaylistWriter.Append(playlistFile, song2);

            var parsedPaths = PlaylistScanner.ParsePaths(playlistFile);
            Assert.Equal(2, parsedPaths.Count);
            Assert.Equal(Path.GetFullPath(track1), parsedPaths[0]);
            Assert.Equal(Path.GetFullPath(track2), parsedPaths[1]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Write_EmptyPlaylist_CreatesValidM3u8Header()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var playlistFile = Path.Combine(dir.FullName, "empty.m3u8");
            PlaylistWriter.Write(playlistFile, []);

            Assert.True(File.Exists(playlistFile));
            var text = File.ReadAllText(playlistFile);
            Assert.StartsWith("#EXTM3U", text.Trim());

            var paths = PlaylistScanner.ParsePaths(playlistFile);
            Assert.Empty(paths);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Write_ReorderedSongs_PreservesNewOrder()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track1 = Path.Combine(dir.FullName, "track1.mp3");
            var track2 = Path.Combine(dir.FullName, "track2.mp3");
            var track3 = Path.Combine(dir.FullName, "track3.mp3");
            File.WriteAllBytes(track1, []);
            File.WriteAllBytes(track2, []);
            File.WriteAllBytes(track3, []);

            var song1 = LibraryScanner.ReadSong(track1);
            var song2 = LibraryScanner.ReadSong(track2);
            var song3 = LibraryScanner.ReadSong(track3);

            var playlistFile = Path.Combine(dir.FullName, "ordered.m3u8");
            // Initial order: 1, 2, 3
            PlaylistWriter.Write(playlistFile, [song1, song2, song3]);

            // Reorder: 3, 1, 2
            PlaylistWriter.Write(playlistFile, [song3, song1, song2]);

            var parsed = PlaylistScanner.ParsePaths(playlistFile);
            Assert.Equal(3, parsed.Count);
            Assert.Equal(Path.GetFullPath(track3), parsed[0]);
            Assert.Equal(Path.GetFullPath(track1), parsed[1]);
            Assert.Equal(Path.GetFullPath(track2), parsed[2]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
