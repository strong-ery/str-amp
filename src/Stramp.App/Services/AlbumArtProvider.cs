using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CoreArtCache = Stramp.Core.Library.ArtCache;

namespace Stramp.App.Services;

/// <summary>
/// UI-facing wrapper around Stramp.Core's ArtCache: reads/caches raw embedded-art bytes there,
/// and additionally caches decoded Avalonia Bitmaps here so repeated UI refreshes (e.g. re-showing
/// a queue row) don't re-decode the image every time.
/// </summary>
public sealed class AlbumArtProvider
{
    private readonly CoreArtCache _byteCache = new();
    private readonly ConcurrentDictionary<string, Bitmap?> _bitmapCache = new();

    /// <summary>Fetches art for `path` on a background thread and invokes `callback` on the UI thread.</summary>
    public void GetArtAsync(string path, Action<Bitmap?> callback)
    {
        if (_bitmapCache.TryGetValue(path, out var cached))
        {
            callback(cached);
            return;
        }

        Task.Run(() =>
        {
            if (!_byteCache.TryGet(path, out var bytes))
            {
                bytes = CoreArtCache.ReadEmbeddedArt(path);
                _byteCache.Put(path, bytes);
            }

            var bitmap = DecodeSafely(bytes);
            _bitmapCache[path] = bitmap;
            Dispatcher.UIThread.Post(() => callback(bitmap));
        });
    }

    public void Invalidate(string path)
    {
        _byteCache.Invalidate(path);
        _bitmapCache.TryRemove(path, out _);
    }

    private static Bitmap? DecodeSafely(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }
}
