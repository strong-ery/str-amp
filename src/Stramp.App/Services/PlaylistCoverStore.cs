using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Stramp.Core.Settings;

namespace Stramp.App.Services;

/// <summary>
/// Custom playlist covers, kept in STRAMP's own app data folder (never next to the user's
/// playlists or music). Each is a square PNG named after a hash of the playlist's name, so any
/// playlist name is a safe file name and a name always finds the same file.
/// </summary>
public static class PlaylistCoverStore
{
    /// <summary>Side of the saved square, in pixels. Plenty for the largest place a cover is shown.</summary>
    public const int SavedSize = 512;

    /// <summary>Covers are shown at most ~100px wide, so a decode this size stays sharp on high-DPI.</summary>
    private const int DisplayDecodeWidth = 256;

    /// <summary>Images wider or taller than this are decoded smaller for the editor.</summary>
    private const int EditorMaxSide = 3000;

    /// <summary>What the file picker offers: the formats the image decoder can actually read.</summary>
    public static readonly string[] SupportedPatterns =
        ["*.png", "*.jpg", "*.jpeg", "*.jpe", "*.jfif", "*.webp", "*.bmp", "*.gif", "*.ico"];

    public static string Directory => Path.Combine(SettingsService.ConfigDirectory, "playlist-covers");

    public static string PathFor(string playlistName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(playlistName.Trim().ToLowerInvariant()));
        return Path.Combine(Directory, Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".png");
    }

    public static bool Exists(string playlistName) => File.Exists(PathFor(playlistName));

    /// <summary>The saved cover at display size, or null if there is none or it can't be read.</summary>
    public static Bitmap? TryLoad(string playlistName)
    {
        try
        {
            var path = PathFor(playlistName);
            if (!File.Exists(path))
                return null;

            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, DisplayDecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes the cover through a temporary file, so a failure never leaves half an image.</summary>
    public static void Save(string playlistName, Bitmap cover)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(playlistName);
        var temporary = path + ".tmp";
        try
        {
            using (var stream = File.Create(temporary))
                cover.Save(stream, PngBitmapEncoderOptions.Default);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    public static void Delete(string playlistName)
    {
        try
        {
            File.Delete(PathFor(playlistName));
        }
        catch
        {
            // A cover that can't be deleted is harmless: it just stays until the next attempt.
        }
    }

    /// <summary>Opens a picked image for the editor, shrunk if it is huge. Null when it can't be read.</summary>
    public static Bitmap? LoadForEditing(string path)
    {
        try
        {
            Bitmap full;
            using (var stream = File.OpenRead(path))
                full = new Bitmap(stream);

            var width = full.PixelSize.Width;
            var height = full.PixelSize.Height;
            if (Math.Max(width, height) <= EditorMaxSide)
                return full;

            // Never upscale: only ask for a smaller decode when the picture is larger than needed.
            full.Dispose();
            var targetWidth = width >= height ? EditorMaxSide : EditorMaxSide * width / height;
            using var again = File.OpenRead(path);
            return Bitmap.DecodeToWidth(again, Math.Max(1, targetWidth), BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }
}
