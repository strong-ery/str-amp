using Avalonia;
using Avalonia.Media.Imaging;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    private const int CollageSize = 192;

    /// <summary>Finished collages by the songs they were built from, so rebuilding the source
    /// list (which happens after every playlist edit) doesn't redo work that hasn't changed.</summary>
    private readonly Dictionary<string, Bitmap?> _coverCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Builds a playlist's thumbnail the first time something shows it: a 2x2 collage of
    /// its album covers, or the single cover when there are fewer than four.</summary>
    public void LoadSourceCover(LibrarySourceRow? source)
    {
        if (source is not { HasCover: true } || source.CoverRequested)
            return;

        source.CoverRequested = true;
        var candidates = source.CoverCandidatePaths();
        if (candidates.Count == 0)
            return;

        var key = string.Join('|', candidates);
        if (_coverCache.TryGetValue(key, out var cached))
        {
            source.Cover = cached;
            return;
        }

        _ = BuildSourceCoverAsync(source, candidates, key);
    }

    private async Task BuildSourceCoverAsync(
        LibrarySourceRow source, IReadOnlyList<string> candidates, string key)
    {
        // Thumbnails are only cached while "pinned" for a visible row, so each is requested here
        // like a row would, drawn into the collage, then released. The collage is its own bitmap
        // and outlives the cache entries it came from.
        var requests = candidates.Select(path =>
        {
            var result = new TaskCompletionSource<Bitmap?>();
            _artProvider.GetThumbnailAsync(path, bitmap => result.TrySetResult(bitmap));
            return result.Task;
        }).ToArray();

        // A request that gets superseded never calls back, so don't wait on one forever.
        var allDone = Task.WhenAll(requests);
        await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(15)));

        try
        {
            // A collage missing some covers would differ from the finished one, and the point is
            // that it never changes: leave it blank and try again the next time it is shown.
            if (!allDone.IsCompletedSuccessfully)
            {
                source.CoverRequested = false;
                return;
            }

            var covers = requests
                .Where(task => task.IsCompletedSuccessfully && task.Result is not null)
                .Select(task => task.Result!)
                .ToList();

            var collage = ComposeCollage(covers);
            _coverCache[key] = collage;
            source.Cover = collage;
        }
        finally
        {
            foreach (var path in candidates)
                _artProvider.ReleaseThumbnail(path);
        }
    }

    private static Bitmap? ComposeCollage(IReadOnlyList<Bitmap> covers)
    {
        if (covers.Count == 0)
            return null;

        try
        {
            var target = new RenderTargetBitmap(new PixelSize(CollageSize, CollageSize));
            using var ctx = target.CreateDrawingContext();

            if (covers.Count < LibrarySourceRow.CollageCovers)
            {
                DrawCropped(ctx, covers[0], new Rect(0, 0, CollageSize, CollageSize));
                return target;
            }

            const int half = CollageSize / 2;
            for (var index = 0; index < LibrarySourceRow.CollageCovers; index++)
                DrawCropped(ctx, covers[index],
                    new Rect(index % 2 * half, index / 2 * half, half, half));
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Draws the centre square of `image` into `destination`, so a non-square cover is
    /// cropped rather than squashed.</summary>
    private static void DrawCropped(Avalonia.Media.DrawingContext ctx, Bitmap image, Rect destination)
    {
        var side = Math.Min(image.Size.Width, image.Size.Height);
        var source = new Rect(
            (image.Size.Width - side) / 2, (image.Size.Height - side) / 2, side, side);
        ctx.DrawImage(image, source, destination);
    }
}
