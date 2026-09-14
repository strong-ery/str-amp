using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Runtime.Versioning;
using Stramp.Core.Playback;

namespace Stramp.Audio;

/// <summary>
/// Native Windows playback: Media Foundation decodes to 32-bit float and WASAPI sends it through
/// the standard shared-mode media path. The stream follows Windows' default/per-app output route.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiMediaPlayer : IMediaPlayer
{
    private static readonly float[] BandFrequencies =
        [63, 110, 250, 370, 650, 1200, 2130, 4550, 6850, 16000];

    private readonly object _gate = new();
    private readonly Timer _positionTimer;
    private WasapiPlayer? _output;
    private MediaFoundationReader? _reader;
    private GainSampleProvider? _normalizer;
    private EqualizerSampleProvider? _equalizer;
    private double[] _equalizerGains = new double[BandFrequencies.Length];
    private bool _equalizerEnabled;
    private double _volume = 100;
    private double _normalizationGainDb;
    private bool _disposed;

    public event Action<double>? TimePositionChanged;
    public event Action? PlaybackEnded;

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _output?.PlaybackState == PlaybackState.Playing;
        }
    }

    public double TimePosition
    {
        get
        {
            lock (_gate)
                return _reader?.CurrentTime.TotalSeconds ?? 0;
        }
    }

    public double Volume
    {
        get
        {
            lock (_gate)
                return _volume;
        }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0, 100);
                if (_output is not null)
                    _output.Volume = (float)(_volume / 100.0);
            }
        }
    }

    public double NormalizationGainDb
    {
        get
        {
            lock (_gate)
                return _normalizationGainDb;
        }
        set
        {
            lock (_gate)
            {
                _normalizationGainDb = Math.Clamp(value, -60, 24);
                _normalizer?.SetGainDb(_normalizationGainDb);
            }
        }
    }

    public IReadOnlyList<float> EqualizerBands => BandFrequencies;

    public WasapiMediaPlayer()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The WASAPI playback backend requires Windows.");

        _positionTimer = new Timer(ReportPosition, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Play(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseCurrentPlayback();

            var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true,
                RepositionInRead = true,
            });
            var normalizer = new GainSampleProvider(reader.ToSampleProvider(), _normalizationGainDb);
            var equalizer = new EqualizerSampleProvider(normalizer, BandFrequencies);
            equalizer.Update(_equalizerGains, _equalizerEnabled);

            // Shared mode is intentional: this is the normal Windows music-app path, supports the
            // system volume mixer, and lets Windows perform the one required 44.1 -> 48 kHz conversion
            // for devices such as the user's ME6S. Default-device routing also honors per-app routing.
            var output = new WasapiPlayerBuilder()
                .WithDefaultDeviceStreamRouting()
                .WithEventSync()
                .WithLatency(100)
                .WithMmcssThreadPriority("Audio")
                .WithCategory(AudioStreamCategory.Media)
                .BuildAsync()
                .GetAwaiter()
                .GetResult();

            output.Init(equalizer);
            output.Volume = (float)(_volume / 100.0);
            output.PlaybackStopped += (_, e) => HandlePlaybackStopped(output, e);

            _reader = reader;
            _normalizer = normalizer;
            _equalizer = equalizer;
            _output = output;
            output.Play();
            _positionTimer.Change(0, 100);
        }
    }

    public void Pause()
    {
        lock (_gate)
            _output?.Pause();
    }

    public void Resume()
    {
        lock (_gate)
            _output?.Play();
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            if (_output is null)
                return false;

            if (_output.PlaybackState == PlaybackState.Playing)
                _output.Pause();
            else
                _output.Play();

            return _output.PlaybackState == PlaybackState.Playing;
        }
    }

    public void Seek(double positionSeconds)
    {
        lock (_gate)
        {
            if (_reader is null)
                return;

            var seconds = Math.Clamp(positionSeconds, 0, _reader.TotalTime.TotalSeconds);
            _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
            _equalizer?.Reset();
        }
    }

    public void ApplyEqualizer(IReadOnlyList<double> gainsDb, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(gainsDb);
        lock (_gate)
        {
            _equalizerGains = new double[BandFrequencies.Length];
            for (var i = 0; i < Math.Min(_equalizerGains.Length, gainsDb.Count); i++)
                _equalizerGains[i] = Math.Clamp(gainsDb[i], -20, 20);
            _equalizerEnabled = enabled;
            _equalizer?.Update(_equalizerGains, enabled);
        }
    }

    private void ReportPosition(object? _)
    {
        double seconds;
        lock (_gate)
        {
            if (_disposed || _reader is null)
                return;
            seconds = _reader.CurrentTime.TotalSeconds;
        }

        TimePositionChanged?.Invoke(seconds);
    }

    private void HandlePlaybackStopped(WasapiPlayer output, StoppedEventArgs e)
    {
        // Stop/Dispose can synchronously wait for this callback. Bail out before taking the player
        // lock when the output has already been detached, avoiding a shutdown deadlock.
        if (!ReferenceEquals(Volatile.Read(ref _output), output))
            return;

        bool ended;
        lock (_gate)
        {
            if (!ReferenceEquals(_output, output))
                return;

            _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            ended = e.Exception is null && _reader is not null &&
                _reader.Position >= _reader.Length - _reader.WaveFormat.AverageBytesPerSecond / 2;
        }

        if (ended)
            PlaybackEnded?.Invoke();
    }

    private void CloseCurrentPlayback()
    {
        _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);

        var output = _output;
        var reader = _reader;
        _output = null;
        _reader = null;
        _normalizer = null;
        _equalizer = null;

        if (output is not null)
        {
            output.Stop();
            output.Dispose();
        }
        reader?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseCurrentPlayback();
        }
        _positionTimer.Dispose();
    }

    /// <summary>Applies a constant gain with a short smoothing ramp when it changes mid-track.</summary>
    private sealed class GainSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float _smoothingFactor;
        private float _currentGain;
        private float _targetGain;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public GainSampleProvider(ISampleProvider source, double gainDb)
        {
            _source = source;
            _currentGain = DbToLinear(gainDb);
            _targetGain = _currentGain;
            _smoothingFactor = 1 - MathF.Exp(-1 / (0.02f * WaveFormat.SampleRate));
        }

        public void SetGainDb(double gainDb) =>
            Volatile.Write(ref _targetGain, DbToLinear(gainDb));

        public int Read(Span<float> buffer)
        {
            var read = _source.Read(buffer);
            var target = Volatile.Read(ref _targetGain);
            var channels = WaveFormat.Channels;

            for (var frame = 0; frame < read; frame += channels)
            {
                _currentGain += (target - _currentGain) * _smoothingFactor;
                if (MathF.Abs(target - _currentGain) < 0.000001f)
                    _currentGain = target;

                for (var channel = 0; channel < channels && frame + channel < read; channel++)
                    buffer[frame + channel] *= _currentGain;
            }

            return read;
        }

        private static float DbToLinear(double gainDb) =>
            (float)Math.Pow(10, Math.Clamp(gainDb, -60, 24) / 20);
    }

    /// <summary>Interleaved, per-channel parametric EQ with automatic anti-clipping headroom.</summary>
    private sealed class EqualizerSampleProvider : ISampleProvider
    {
        private const float Q = 1.0f;
        private readonly ISampleProvider _source;
        private readonly float[] _frequencies;
        private readonly object _settingsGate = new();
        private double[] _gains;
        private bool _enabled;
        private bool _settingsChanged = true;
        private BiQuadFilter[][]? _filters;
        private float _preamp = 1;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, IReadOnlyList<float> frequencies)
        {
            _source = source;
            _frequencies = [.. frequencies];
            _gains = new double[_frequencies.Length];
        }

        public int Read(Span<float> buffer)
        {
            var read = _source.Read(buffer);
            RefreshFiltersIfNeeded();

            if (!_enabled || _filters is null)
                return read;

            var channels = WaveFormat.Channels;
            for (var sampleIndex = 0; sampleIndex < read; sampleIndex++)
            {
                var channel = sampleIndex % channels;
                var sample = buffer[sampleIndex] * _preamp;
                foreach (var filter in _filters[channel])
                    sample = filter.Transform(sample);
                buffer[sampleIndex] = Math.Clamp(sample, -1f, 1f);
            }
            return read;
        }

        public void Update(IReadOnlyList<double> gainsDb, bool enabled)
        {
            lock (_settingsGate)
            {
                _gains = new double[_frequencies.Length];
                for (var i = 0; i < Math.Min(_gains.Length, gainsDb.Count); i++)
                    _gains[i] = Math.Clamp(gainsDb[i], -20, 20);
                _enabled = enabled;
                _settingsChanged = true;
            }
        }

        public void Reset()
        {
            lock (_settingsGate)
                _settingsChanged = true;
        }

        private void RefreshFiltersIfNeeded()
        {
            lock (_settingsGate)
            {
                if (!_settingsChanged)
                    return;

                var maxBoost = _enabled ? Math.Max(0, _gains.Max()) : 0;
                _preamp = (float)Math.Pow(10, -maxBoost / 20);
                _filters = new BiQuadFilter[WaveFormat.Channels][];
                for (var channel = 0; channel < _filters.Length; channel++)
                {
                    _filters[channel] = new BiQuadFilter[_frequencies.Length];
                    for (var band = 0; band < _frequencies.Length; band++)
                    {
                        var frequency = Math.Min(_frequencies[band], WaveFormat.SampleRate * 0.49f);
                        _filters[channel][band] = BiQuadFilter.PeakingEQ(
                            WaveFormat.SampleRate, frequency, Q, (float)_gains[band]);
                    }
                }
                _settingsChanged = false;
            }
        }
    }
}
