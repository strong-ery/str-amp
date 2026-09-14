using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Stramp.App.Services;

/// <summary>A pair of vibrant colors pulled out of album artwork.</summary>
public readonly record struct ArtPalette(Color Primary, Color Secondary);

/// <summary>
/// Derives theme colors from album art: downsamples the cover, buckets pixels by hue weighted by
/// how vivid they are, then picks the strongest hue plus a clearly different second one. Results
/// are pushed into a saturation/lightness range that stays legible on a dark UI.
/// </summary>
public static class AlbumPalette
{
    private const int SampleSize = 28;
    private const int HueBuckets = 24;

    public static ArtPalette? Extract(Bitmap source)
    {
        var pixels = SamplePixels(source);
        if (pixels is null)
            return null;

        var weights = new double[HueBuckets];
        var hueSums = new double[HueBuckets];
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

        return new ArtPalette(
            FromHsl(primaryHue, 0.72, 0.63),
            FromHsl(secondaryHue, 0.76, 0.66));
    }

    /// <summary>A very dark, faintly hue-tinted background that pairs with the derived accent.</summary>
    public static Color DeriveBackground(Color primary)
    {
        var (h, _, _) = ToHsl(primary.R / 255.0, primary.G / 255.0, primary.B / 255.0);
        return FromHsl(h, 0.30, 0.055);
    }

    private static byte[]? SamplePixels(Bitmap source)
    {
        try
        {
            var size = new PixelSize(SampleSize, SampleSize);
            using var target = new RenderTargetBitmap(size);
            using (var ctx = target.CreateDrawingContext())
                ctx.DrawImage(source, new Rect(0, 0, SampleSize, SampleSize));

            var stride = SampleSize * 4;
            var buffer = new byte[stride * SampleSize];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                target.CopyPixels(
                    new PixelRect(0, 0, SampleSize, SampleSize),
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
