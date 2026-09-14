using System.Collections.Concurrent;
using System.Text.Json;
using Stramp.Audio;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.App.Services;

/// <summary>Caches non-destructive track measurements and returns their static playback gains.</summary>
public sealed class LoudnessNormalizationService : IDisposable
{
    private const int BackgroundWorkers = 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _cachePath = Path.Combine(SettingsService.ConfigDirectory, "loudness-cache.json");
    private readonly SemaphoreSlim _analysisSlots = new(BackgroundWorkers + 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<TrackLoudness?>>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CacheEntry> _entries;

    public LoudnessNormalizationService()
    {
        _entries = LoadCache();
    }

    public bool TryGetGainDb(
        string path, AudioNormalizationLevel level, out double gainDb)
    {
        gainDb = 0;
        if (!TryGetMeasurement(path, out var loudness))
            return false;

        gainDb = LoudnessNormalization.CalculateGainDb(loudness, level);
        return true;
    }

    public async Task<double?> GetGainDbAsync(
        string path, AudioNormalizationLevel level, CancellationToken ct = default)
    {
        if (TryGetGainDb(path, level, out var cachedGain))
            return cachedGain;

        var measurement = await GetMeasurementAsync(path, ct);
        if (measurement is not { } loudness || ct.IsCancellationRequested)
            return null;

        return LoudnessNormalization.CalculateGainDb(loudness, level);
    }

    /// <summary>Fills the on-disk cache with two background workers, without flooding the disk.</summary>
    public async Task CacheLibraryAsync(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var uniquePaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            await Parallel.ForEachAsync(uniquePaths, new ParallelOptions
            {
                MaxDegreeOfParallelism = BackgroundWorkers,
                CancellationToken = ct,
            }, async (path, token) =>
            {
                if (!TryGetMeasurement(path, out _))
                    await GetMeasurementAsync(path, token);
            });
        }
        catch (OperationCanceledException)
        {
            // Changing libraries or closing the app stops scheduling more tracks.
        }
    }

    private bool TryGetMeasurement(string path, out TrackLoudness loudness)
    {
        loudness = default;
        if (!TryGetFileStamp(path, out var length, out var modifiedUtcTicks))
            return false;

        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var entry) ||
                entry.Length != length || entry.ModifiedUtcTicks != modifiedUtcTicks)
                return false;

            loudness = new TrackLoudness(entry.IntegratedLufs, entry.TruePeakDbTp);
            return true;
        }
    }

    private async Task<TrackLoudness?> GetMeasurementAsync(string path, CancellationToken ct)
    {
        if (TryGetMeasurement(path, out var cached))
            return cached;

        var lazy = _inFlight.GetOrAdd(path, static (filePath, service) =>
            new Lazy<Task<TrackLoudness?>>(
                () => service.AnalyzeAndCacheAsync(filePath),
                LazyThreadSafetyMode.ExecutionAndPublication), this);
        var task = lazy.Value;

        try
        {
            return await task.WaitAsync(ct);
        }
        finally
        {
            if (task.IsCompleted && _inFlight.TryGetValue(path, out var current) &&
                ReferenceEquals(current, lazy))
                _inFlight.TryRemove(path, out _);
        }
    }

    private async Task<TrackLoudness?> AnalyzeAndCacheAsync(string path)
    {
        try
        {
            await _analysisSlots.WaitAsync(_shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (TryGetMeasurement(path, out var cached))
                return cached;

            var measurement = await FfmpegLoudnessAnalyzer.AnalyzeAsync(path, _shutdown.Token);
            if (measurement is not { } loudness ||
                !TryGetFileStamp(path, out var length, out var modifiedUtcTicks))
                return null;

            lock (_gate)
            {
                _entries[path] = new CacheEntry
                {
                    Length = length,
                    ModifiedUtcTicks = modifiedUtcTicks,
                    IntegratedLufs = loudness.IntegratedLufs,
                    TruePeakDbTp = loudness.TruePeakDbTp,
                };
                SaveCache();
            }

            return loudness;
        }
        finally
        {
            _analysisSlots.Release();
        }
    }

    private Dictionary<string, CacheEntry> LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(_cachePath);
            var stored = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, JsonOptions);
            return stored is null
                ? new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, CacheEntry>(stored, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.ConfigDirectory);
            var temporaryPath = _cachePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch
        {
            // Playback still works without a writable cache; the track will be measured next time.
        }
    }

    private static bool TryGetFileStamp(string path, out long length, out long modifiedUtcTicks)
    {
        length = 0;
        modifiedUtcTicks = 0;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return false;
            length = file.Length;
            modifiedUtcTicks = file.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry()
        {
        }

        public long Length { get; init; }
        public long ModifiedUtcTicks { get; init; }
        public double IntegratedLufs { get; init; }
        public double TruePeakDbTp { get; init; }
    }

    public void Dispose() => _shutdown.Cancel();
}
