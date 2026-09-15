using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CoreArtCache = Stramp.Core.Library.ArtCache;

namespace Stramp.App.Services;

/// <summary>
/// Supplies full artwork for the now-playing view and small decoded thumbnails for virtualized
/// list rows. Both caches are bounded and duplicate requests share the same decode operation.
/// </summary>
public sealed class AlbumArtProvider : IDisposable
{
    private const int ThumbnailDecodeWidth = 96;
    private const int ThumbnailCacheSize = 192;
    private const int FullArtCacheSize = 3;
    private const int ConcurrentDecodes = 4;

    private readonly CoreArtCache _byteCache = new();
    private readonly BitmapLruCache _thumbnailCache = new(ThumbnailCacheSize, disposeOnEviction: true);
    private readonly BitmapLruCache _fullArtCache = new(FullArtCacheSize);
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _thumbnailInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _thumbnailCancellation =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> _fullArtInFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeSlots = new(ConcurrentDecodes);
    private bool _disposed;

    /// <summary>Fetches original-resolution art for the large now-playing cover.</summary>
    public void GetArtAsync(string path, Action<Bitmap?> callback) =>
        DeliverAsync(path, callback, thumbnail: false, CancellationToken.None);

    /// <summary>Fetches a high-DPI 96px thumbnail for a visible library or queue row.</summary>
    public void GetThumbnailAsync(string path, Action<Bitmap?> callback)
    {
        _thumbnailCache.Pin(path);
        var cancellation = _thumbnailCancellation.GetOrAdd(
            path, static _ => new CancellationTokenSource());
        DeliverAsync(path, callback, thumbnail: true, cancellation.Token);
    }

    /// <summary>Releases a visible row's ownership so an old thumbnail can be evicted safely.</summary>
    public void ReleaseThumbnail(string path)
    {
        if (!_thumbnailCache.Unpin(path))
            return;

        // Remove the lazy request first so a row that becomes visible again gets fresh work rather
        // than inheriting a canceled waiter. The old task remains safely owned by its awaiter.
        _thumbnailInFlight.TryRemove(path, out _);
        if (_thumbnailCancellation.TryRemove(path, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    private async void DeliverAsync(
        string path, Action<Bitmap?> callback, bool thumbnail, CancellationToken ct)
    {
        if (_disposed)
            return;

        var cache = thumbnail ? _thumbnailCache : _fullArtCache;
        if (cache.TryGet(path, out var cached))
        {
            callback(cached);
            return;
        }

        var inFlight = thumbnail ? _thumbnailInFlight : _fullArtInFlight;
        var request = inFlight.GetOrAdd(path, requestedPath =>
            new Lazy<Task<Bitmap?>>(
                () => LoadAndCacheAsync(requestedPath, cache, thumbnail, ct),
                LazyThreadSafetyMode.ExecutionAndPublication));
        Bitmap? bitmap;
        try
        {
            bitmap = await request.Value;
        }
        finally
        {
            if (inFlight.TryGetValue(path, out var current) && ReferenceEquals(current, request))
                inFlight.TryRemove(path, out _);
        }
        if (_disposed || ct.IsCancellationRequested ||
            (thumbnail && !_thumbnailCache.IsPinned(path)))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && !ct.IsCancellationRequested &&
                (!thumbnail || _thumbnailCache.IsPinned(path)))
                callback(bitmap);
        });
    }

    private async Task<Bitmap?> LoadAndCacheAsync(
        string path,
        BitmapLruCache cache,
        bool thumbnail,
        CancellationToken ct)
    {
        var enteredDecodeSlot = false;
        try
        {
            await _decodeSlots.WaitAsync(ct);
            enteredDecodeSlot = true;
            if (_disposed || ct.IsCancellationRequested ||
                (thumbnail && !cache.IsPinned(path)))
                return null;

            var bitmap = await Task.Run(() =>
            {
                if (!_byteCache.TryGet(path, out var bytes))
                {
                    bytes = CoreArtCache.ReadEmbeddedArt(path);
                    _byteCache.Put(path, bytes);
                }

                return DecodeSafely(bytes, thumbnail);
            });

            if (_disposed || ct.IsCancellationRequested ||
                (thumbnail && !cache.IsPinned(path)))
            {
                bitmap?.Dispose();
                return null;
            }

            cache.Put(path, bitmap);
            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            cache.Put(path, null);
            return null;
        }
        finally
        {
            if (enteredDecodeSlot)
                _decodeSlots.Release();
        }
    }

    public void Invalidate(string path)
    {
        _byteCache.Invalidate(path);
        _thumbnailCache.Remove(path);
        _fullArtCache.Remove(path);
    }

    private static Bitmap? DecodeSafely(byte[]? bytes, bool thumbnail)
    {
        if (bytes is null || bytes.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(bytes);
            return thumbnail
                ? Bitmap.DecodeToWidth(stream, ThumbnailDecodeWidth, BitmapInterpolationMode.HighQuality)
                : new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var cancellation in _thumbnailCancellation.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        _thumbnailCancellation.Clear();
        _thumbnailInFlight.Clear();
        _thumbnailCache.Dispose();
        _fullArtCache.Dispose();
    }

    private sealed class BitmapLruCache(int capacity, bool disposeOnEviction = false) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, CacheEntry> _entries =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly LinkedList<string> _order = new();
        private bool _disposed;

        public void Pin(string path)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;

                if (_entries.TryGetValue(path, out var existing))
                {
                    existing.PinCount++;
                    _order.Remove(existing.Node);
                    _order.AddLast(existing.Node);
                    return;
                }

                var node = _order.AddLast(path);
                _entries[path] = new CacheEntry(bitmap: null, node)
                {
                    PinCount = 1,
                    IsLoaded = false,
                };
            }
        }

        /// <returns>True when no visible row still owns this path.</returns>
        public bool Unpin(string path)
        {
            lock (_gate)
            {
                if (_disposed || !_entries.TryGetValue(path, out var entry))
                    return true;
                entry.PinCount = Math.Max(0, entry.PinCount - 1);
                Trim();
                return entry.PinCount == 0;
            }
        }

        public bool IsPinned(string path)
        {
            lock (_gate)
                return !_disposed && _entries.TryGetValue(path, out var entry) && entry.PinCount > 0;
        }

        public bool TryGet(string path, out Bitmap? bitmap)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    bitmap = null;
                    return false;
                }

                if (!_entries.TryGetValue(path, out var entry))
                {
                    bitmap = null;
                    return false;
                }

                if (!entry.IsLoaded)
                {
                    bitmap = null;
                    return false;
                }

                _order.Remove(entry.Node);
                _order.AddLast(entry.Node);
                bitmap = entry.Bitmap;
                return true;
            }
        }

        public void Put(string path, Bitmap? bitmap)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    bitmap?.Dispose();
                    return;
                }

                if (_entries.TryGetValue(path, out var existing))
                {
                    existing.Bitmap = bitmap;
                    existing.IsLoaded = true;
                    _order.Remove(existing.Node);
                    _order.AddLast(existing.Node);
                    Trim();
                    return;
                }

                var node = _order.AddLast(path);
                _entries[path] = new CacheEntry(bitmap, node) { IsLoaded = true };
                Trim();
            }
        }

        private void Trim()
        {
            while (_entries.Count > capacity)
            {
                var candidate = _order.First;
                while (candidate is not null &&
                       (!_entries[candidate.Value].IsLoaded || _entries[candidate.Value].PinCount > 0))
                    candidate = candidate.Next;

                if (candidate is null)
                    return; // More visible/pending entries than the cap; trim as rows clear.

                var entry = _entries[candidate.Value];
                _order.Remove(candidate);
                _entries.Remove(candidate.Value);
                if (disposeOnEviction)
                    entry.Bitmap?.Dispose();
            }
        }

        public void Remove(string path)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                if (!_entries.Remove(path, out var entry))
                    return;
                _order.Remove(entry.Node);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                foreach (var bitmap in _entries.Values.Select(entry => entry.Bitmap).OfType<Bitmap>())
                    bitmap.Dispose();
                _entries.Clear();
                _order.Clear();
            }
        }

        private sealed class CacheEntry(Bitmap? bitmap, LinkedListNode<string> node)
        {
            public Bitmap? Bitmap { get; set; } = bitmap;
            public LinkedListNode<string> Node { get; } = node;
            public int PinCount { get; set; }
            public bool IsLoaded { get; set; }
        }
    }
}
