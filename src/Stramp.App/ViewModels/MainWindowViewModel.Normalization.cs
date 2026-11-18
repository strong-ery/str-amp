using Avalonia.Threading;
using Stramp.Core.Models;

namespace Stramp.App.ViewModels;
public partial class MainWindowViewModel
{
    private void ApplyNormalizationSetting()
    {
        var currentPath = _queue.Current?.Path;
        if (!_settings.AudioNormalizationEnabled)
        {
            var pendingPath = _pendingPlaybackPath;
            CancelNormalizationAnalysis();
            _player.NormalizationGainDb = 0;

            if (pendingPath is not null && currentPath == pendingPath)
                StartPlaybackNow(pendingPath);
            return;
        }

        if (!_playerHasCurrentTrack || currentPath is null || _pendingPlaybackPath is not null)
            return;

        CancelNormalizationAnalysis();

        if (_loudnessNormalizer.TryGetGainDb(
            currentPath, _settings.AudioNormalizationLevel, out var cachedGain))
        {
            _player.NormalizationGainDb = cachedGain;
            return;
        }

        _player.NormalizationGainDb = 0;
        _normalizationCts = new CancellationTokenSource();
        _ = AnalyzeAndApplyNormalizationAsync(
            currentPath, startPlayback: false, _normalizationCts.Token);
    }

    private async Task AnalyzeAndApplyNormalizationAsync(
        string path, bool startPlayback, CancellationToken ct)
    {
        var gainDb = await _loudnessNormalizer.GetGainDbAsync(
            path, _settings.AudioNormalizationLevel, ct);
        if (ct.IsCancellationRequested)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested || !_settings.AudioNormalizationEnabled ||
                _queue.Current?.Path != path)
                return;

            // The user may have selected another preset while analysis was running. Measurements
            // are target-independent, so derive the gain again from the now-cached values.
            _player.NormalizationGainDb = _loudnessNormalizer.TryGetGainDb(
                path, _settings.AudioNormalizationLevel, out var currentGain)
                ? currentGain
                : gainDb ?? 0;
            if (startPlayback && _pendingPlaybackPath == path)
                StartPlaybackNow(path);
        });
    }

    private void StartNormalizationWarmup()
    {
        CancelNormalizationWarmup();
        if (_library.Count == 0)
            return;

        _normalizationWarmupCts = new CancellationTokenSource();
        var token = _normalizationWarmupCts.Token;

        // Snapshots are taken here; ordering 100K paths happens on a worker, not the UI thread.
        var queue = _queue.Songs.ToArray();
        var queueStart = Math.Clamp(_queue.Position, 0, queue.Length);
        var library = _library.ToArray();
        _ = Task.Run(() => _loudnessNormalizer.CacheLibraryAsync(
            BuildNormalizationCacheOrder(queue, queueStart, library), token), token);
    }

    private static IReadOnlyList<string> BuildNormalizationCacheOrder(
        Song[] queue, int queueStart, Song[] library)
    {
        var paths = new List<string>(library.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Cache what will play next first. Once the queue is exhausted, sweep every remaining
        // library track so leaving STRAMP open eventually produces a complete disk cache.
        for (var index = queueStart; index < queue.Length; index++)
        {
            var path = queue[index].Path;
            if (seen.Add(path))
                paths.Add(path);
        }

        foreach (var song in library)
        {
            if (seen.Add(song.Path))
                paths.Add(song.Path);
        }

        return paths;
    }

    private void CancelNormalizationWarmup()
    {
        _normalizationWarmupCts?.Cancel();
        _normalizationWarmupCts?.Dispose();
        _normalizationWarmupCts = null;
    }

    private void CancelNormalizationAnalysis()
    {
        _normalizationCts?.Cancel();
        _normalizationCts?.Dispose();
        _normalizationCts = null;
        _pendingPlaybackPath = null;
    }

}
