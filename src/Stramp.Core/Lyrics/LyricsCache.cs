using System.Security.Cryptography;
using System.Text;
using Stramp.Core.Settings;

namespace Stramp.Core.Lyrics;

/// <summary>
/// Remembers what LRCLIB answered, so a track is only ever looked up once. Hits are kept as .lrc
/// text; misses are kept too, because re-asking for a track LRCLIB does not have on every play is
/// the main way a client becomes a nuisance to it.
/// </summary>
public sealed class LyricsCache(string? directory = null)
{
    /// <summary>Misses go stale: LRCLIB gains uploads, so an old "nothing here" is worth retrying.</summary>
    private static readonly TimeSpan MissLifetime = TimeSpan.FromDays(14);

    private const string InstrumentalMarker = "instrumental";

    private readonly string _directory =
        directory ?? Path.Combine(SettingsService.ConfigDirectory, "lyrics");

    public enum Miss
    {
        /// <summary>LRCLIB was asked and had nothing for this track.</summary>
        NotFound,

        /// <summary>LRCLIB has the track, flagged as having no words.</summary>
        Instrumental,
    }

    /// <summary>Cached .lrc text for a track, or null when it has never been looked up.</summary>
    public string? ReadLyrics(string audioPath)
    {
        try
        {
            var path = PathFor(audioPath, ".lrc");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when a still-fresh miss is on record, so there is no point asking again.</summary>
    public bool TryReadMiss(string audioPath, out Miss miss)
    {
        miss = Miss.NotFound;
        try
        {
            var path = PathFor(audioPath, ".miss");
            if (!File.Exists(path))
                return false;

            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > MissLifetime)
            {
                File.Delete(path);
                return false;
            }

            if (File.ReadAllText(path).Trim() == InstrumentalMarker)
                miss = Miss.Instrumental;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void WriteLyrics(string audioPath, string lrcText) =>
        Write(audioPath, ".lrc", lrcText);

    public void WriteMiss(string audioPath, Miss miss) =>
        Write(audioPath, ".miss", miss == Miss.Instrumental ? InstrumentalMarker : "none");

    /// <summary>Drops whatever is on record for a track, so the next lookup goes back to LRCLIB.</summary>
    public void Forget(string audioPath)
    {
        foreach (var extension in (string[])[".lrc", ".miss"])
        {
            try
            {
                File.Delete(PathFor(audioPath, extension));
            }
            catch
            {
                // Nothing cached, or the file is locked — the lookup runs again either way.
            }
        }
    }

    private void Write(string audioPath, string extension, string content)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(PathFor(audioPath, extension), content);
            RemoveOthers(audioPath, kept: extension);
        }
        catch
        {
            // An unwritable cache directory only costs us the lookup again next time.
        }
    }

    /// <summary>A track is either a hit or a miss, never both, so writing one clears the other.</summary>
    private void RemoveOthers(string audioPath, string kept)
    {
        foreach (var extension in (string[])[".lrc", ".miss"])
        {
            if (extension == kept)
                continue;
            try
            {
                File.Delete(PathFor(audioPath, extension));
            }
            catch
            {
                // Leaving a stale marker behind is harmless; the .lrc is read first.
            }
        }
    }

    /// <summary>
    /// Track paths are hashed rather than sanitised: they are longer than a filename may be, and
    /// two different tracks must never collapse onto the same cache entry.
    /// </summary>
    private string PathFor(string audioPath, string extension)
    {
        var normalized = Path.GetFullPath(audioPath).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash)[..32] + extension);
    }
}
