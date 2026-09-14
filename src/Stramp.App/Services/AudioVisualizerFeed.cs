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

    private float[]? _pcm;
    private float[]? _waveform;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread once a track's PCM is decoded and its waveform is ready.</summary>
    public event Action? WaveformReady;

    public void LoadTrack(string path)
    {
        _pcm = null;
        _waveform = null;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadAsync(path, cts.Token);
    }

    private async Task LoadAsync(string path, CancellationToken ct)
    {
        var pcm = await TrackPcmDecoder.DecodeMonoAsync(path, SampleRate, ct);
        if (ct.IsCancellationRequested)
            return;

        _pcm = pcm;
        _waveform = pcm is null ? null : BuildWaveform(pcm, WaveformBuckets);

        if (_waveform is not null)
            Dispatcher.UIThread.Post(() => WaveformReady?.Invoke());
    }

    /// <summary>Normalized peak envelope of the whole track, or null until decoding finishes.</summary>
    public float[]? Waveform => _waveform;

    private const int WaveformBuckets = 900;

    private static float[] BuildWaveform(float[] pcm, int buckets)
    {
        var envelope = new float[buckets];
        if (pcm.Length == 0)
            return envelope;

        var samplesPerBucket = Math.Max(1, pcm.Length / buckets);
        var loudest = 0f;

        for (var b = 0; b < buckets; b++)
        {
            var start = (int)((long)b * pcm.Length / buckets);
            var end = Math.Min(pcm.Length, start + samplesPerBucket);

            var peak = 0f;
            for (var i = start; i < end; i++)
            {
                var v = Math.Abs(pcm[i]);
                if (v > peak)
                    peak = v;
            }

            envelope[b] = peak;
            if (peak > loudest)
                loudest = peak;
        }

        // Normalize against the track's own peak so quiet masters still fill the bar.
        if (loudest > 0.0001f)
        {
            for (var b = 0; b < buckets; b++)
                envelope[b] = Math.Clamp(envelope[b] / loudest, 0f, 1f);
        }

        return envelope;
    }

    /// <summary>Returns the spectrum around `timePositionSeconds`, or null if not decoded (yet, or at all).</summary>
    public SpectrumFrame? GetFrame(double timePositionSeconds, int displayBinCount)
    {
        var pcm = _pcm;
        if (pcm is null || pcm.Length < WindowSize)
            return null;

        var center = (int)(timePositionSeconds * SampleRate);
        var start = Math.Clamp(center - WindowSize, 0, pcm.Length - WindowSize);
        return SpectrumAnalyzer.Analyze(pcm.AsSpan(start, WindowSize), SampleRate, displayBinCount);
    }
}
