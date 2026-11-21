using Avalonia.Threading;
using Stramp.Audio;
using Stramp.Core.Dsp;

namespace Stramp.App.Services;

/// <summary>
/// Owns the decoded-PCM buffer for whichever track is currently loaded, and answers
/// "what does the spectrum look like at this playback position" on demand. Decoding happens
/// once per track on a background thread; analysis is a cheap per-tick FFT over a small window.
/// </summary>
public sealed class AudioVisualizerFeed
{
    private const int SampleRate = 44100;
    private const int WindowSize = 2048;

    private ReadOnlyMemory<float>? _pcm;
    private float[]? _waveform;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread once a track's PCM is decoded and its waveform is ready.</summary>
    public event Action? WaveformReady;

    public void LoadTrack(string path, double durationSeconds = 0)
    {
        _pcm = null;
        _waveform = null;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(path, durationSeconds, cts.Token);
    }

    private async Task LoadAsync(string path, double durationSeconds, CancellationToken ct)
    {
        // Skipping through songs cancels this almost immediately, so wait a beat before spending a
        // decode (and a GC) on a track that is about to be skipped.
        try { await Task.Delay(200, ct); }
        catch (OperationCanceledException) { return; }

        // The previous track's PCM is a large-object-heap array that's now garbage. Compact the LOH
        // once, before allocating the next one, so freed holes get reused/returned instead of
        // piling up as fragmentation across skips. The live heap is small, so the pause is short.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        var pcm = await TrackPcmDecoder.DecodeMonoAsync(path, SampleRate, durationSeconds, ct);
        if (ct.IsCancellationRequested)
            return;

        _pcm = pcm;
        _waveform = pcm is null ? null : BuildWaveform(pcm.Value.Span, WaveformBuckets);

        if (_waveform is not null)
            Dispatcher.UIThread.Post(() => WaveformReady?.Invoke());
    }

    /// <summary>Normalized peak envelope of the whole track, or null until decoding finishes.</summary>
    public float[]? Waveform => _waveform;

    private const int WaveformBuckets = 900;

    private static float[] BuildWaveform(ReadOnlySpan<float> pcm, int buckets)
    {
        var envelope = new float[buckets];
        if (pcm.Length == 0)
            return envelope;

        var samplesPerBucket = Math.Max(1, pcm.Length / buckets);
        var maxRms = 0f;

        for (var b = 0; b < buckets; b++)
        {
            var start = (int)((long)b * pcm.Length / buckets);
            var end = Math.Min(pcm.Length, start + samplesPerBucket);
            var count = Math.Max(1, end - start);

            double sumSq = 0;
            for (var i = start; i < end; i++)
            {
                var v = pcm[i];
                sumSq += v * v;
            }

            var rms = (float)Math.Sqrt(sumSq / count);
            envelope[b] = rms;
            if (rms > maxRms)
                maxRms = rms;
        }

        // Normalize RMS against max track energy and apply perceptual power curve (gamma 0.7)
        if (maxRms > 0.0001f)
        {
            for (var b = 0; b < buckets; b++)
            {
                var norm = Math.Clamp(envelope[b] / maxRms, 0f, 1f);
                envelope[b] = (float)Math.Pow(norm, 0.7);
            }
        }

        return envelope;
    }

    /// <summary>Returns the spectrum around `timePositionSeconds`, or null if not decoded (yet, or at all).</summary>
    public SpectrumFrame? GetFrame(double timePositionSeconds, int displayBinCount)
    {
        if (_pcm is not { } memory || memory.Length < WindowSize)
            return null;
        var pcm = memory.Span;

        var center = (int)(timePositionSeconds * SampleRate);
        var start = Math.Clamp(center - WindowSize, 0, pcm.Length - WindowSize);
        return SpectrumAnalyzer.Analyze(pcm.Slice(start, WindowSize), SampleRate, displayBinCount);
    }
}
