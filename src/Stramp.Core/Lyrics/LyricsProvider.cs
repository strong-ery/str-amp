using Stramp.Core.Models;

namespace Stramp.Core.Lyrics;

/// <summary>
/// Finds a track's lyrics, in the order the user would expect: an .lrc they put beside the file
/// always wins, then whatever LRCLIB told us last time, and only then the network.
/// </summary>
public sealed class LyricsProvider : IDisposable
{
    private readonly LyricsCache _cache;
    private readonly LrcLibClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    public LyricsProvider(LyricsCache? cache = null, LrcLibClient? client = null)
    {
        _cache = cache ?? new LyricsCache();
        _ownsClient = client is null;
        _client = client ?? new LrcLibClient();
    }

    /// <param name="allowOnline">False keeps the lookup entirely local — no request is made.</param>
    /// <param name="forceRefresh">Discards what LRCLIB said last time and asks it again.</param>
    public async Task<LyricsResult> GetAsync(
        Song song,
        bool allowOnline,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        var local = await Task.Run(() => LyricsLoader.Load(song.Path), ct);
        if (local.HasLines)
            return new LyricsResult(local, LyricsOrigin.LocalFile);

        ct.ThrowIfCancellationRequested();

        if (forceRefresh)
        {
            _cache.Forget(song.Path);
        }
        else if (ReadCached(song.Path) is { } cached)
        {
            return cached;
        }

        if (!allowOnline)
            return LyricsResult.Empty;

        return await DownloadAsync(song, ct);
    }

    private LyricsResult? ReadCached(string audioPath)
    {
        if (_cache.ReadLyrics(audioPath) is { } text)
        {
            var document = LrcParser.Parse(text);
            if (document.HasLines)
                return new LyricsResult(document, LyricsOrigin.Cache);
        }

        if (_cache.TryReadMiss(audioPath, out var miss))
        {
            return new LyricsResult(
                LyricsDocument.Empty,
                miss == LyricsCache.Miss.Instrumental ? LyricsOrigin.Instrumental : LyricsOrigin.None);
        }

        return null;
    }

    private async Task<LyricsResult> DownloadAsync(Song song, CancellationToken ct)
    {
        LrcLibTrack? track;
        try
        {
            track = await _client.FindAsync(song, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Offline, DNS down, LRCLIB having a bad day: none of that is an answer about the
            // track, so nothing is cached and the next play asks again.
            return new LyricsResult(LyricsDocument.Empty, LyricsOrigin.Unavailable);
        }

        if (track?.BestLyrics is { } lyrics)
        {
            var document = LrcParser.Parse(lyrics);
            if (document.HasLines)
            {
                _cache.WriteLyrics(song.Path, lyrics);
                return new LyricsResult(document, LyricsOrigin.LrcLib);
            }
        }

        var instrumental = track?.Instrumental == true;
        _cache.WriteMiss(song.Path, instrumental ? LyricsCache.Miss.Instrumental : LyricsCache.Miss.NotFound);
        return new LyricsResult(
            LyricsDocument.Empty, instrumental ? LyricsOrigin.Instrumental : LyricsOrigin.None);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_ownsClient)
            _client.Dispose();
    }
}
