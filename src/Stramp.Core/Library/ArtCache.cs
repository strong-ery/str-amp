namespace Stramp.Core.Library;

/// <summary>Reads embedded cover art and caches the raw bytes (LRU-ish, bounded size).</summary>
public sealed class ArtCache
{
    private readonly int _maxSize;
    private readonly Dictionary<string, byte[]?> _entries = new();
    private readonly LinkedList<string> _order = new();
    private readonly object _lock = new();

    public ArtCache(int maxSize = 120) => _maxSize = maxSize;

    /// <summary>Returns cached art bytes (possibly null meaning "known to have no art"), or false if not cached yet.</summary>
    public bool TryGet(string path, out byte[]? artBytes)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(path, out artBytes);
        }
    }

    public void Put(string path, byte[]? artBytes)
    {
        lock (_lock)
        {
            _entries[path] = artBytes;
            _order.AddLast(path);
            while (_order.Count > _maxSize)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _entries.Remove(oldest);
            }
        }
    }

    public void Invalidate(string path)
    {
        lock (_lock)
        {
            _entries.Remove(path);
            _order.Remove(path);
        }
    }

    /// <summary>Reads the first embedded picture for a track, or null if it has none / can't be read.</summary>
    public static byte[]? ReadEmbeddedArt(string path)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            var picture = tagFile.Tag.Pictures.FirstOrDefault();
            return picture?.Data?.Data;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Removes all embedded pictures from the file's tags.</summary>
    public static bool StripArt(string path)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            tagFile.Tag.Pictures = [];
            tagFile.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Replaces embedded art with the given image bytes.</summary>
    public static bool EmbedArt(string path, byte[] imageBytes, string mimeType = "image/jpeg")
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            tagFile.Tag.Pictures =
            [
                new TagLib.Picture
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mimeType,
                    Data = imageBytes,
                }
            ];
            tagFile.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
