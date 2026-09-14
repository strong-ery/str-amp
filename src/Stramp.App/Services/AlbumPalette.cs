using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Stramp.App.Services;

/// <summary>Two accents plus an independently selected background color from album artwork.</summary>
public readonly record struct ArtPalette(Color Primary, Color Secondary, Color Background);

/// <summary>
/// Derives theme colors from album art: downsamples the cover, buckets pixels by hue weighted by
/// how vivid they are, then picks the strongest hue plus a clearly different second one. Results
/// are pushed into a saturation/lightness range that stays legible on a dark UI.
/// </summary>
public static class AlbumPalette
{
    private const int InferredSampleSize = 28;
    private const int DirectSampleSize = 32;
    private const int DirectHueBuckets = 36;
    private const int DirectHueWindow = 2;
    private const int HueBuckets = 24;

    public static ArtPalette? ExtractInferred(Bitmap source)
    {
        var pixels = SamplePixels(source, InferredSampleSize);
        if (pixels is null)
            return null;

        var weights = new double[HueBuckets];
        var hueSums = new double[HueBuckets];
        var rowSums = new double[HueBuckets];
        var totalVivid = 0.0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // RenderTargetBitmap gives premultiplied BGRA.
            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            var a = pixels[i + 3];
            if (a < 8)
                continue;

            var (h, s, l) = ToHsl(r / 255, g / 255, b / 255);

            // Ignore near-black, near-white and washed-out pixels: they carry no usable hue.
            if (s < 0.18 || l < 0.08 || l > 0.94)
                continue;

            var weight = s * (1 - Math.Abs(l - 0.5) * 1.2);
            if (weight <= 0)
                continue;

            var bucket = (int)(h / 360.0 * HueBuckets) % HueBuckets;
            weights[bucket] += weight;
            hueSums[bucket] += h * weight;
            rowSums[bucket] += (i / 4 / InferredSampleSize) * weight;
            totalVivid += weight;
        }

        if (totalVivid < 1.0)
            return null; // essentially greyscale artwork — leave the theme alone

        var primaryBucket = IndexOfMax(weights);
        var primaryHue = hueSums[primaryBucket] / weights[primaryBucket];

        // Second color: the strongest bucket that is a clearly different hue, else a shifted primary.
        var secondaryBucket = -1;
        var bestSecondary = 0.0;
        for (var i = 0; i < HueBuckets; i++)
        {
            if (weights[i] <= 0 || HueDistance(i, primaryBucket) < 3)
                continue;
            if (weights[i] > bestSecondary)
            {
                bestSecondary = weights[i];
                secondaryBucket = i;
            }
        }

        var secondaryHue = secondaryBucket >= 0 && bestSecondary > totalVivid * 0.06
            ? hueSums[secondaryBucket] / weights[secondaryBucket]
            : (primaryHue + 48) % 360;

        var primaryColor = FromHsl(primaryHue, 0.72, 0.63);
        var secondaryColor = FromHsl(secondaryHue, 0.76, 0.66);

        if (secondaryBucket >= 0)
        {
            var primaryRow = rowSums[primaryBucket] / weights[primaryBucket];
            var secondaryRow = rowSums[secondaryBucket] / weights[secondaryBucket];
            if (secondaryRow < primaryRow)
                (primaryColor, secondaryColor) = (secondaryColor, primaryColor);
        }

        var background = ExtractBackgroundColor(
            pixels,
            (int)(primaryHue / 360 * DirectHueBuckets) % DirectHueBuckets,
            (int)(secondaryHue / 360 * DirectHueBuckets) % DirectHueBuckets,
            primaryColor,
            secondaryColor,
            InferredSampleSize);
        return new ArtPalette(primaryColor, secondaryColor, background);
    }

    /// <summary>
    /// Searches inward from the top and bottom of a 32x32 copy for real color, ignoring black and
    /// white unless the whole cover is achromatic. The returned accents retain that direction.
    /// </summary>
    public static ArtPalette? ExtractDirect(Bitmap source)
    {
        var pixels = SamplePixels(source, DirectSampleSize);
        if (pixels is null)
            return null;

        var top = FindDirectionalHue(pixels, fromTop: true);
        var bottom = FindDirectionalHue(pixels, fromTop: false);
        if (top is null || bottom is null)
            return ExtractRgbDominants(pixels);

        var primary = ExtractDominantHueColor(
            pixels, top.Value.Hue, top.Value.StartRow, top.Value.EndRow);
        var secondary = ExtractDominantHueColor(
            pixels, bottom.Value.Hue, bottom.Value.StartRow, bottom.Value.EndRow);
        if (primary is null || secondary is null)
            return ExtractRgbDominants(pixels);

        var background = ExtractBackgroundColor(
            pixels, top.Value.Hue, bottom.Value.Hue, primary.Value.Color, secondary.Value.Color);
        return new ArtPalette(primary.Value.Color, secondary.Value.Color, background);
    }

    private static byte[]? SamplePixels(Bitmap source, int sampleSize)
    {
        try
        {
            var size = new PixelSize(sampleSize, sampleSize);
            using var target = new RenderTargetBitmap(size);
            using (var ctx = target.CreateDrawingContext())
                ctx.DrawImage(source, new Rect(0, 0, sampleSize, sampleSize));

            var stride = sampleSize * 4;
            var buffer = new byte[stride * sampleSize];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                target.CopyPixels(
                    new PixelRect(0, 0, sampleSize, sampleSize),
                    handle.AddrOfPinnedObject(),
                    buffer.Length,
                    stride);
            }
            finally
            {
                handle.Free();
            }

            return buffer;
        }
        catch
        {
            return null;
        }
    }

    private static int FindMostCommon(
        DirectColorBucket[] buckets, int excludedIndex, Color? distinctFrom)
    {
        const int minimumDistanceSquared = 48 * 48;
        var bestIndex = -1;
        var bestCount = 0;

        for (var i = 0; i < buckets.Length; i++)
        {
            ref var bucket = ref buckets[i];
            if (i == excludedIndex || bucket.Count <= bestCount)
                continue;

            if (distinctFrom is { } other)
            {
                var candidate = bucket.AverageColor;
                var dr = candidate.R - other.R;
                var dg = candidate.G - other.G;
                var db = candidate.B - other.B;
                if (dr * dr + dg * dg + db * db < minimumDistanceSquared)
                    continue;
            }

            bestIndex = i;
            bestCount = bucket.Count;
        }

        return bestIndex;
    }

    private static DirectionalHue? FindDirectionalHue(byte[] pixels, bool fromTop)
    {
        const int minimumColorPixels = 8;
        var initialDepth = (int)Math.Ceiling(DirectSampleSize * 0.30);
        var startRow = fromTop ? 0 : DirectSampleSize - initialDepth;
        var endRow = fromTop ? initialDepth : DirectSampleSize;

        while (true)
        {
            var support = BuildHueSupport(
                pixels, startRow, endRow, DirectSampleSize, out var colorPixels);
            if (colorPixels >= minimumColorPixels)
            {
                var hue = IndexOfMax(support);
                if (support[hue] > 0)
                    return new DirectionalHue(hue, startRow, endRow);
            }

            if (startRow == 0 && endRow == DirectSampleSize)
                return null;

            if (fromTop)
                endRow = Math.Min(DirectSampleSize, endRow + 1);
            else
                startRow = Math.Max(0, startRow - 1);
        }
    }

    private static double[] BuildHueSupport(
        byte[] pixels, int startRow, int endRow, int sampleSize, out int colorPixels)
    {
        var weights = new double[DirectHueBuckets];
        colorPixels = 0;

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var row = PixelRow(i, sampleSize);
            if (row < startRow || row >= endRow ||
                !TryReadDirectPixel(pixels, i, out var r, out var g, out var b))
                continue;

            var (h, s, l) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
            if (!IsDirectColor(s, l))
                continue;

            weights[(int)(h / 360 * DirectHueBuckets) % DirectHueBuckets]++;
            colorPixels++;
        }

        var support = new double[DirectHueBuckets];
        for (var hue = 0; hue < DirectHueBuckets; hue++)
        {
            for (var offset = -DirectHueWindow; offset <= DirectHueWindow; offset++)
                support[hue] += weights[(hue + offset + DirectHueBuckets) % DirectHueBuckets];
        }

        return support;
    }

    private static ArtPalette? ExtractRgbDominants(byte[] pixels)
    {
        var buckets = new DirectColorBucket[4096];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (!TryReadDirectPixel(pixels, i, out var r, out var g, out var b))
                continue;

            var index = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            buckets[index].Add(r, g, b, saturation: 0, PixelRow(i, DirectSampleSize));
        }

        var primaryIndex = FindMostCommon(buckets, excludedIndex: -1, distinctFrom: null);
        if (primaryIndex < 0)
            return null;

        var primary = buckets[primaryIndex].AverageColor;
        var secondaryIndex = FindMostCommon(buckets, primaryIndex, primary);
        if (secondaryIndex < 0)
            secondaryIndex = FindMostCommon(buckets, primaryIndex, distinctFrom: null);

        if (secondaryIndex < 0)
            return new ArtPalette(primary, primary, ToBackgroundColor(primary));

        var secondary = buckets[secondaryIndex].AverageColor;
        var backgroundSeedIndex = FindBackgroundRgbBucket(buckets, primary, secondary);
        var backgroundSeed = backgroundSeedIndex >= 0
            ? buckets[backgroundSeedIndex].AverageColor
            : primary;
        var background = ToBackgroundColor(backgroundSeed);

        return buckets[primaryIndex].AverageRow <= buckets[secondaryIndex].AverageRow
            ? new ArtPalette(primary, secondary, background)
            : new ArtPalette(secondary, primary, background);
    }

    private static LocatedColor? ExtractDominantHueColor(
        byte[] pixels,
        int selectedHue,
        int startRow = 0,
        int endRow = DirectSampleSize,
        int sampleSize = DirectSampleSize)
    {
        var buckets = new DirectColorBucket[4096];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var row = PixelRow(i, sampleSize);
            if (row < startRow || row >= endRow ||
                !TryReadDirectPixel(pixels, i, out var r, out var g, out var b))
                continue;

            var (h, s, l) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
            if (!IsDirectColor(s, l))
                continue;

            var hue = (int)(h / 360 * DirectHueBuckets) % DirectHueBuckets;
            if (DirectHueDistance(hue, selectedHue) > DirectHueWindow)
                continue;

            var index = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            buckets[index].Add(r, g, b, s, row);
        }

        var bestIndex = -1;
        var bestScore = 0.0;
        for (var index = 0; index < buckets.Length; index++)
        {
            var score = buckets[index].VisualDominance;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = index;
            }
        }

        return bestIndex >= 0
            ? new LocatedColor(buckets[bestIndex].AverageColor, buckets[bestIndex].AverageRow)
            : null;
    }

    private static Color ExtractBackgroundColor(
        byte[] pixels,
        int primaryHue,
        int secondaryHue,
        Color primary,
        Color secondary,
        int sampleSize = DirectSampleSize)
    {
        var support = BuildHueSupport(pixels, 0, sampleSize, sampleSize, out var colorPixels);
        var minimumSupport = Math.Max(2, colorPixels * 0.015);
        var backgroundHue = -1;
        var bestSupport = 0.0;

        for (var hue = 0; hue < DirectHueBuckets; hue++)
        {
            if (DirectHueDistance(hue, primaryHue) < 4 ||
                DirectHueDistance(hue, secondaryHue) < 4 ||
                support[hue] < minimumSupport || support[hue] <= bestSupport)
                continue;

            backgroundHue = hue;
            bestSupport = support[hue];
        }

        if (backgroundHue >= 0 &&
            ExtractDominantHueColor(
                pixels, backgroundHue, endRow: sampleSize, sampleSize: sampleSize) is { } backgroundColor)
            return ToBackgroundColor(backgroundColor.Color);

        var buckets = BuildRgbBuckets(pixels, sampleSize);
        var seedIndex = FindBackgroundRgbBucket(buckets, primary, secondary);
        return ToBackgroundColor(seedIndex >= 0 ? buckets[seedIndex].AverageColor : primary);
    }

    private static DirectColorBucket[] BuildRgbBuckets(byte[] pixels, int sampleSize)
    {
        var buckets = new DirectColorBucket[4096];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (!TryReadDirectPixel(pixels, i, out var r, out var g, out var b))
                continue;
            var (_, s, _) = ToHsl(r / 255.0, g / 255.0, b / 255.0);
            var index = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            buckets[index].Add(r, g, b, s, PixelRow(i, sampleSize));
        }
        return buckets;
    }

    private static int FindBackgroundRgbBucket(
        DirectColorBucket[] buckets, Color primary, Color secondary)
    {
        const int minimumDistanceSquared = 56 * 56;
        var bestIndex = -1;
        var bestCount = 0;
        for (var index = 0; index < buckets.Length; index++)
        {
            ref var bucket = ref buckets[index];
            if (bucket.Count <= bestCount)
                continue;

            var color = bucket.AverageColor;
            if (ColorDistanceSquared(color, primary) < minimumDistanceSquared ||
                ColorDistanceSquared(color, secondary) < minimumDistanceSquared)
                continue;

            bestIndex = index;
            bestCount = bucket.Count;
        }
        return bestIndex;
    }

    private static int ColorDistanceSquared(Color first, Color second)
    {
        var dr = first.R - second.R;
        var dg = first.G - second.G;
        var db = first.B - second.B;
        return dr * dr + dg * dg + db * db;
    }

    private static Color ToBackgroundColor(Color seed)
    {
        var (h, s, _) = ToHsl(seed.R / 255.0, seed.G / 255.0, seed.B / 255.0);
        if (s < 0.08)
            return Color.FromRgb(14, 15, 16);
        return FromHsl(h, Math.Clamp(s * 0.65, 0.18, 0.42), 0.055);
    }

    private static bool IsDirectColor(double saturation, double lightness) =>
        saturation >= 0.16 && lightness >= 0.08 && lightness <= 0.94;

    private static int PixelRow(int byteIndex, int sampleSize) => byteIndex / 4 / sampleSize;

    private static int DirectHueDistance(int first, int second)
    {
        var distance = Math.Abs(first - second);
        return Math.Min(distance, DirectHueBuckets - distance);
    }

    private static bool TryReadDirectPixel(
        byte[] pixels, int index, out byte red, out byte green, out byte blue)
    {
        var alpha = pixels[index + 3];
        if (alpha < 16)
        {
            red = green = blue = 0;
            return false;
        }

        blue = pixels[index];
        green = pixels[index + 1];
        red = pixels[index + 2];
        if (alpha < 255)
        {
            blue = (byte)Math.Min(255, blue * 255 / alpha);
            green = (byte)Math.Min(255, green * 255 / alpha);
            red = (byte)Math.Min(255, red * 255 / alpha);
        }

        return true;
    }

    private struct DirectColorBucket
    {
        private int _red;
        private int _green;
        private int _blue;
        private int _rowTotal;
        private double _saturation;
        public int Count { get; private set; }

        public readonly double VisualDominance => Count == 0
            ? 0
            : Count * Math.Pow(_saturation / Count, 3);

        public readonly double AverageRow => Count == 0 ? 0 : (double)_rowTotal / Count;

        public readonly Color AverageColor => Color.FromRgb(
            (byte)(_red / Count),
            (byte)(_green / Count),
            (byte)(_blue / Count));

        public void Add(byte red, byte green, byte blue, double saturation, int row)
        {
            _red += red;
            _green += green;
            _blue += blue;
            _rowTotal += row;
            _saturation += saturation;
            Count++;
        }
    }

    private readonly record struct LocatedColor(Color Color, double AverageRow);
    private readonly record struct DirectionalHue(int Hue, int StartRow, int EndRow);

    private static int HueDistance(int a, int b)
    {
        var diff = Math.Abs(a - b);
        return Math.Min(diff, HueBuckets - diff);
    }

    private static int IndexOfMax(double[] values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best])
                best = i;
        }
        return best;
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;

        if (Math.Abs(max - min) < 1e-6)
            return (0, 0, l);

        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-6)
            h = (g - b) / d + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 1e-6)
            h = (b - r) / d + 2;
        else
            h = (r - g) / d + 4;

        return (h * 60, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
