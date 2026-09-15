using System.Net;
using System.Text;
using Stramp.Core.Lyrics;
using Stramp.Core.Models;

namespace Stramp.Core.Tests;

public class LrcLibTests
{
    private static Song TrackWith(string directory, double durationSeconds = 180, string album = "Album") =>
        new()
        {
            Path = Path.Combine(directory, "Artist - Title.mp3"),
            Title = "Title",
            Artist = "Artist",
            Album = album,
            Duration = TimeSpan.FromSeconds(durationSeconds),
        };

    private static string TrackJson(
        string? synced = null, string? plain = null, bool instrumental = false, double duration = 180) =>
        $$"""
        {
            "id": 1,
            "trackName": "Title",
            "artistName": "Artist",
            "albumName": "Album",
            "duration": {{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
            "instrumental": {{(instrumental ? "true" : "false")}},
            "plainLyrics": {{Quote(plain)}},
            "syncedLyrics": {{Quote(synced)}}
        }
        """;

    private static string Quote(string? value) =>
        value is null ? "null" : System.Text.Json.JsonSerializer.Serialize(value);

    // ── Client ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAsync_UsesTheExactMatch_WithoutSearching()
    {
        using var stub = new StubHandler(_ => Json(TrackJson(synced: "[00:01.00]Hello")));
        using var client = new LrcLibClient(stub.CreateClient());

        var track = await client.FindAsync(TrackWith("C:/music"));

        Assert.Equal("[00:01.00]Hello", track!.SyncedLyrics);
        Assert.Single(stub.Requests);
        Assert.StartsWith("/api/get?", stub.Requests[0]);
    }

    [Fact]
    public async Task FindAsync_SendsTheTrackSignature()
    {
        using var stub = new StubHandler(_ => Json(TrackJson(plain: "Hello")));
        using var client = new LrcLibClient(stub.CreateClient());

        await client.FindAsync(TrackWith("C:/music", durationSeconds: 183.4));

        var request = stub.Requests[0];
        Assert.Contains("artist_name=Artist", request);
        Assert.Contains("track_name=Title", request);
        Assert.Contains("album_name=Album", request);
        Assert.Contains("duration=183", request);
    }

    [Fact]
    public async Task FindAsync_FallsBackToSearch_WhenTheSignatureMisses()
    {
        using var stub = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/get")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json($"[{TrackJson(plain: "Plain only")}]"));
        using var client = new LrcLibClient(stub.CreateClient());

        var track = await client.FindAsync(TrackWith("C:/music"));

        Assert.Equal("Plain only", track!.PlainLyrics);
        Assert.Equal(2, stub.Requests.Count);
        Assert.StartsWith("/api/search?", stub.Requests[1]);
    }

    [Fact]
    public async Task FindAsync_PrefersASyncedCandidate_OverACloserDuration()
    {
        var candidates =
            $"[{TrackJson(plain: "Plain", duration: 180)},{TrackJson(synced: "[00:01.00]Synced", duration: 182)}]";
        using var stub = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/get")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json(candidates));
        using var client = new LrcLibClient(stub.CreateClient());

        var track = await client.FindAsync(TrackWith("C:/music", durationSeconds: 180));

        Assert.Equal("[00:01.00]Synced", track!.SyncedLyrics);
    }

    /// <summary>A length that far off is a different recording, not a worse match for this one.</summary>
    [Fact]
    public async Task FindAsync_RejectsCandidatesOfAVeryDifferentLength()
    {
        using var stub = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/get")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json($"[{TrackJson(synced: "[00:01.00]Wrong cut", duration: 400)}]"));
        using var client = new LrcLibClient(stub.CreateClient());

        Assert.Null(await client.FindAsync(TrackWith("C:/music", durationSeconds: 180)));
    }

    [Fact]
    public async Task FindAsync_ReturnsNull_WhenTheSearchComesBackEmpty()
    {
        using var stub = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/get")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Json("[]"));
        using var client = new LrcLibClient(stub.CreateClient());

        Assert.Null(await client.FindAsync(TrackWith("C:/music")));
    }

    [Fact]
    public async Task FindAsync_KeepsAnInstrumental_AsAnAnswer()
    {
        using var stub = new StubHandler(_ => Json(TrackJson(instrumental: true)));
        using var client = new LrcLibClient(stub.CreateClient());

        var track = await client.FindAsync(TrackWith("C:/music"));

        Assert.True(track!.Instrumental);
        Assert.Null(track.BestLyrics);
    }

    /// <summary>
    /// LRCLIB refuses to index anything over an hour, answering the signature lookup with a 400
    /// rather than a 404. That still means "not a match", so the search must get its turn.
    /// </summary>
    [Fact]
    public async Task FindAsync_FallsBackToSearch_WhenTheSignatureIsRejectedOutright()
    {
        using var stub = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/get")
            ? new HttpResponseMessage(HttpStatusCode.BadRequest)
            : Json($"[{TrackJson(synced: "[00:01.00]Long set", duration: 5000)}]"));
        using var client = new LrcLibClient(stub.CreateClient());

        var track = await client.FindAsync(TrackWith("C:/music", durationSeconds: 5000));

        Assert.Equal("[00:01.00]Long set", track!.SyncedLyrics);
    }

    [Fact]
    public async Task FindAsync_OmitsADurationLrcLibWouldReject()
    {
        using var stub = new StubHandler(_ => Json(TrackJson(plain: "Hello")));
        using var client = new LrcLibClient(stub.CreateClient());

        await client.FindAsync(TrackWith("C:/music", durationSeconds: 5000));

        Assert.DoesNotContain("duration=", stub.Requests[0]);
    }

    [Fact]
    public async Task FindAsync_PropagatesServerErrors()
    {
        using var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new LrcLibClient(stub.CreateClient());

        await Assert.ThrowsAsync<HttpRequestException>(() => client.FindAsync(TrackWith("C:/music")));
    }

    // ── Cache ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cache_RoundTripsLyricsAndMisses()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new LyricsCache(dir.FullName);
            var track = Path.Combine(dir.FullName, "song.mp3");

            Assert.Null(cache.ReadLyrics(track));
            Assert.False(cache.TryReadMiss(track, out _));

            cache.WriteLyrics(track, "[00:01.00]Hello");
            Assert.Equal("[00:01.00]Hello", cache.ReadLyrics(track));

            // Writing a miss replaces the hit, so a track is never both at once.
            cache.WriteMiss(track, LyricsCache.Miss.Instrumental);
            Assert.Null(cache.ReadLyrics(track));
            Assert.True(cache.TryReadMiss(track, out var miss));
            Assert.Equal(LyricsCache.Miss.Instrumental, miss);

            cache.Forget(track);
            Assert.False(cache.TryReadMiss(track, out _));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Cache_KeepsDifferentTracksApart()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var cache = new LyricsCache(dir.FullName);
            cache.WriteLyrics(Path.Combine(dir.FullName, "a.mp3"), "A");
            cache.WriteLyrics(Path.Combine(dir.FullName, "b.mp3"), "B");

            Assert.Equal("A", cache.ReadLyrics(Path.Combine(dir.FullName, "a.mp3")));
            Assert.Equal("B", cache.ReadLyrics(Path.Combine(dir.FullName, "b.mp3")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ── Provider ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Provider_PrefersTheLocalFile_AndNeverAsksLrcLib()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = TrackWith(dir.FullName);
            File.WriteAllText(Path.ChangeExtension(song.Path, ".lrc"), "[00:01.00]From disk");

            using var stub = new StubHandler(_ => Json(TrackJson(synced: "[00:01.00]From lrclib")));
            using var provider = NewProvider(dir, stub);

            var result = await provider.GetAsync(song, allowOnline: true);

            Assert.Equal(LyricsOrigin.LocalFile, result.Origin);
            Assert.Equal("From disk", result.Document.Lines.Single().Text);
            Assert.Empty(stub.Requests);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Provider_DownloadsThenServesTheSecondLookupFromCache()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = TrackWith(dir.FullName);
            using var stub = new StubHandler(_ => Json(TrackJson(synced: "[00:01.00]Downloaded")));
            using var provider = NewProvider(dir, stub);

            var first = await provider.GetAsync(song, allowOnline: true);
            Assert.Equal(LyricsOrigin.LrcLib, first.Origin);
            Assert.True(first.Document.IsSynced);

            var second = await provider.GetAsync(song, allowOnline: true);
            Assert.Equal(LyricsOrigin.Cache, second.Origin);
            Assert.Equal("Downloaded", second.Document.Lines.Single().Text);
            Assert.Single(stub.Requests);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Provider_RemembersAMiss_RatherThanReAskingEveryPlay()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = TrackWith(dir.FullName);
            using var stub = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/get")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json("[]"));
            using var provider = NewProvider(dir, stub);

            Assert.Equal(LyricsOrigin.None, (await provider.GetAsync(song, allowOnline: true)).Origin);
            var requestsAfterFirst = stub.Requests.Count;

            Assert.Equal(LyricsOrigin.None, (await provider.GetAsync(song, allowOnline: true)).Origin);
            Assert.Equal(requestsAfterFirst, stub.Requests.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Provider_ReportsAnInstrumental()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            using var stub = new StubHandler(_ => Json(TrackJson(instrumental: true)));
            using var provider = NewProvider(dir, stub);

            var result = await provider.GetAsync(TrackWith(dir.FullName), allowOnline: true);

            Assert.Equal(LyricsOrigin.Instrumental, result.Origin);
            Assert.False(result.HasLines);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Being offline says nothing about the track, so it must not be cached as a miss.</summary>
    [Fact]
    public async Task Provider_DoesNotCacheAFailedRequest()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = TrackWith(dir.FullName);
            var cache = new LyricsCache(dir.FullName);
            using var stub = new StubHandler(_ => throw new HttpRequestException("offline"));
            using var provider = new LyricsProvider(cache, new LrcLibClient(stub.CreateClient()));

            var result = await provider.GetAsync(song, allowOnline: true);

            Assert.Equal(LyricsOrigin.Unavailable, result.Origin);
            Assert.False(cache.TryReadMiss(song.Path, out _));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Provider_StaysOffline_WhenLookupIsTurnedOff()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            using var stub = new StubHandler(_ => Json(TrackJson(synced: "[00:01.00]Downloaded")));
            using var provider = NewProvider(dir, stub);

            var result = await provider.GetAsync(TrackWith(dir.FullName), allowOnline: false);

            Assert.Equal(LyricsOrigin.None, result.Origin);
            Assert.Empty(stub.Requests);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Provider_ForceRefresh_GoesBackToLrcLib()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var song = TrackWith(dir.FullName);
            using var stub = new StubHandler(_ => Json(TrackJson(synced: "[00:01.00]Downloaded")));
            using var provider = NewProvider(dir, stub);

            await provider.GetAsync(song, allowOnline: true);
            var result = await provider.GetAsync(song, allowOnline: true, forceRefresh: true);

            Assert.Equal(LyricsOrigin.LrcLib, result.Origin);
            Assert.Equal(2, stub.Requests.Count);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static LyricsProvider NewProvider(DirectoryInfo cacheDir, StubHandler stub) =>
        new(new LyricsCache(cacheDir.FullName), new LrcLibClient(stub.CreateClient()));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>Answers requests from a canned function and records what was asked for.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public HttpClient CreateClient() =>
            new(this, disposeHandler: false) { BaseAddress = new Uri("https://lrclib.net/") };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }
}
