using System.Security.Cryptography;
using System.Text;
using Stramp.Core.Settings;

namespace Stramp.Core.Lyrics;

/// <summary>
/// Lyrics the user wrote or timed themselves, kept in STRAMP's own app data folder (never beside
/// the music). They are .lrc text keyed by a hash of the track's path, and always outrank anything
/// found on disk or downloaded, because the user made them on purpose.
/// </summary>
public sealed class UserLyricsStore(string? directory = null)
{
    private readonly string _directory =
        directory ?? Path.Combine(SettingsService.ConfigDirectory, "user-lyrics");

    public string? Read(string audioPath)
    {
        try
        {
            var path = PathFor(audioPath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the user has written lyrics or marked the track instrumental.</summary>
    public bool Exists(string audioPath) => File.Exists(PathFor(audioPath)) || IsInstrumental(audioPath);

    /// <summary>The user said this track has no words. Kept as its own marker file.</summary>
    public bool IsInstrumental(string audioPath) => File.Exists(PathFor(audioPath, ".instrumental"));

    public void WriteInstrumental(string audioPath)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PathFor(audioPath, ".instrumental"), "instrumental");
        TryDelete(PathFor(audioPath));
    }

    /// <summary>Drops everything the user made for this track, so it falls back to disk or LRCLIB.</summary>
    public void Delete(string audioPath)
    {
        TryDelete(PathFor(audioPath));
        TryDelete(PathFor(audioPath, ".instrumental"));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    /// <summary>Written through a temporary file so a failure never leaves half a lyric sheet.</summary>
    public void Write(string audioPath, string lrcText)
    {
        Directory.CreateDirectory(_directory);
        var path = PathFor(audioPath);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, lrcText, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            TryDelete(PathFor(audioPath, ".instrumental"));
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private string PathFor(string audioPath, string extension = ".lrc")
    {
        var normalized = Path.GetFullPath(audioPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash)[..32] + extension);
    }
}
