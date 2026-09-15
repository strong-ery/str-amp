using System.Text.Json;
using Stramp.Core.Models;
using Stramp.Core.Settings;

namespace Stramp.Core.Library;

/// <summary>Persistent tag cache keyed by path, file length, and modification time.</summary>
public sealed class LibraryMetadataCache
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _cachePath;
    private readonly Dictionary<string, CacheEntry> _entries;

    public LibraryMetadataCache(string? cachePath = null)
    {
        _cachePath = cachePath ?? Path.Combine(
            SettingsService.ConfigDirectory, "library-metadata-cache.json");
        _entries = Load();
    }

    public bool TryGet(FileInfo file, out Song song)
    {
        song = null!;
        try
        {
            var length = file.Length;
            var modifiedUtcTicks = file.LastWriteTimeUtc.Ticks;
            lock (_gate)
            {
                if (!_entries.TryGetValue(file.FullName, out var entry) ||
                    entry.Length != length || entry.ModifiedUtcTicks != modifiedUtcTicks)
                    return false;

                song = new Song
                {
                    Path = file.FullName,
                    Title = entry.Title,
                    Artist = entry.Artist,
                    Album = entry.Album,
                    Duration = TimeSpan.FromTicks(entry.DurationTicks),
                };
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public void Put(FileInfo file, Song song)
    {
        try
        {
            var entry = new CacheEntry
            {
                Length = file.Length,
                ModifiedUtcTicks = file.LastWriteTimeUtc.Ticks,
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                DurationTicks = song.Duration.Ticks,
            };
            lock (_gate)
                _entries[file.FullName] = entry;
        }
        catch
        {
            // A file can disappear between enumeration and caching; the song still loads this run.
        }
    }

    public void Save()
    {
        try
        {
            Dictionary<string, CacheEntry> snapshot;
            lock (_gate)
                snapshot = new Dictionary<string, CacheEntry>(_entries, StringComparer.OrdinalIgnoreCase);

            var directory = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var temporaryPath = _cachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch
        {
            // An unwritable cache should only cost performance, never the library itself.
        }
    }

    private Dictionary<string, CacheEntry> Load()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
            var stored = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                File.ReadAllText(_cachePath), JsonOptions);
            return stored is null
                ? new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CacheEntry>(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class CacheEntry
    {
        public long Length { get; init; }
        public long ModifiedUtcTicks { get; init; }
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public string Album { get; init; } = "";
        public long DurationTicks { get; init; }
    }
}
