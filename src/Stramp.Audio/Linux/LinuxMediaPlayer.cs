using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.Wave;
using Stramp.Audio.Dsp;
using Stramp.Audio.Effects;
using Stramp.Core.Playback;

namespace Stramp.Audio.Linux;

/// <summary>
/// Linux playback: ffmpeg decodes to 32-bit float, the same equalizer / effects / gain chain as the
/// Windows backend shapes it, and PulseAudio (or PipeWire's pulse server) plays it.
/// </summary>
public sealed class LinuxMediaPlayer : IMediaPlayer
{
    private static readonly float[] DefaultBandFrequencies =
        [63, 110, 250, 370, 650, 1200, 2130, 4550, 6850, 16000];

    private const int BufferFrames = 1024;
    private const int OutputLatencyMs = 80;

    private readonly object _gate = new();
    private readonly Timer _positionTimer;
    private Session? _session;
    private float[] _equalizerFrequencies = [.. DefaultBandFrequencies];
    private double[] _equalizerGains = new double[DefaultBandFrequencies.Length];
    private bool _equalizerEnabled;
    private AudioEffectSettings _effectSettings = AudioEffectSettings.None;
    private double _volume = 100;
    private bool _muted;
    private double _normalizationGainDb;
    private bool _monoOutput;
    private bool _surroundSoundEnabled;
    private string? _outputDeviceId;
    private string? _currentPath;
    private bool _disposed;

    public event Action<double>? TimePositionChanged;
    public event Action? PlaybackEnded;

    public LinuxMediaPlayer()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The PulseAudio playback backend requires Linux.");

        _positionTimer = new Timer(ReportPosition, null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _session is { Paused: false, Finished: false };
        }
    }

    public double TimePosition
    {
        get
        {
            lock (_gate)
                return _session?.PositionSeconds ?? 0;
        }
    }

    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                _volume = Math.Clamp(value, 0, 100);
                if (_session is not null)
                    _session.TargetVolume = EffectiveVolume;
            }
        }
    }

    public bool Muted
    {
        get { lock (_gate) return _muted; }
        set
        {
            lock (_gate)
            {
                _muted = value;
                if (_session is not null)
                    _session.TargetVolume = EffectiveVolume;
            }
        }
    }

    public double NormalizationGainDb
    {
        get { lock (_gate) return _normalizationGainDb; }
        set
        {
            lock (_gate)
            {
                _normalizationGainDb = Math.Clamp(value, -60, 24);
                _session?.Normalizer.SetGainDb(_normalizationGainDb,
                    immediate: _session is not { Paused: false, Finished: false });
            }
        }
    }

    public string? OutputDeviceId
    {
        get { lock (_gate) return _outputDeviceId; }
        set
        {
            lock (_gate)
            {
                var id = string.IsNullOrWhiteSpace(value) ? null : value;
                if (string.Equals(_outputDeviceId, id, StringComparison.Ordinal))
                    return;

                _outputDeviceId = id;
                if (_disposed || _currentPath is null || _session is null)
                    return;

                // A Pulse stream is bound to its sink when it's created, so moving means rebuilding.
                OpenPlayback(_currentPath, _session.PositionSeconds, !_session.Paused && !_session.Finished);
            }
        }
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pactl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--format=json");
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("sinks");
            psi.Environment["LC_ALL"] = "C";

            using var process = Process.Start(psi);
            if (process is null)
                return [];

            var stdout = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return [];
            }

            using var doc = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            var devices = new List<AudioOutputDevice>();
            foreach (var sink in doc.RootElement.EnumerateArray())
            {
                var name = sink.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(name))
                    continue;
                var description = sink.TryGetProperty("description", out var d) ? d.GetString() : null;
                devices.Add(new AudioOutputDevice(name, string.IsNullOrWhiteSpace(description) ? name : description));
            }
            return devices;
        }
        catch
        {
            // pactl missing or an older version without JSON: just offer the system default.
            return [];
        }
    }

    public bool MonoOutput
    {
        get { lock (_gate) return _monoOutput; }
        set
        {
            lock (_gate)
            {
                _monoOutput = value;
                _session?.Mono.SetEnabled(value);
            }
        }
    }

    public bool SurroundSoundEnabled
    {
        get { lock (_gate) return _surroundSoundEnabled; }
        set
        {
            lock (_gate)
            {
                if (_surroundSoundEnabled == value)
                    return;

                _surroundSoundEnabled = value;
                if (_disposed || _currentPath is null || _session is null)
                    return;

                if (_session.SourceChannels > 2)
                    OpenPlayback(_currentPath, _session.PositionSeconds, !_session.Paused && !_session.Finished);
            }
        }
    }

    public IReadOnlyList<float> DefaultEqualizerBands => DefaultBandFrequencies;

    public void Play(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            OpenPlayback(path, startSeconds: 0, play: true);
        }
    }

    private float EffectiveVolume => _muted ? 0f : (float)(_volume / 100.0);

    /// <summary>Builds the whole decode -> DSP -> output chain for a track. The caller holds the lock.</summary>
    private void OpenPlayback(string path, double startSeconds, bool play)
    {
        CloseSession();

        var probe = FfmpegProbe.Probe(path);
        if (probe.DurationSeconds > 0)
            startSeconds = Math.Clamp(startSeconds, 0, probe.DurationSeconds);

        var channels = probe.Channels > 2 && !_surroundSoundEnabled ? 2 : probe.Channels;
        Session session;
        try
        {
            session = BuildSession(path, startSeconds, probe, channels, play);
        }
        catch (Exception) when (channels > 2)
        {
            // The server couldn't take this many channels: fall back to a stereo downmix.
            session = BuildSession(path, startSeconds, probe, 2, play);
        }

        _currentPath = path;
        _session = session;
        session.Start();
        if (play)
            _positionTimer.Change(0, 100);
    }

    private Session BuildSession(string path, double startSeconds, AudioProbe probe, int channels, bool play)
    {
        var source = new FfmpegSampleProvider(path, startSeconds, channels, probe.SampleRate);
        try
        {
            var normalizer = new GainSampleProvider(source, _normalizationGainDb);
            var equalizer = new EqualizerSampleProvider(normalizer, _equalizerFrequencies);
            equalizer.Update(_equalizerFrequencies, _equalizerGains, _equalizerEnabled);
            var effects = new DfxEffectProcessor(equalizer, _effectSettings);
            var mono = new MonoDownmixSampleProvider(effects, _monoOutput);

            var handle = PulseSimple.Open(_outputDeviceId, probe.SampleRate, channels, OutputLatencyMs, out var error);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException($"couldn't open the audio output (PulseAudio error {error}).");

            return new Session(this, handle, source, normalizer, equalizer, effects, mono,
                startSeconds, probe.SampleRate, channels, probe.Channels, EffectiveVolume, paused: !play);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public void Pause()
    {
        lock (_gate)
            _session?.SetPaused(true);
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_session is null)
                return;
            _session.SetPaused(false);
            _positionTimer.Change(0, 100);
        }
    }

    public bool TogglePause()
    {
        lock (_gate)
        {
            if (_session is null)
                return false;

            var nowPaused = !_session.Paused;
            _session.SetPaused(nowPaused);
            if (!nowPaused)
                _positionTimer.Change(0, 100);
            return !nowPaused;
        }
    }

    public void Seek(double positionSeconds)
    {
        lock (_gate)
        {
            if (_session is null || _currentPath is null)
                return;

            // ffmpeg can't rewind a pipe, so a seek is a fresh decode from the new offset. The old
            // Pulse stream is freed rather than drained, so no stale audio plays after the jump.
            OpenPlayback(_currentPath, Math.Max(0, positionSeconds), !_session.Paused);
        }
    }

    public void ApplyEqualizer(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(centreFrequencies);
        ArgumentNullException.ThrowIfNull(gainsDb);

        lock (_gate)
        {
            var frequencies = centreFrequencies.Count > 0 ? centreFrequencies : DefaultBandFrequencies;
            _equalizerFrequencies = [.. frequencies];
            _equalizerGains = new double[_equalizerFrequencies.Length];
            for (var i = 0; i < Math.Min(_equalizerGains.Length, gainsDb.Count); i++)
                _equalizerGains[i] = Math.Clamp(gainsDb[i], -20, 20);

            _equalizerEnabled = enabled;
            _session?.Equalizer.Update(_equalizerFrequencies, _equalizerGains, enabled);
        }
    }

    public void ApplyEffects(AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _effectSettings = settings.Clamped();
            _session?.Effects.Update(_effectSettings);
        }
    }

    private void ReportPosition(object? _)
    {
        double seconds;
        lock (_gate)
        {
            if (_disposed || _session is null)
                return;
            seconds = _session.PositionSeconds;
        }

        TimePositionChanged?.Invoke(seconds);
    }

    private void OnSessionEnded(Session session)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_session, session))
                return;
            _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // Off the render thread: a handler that starts the next track disposes this session, which
        // joins that thread.
        ThreadPool.QueueUserWorkItem(_ => PlaybackEnded?.Invoke());
    }

    private void CloseSession()
    {
        _positionTimer.Change(Timeout.Infinite, Timeout.Infinite);
        var session = _session;
        _session = null;
        session?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _currentPath = null;
            CloseSession();
        }
        _positionTimer.Dispose();
    }

    /// <summary>One track's decoder, DSP chain, Pulse stream and the thread that feeds it.</summary>
    private sealed class Session : IDisposable
    {
        private readonly LinuxMediaPlayer _owner;
        private readonly IntPtr _pulse;
        private readonly FfmpegSampleProvider _source;
        private readonly ISampleProvider _chain;
        private readonly double _startSeconds;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly ManualResetEventSlim _running;
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private long _framesWritten;
        private long _latencyMicros;
        private float _volume;
        private bool _disposed;

        public GainSampleProvider Normalizer { get; }
        public EqualizerSampleProvider Equalizer { get; }
        public DfxEffectProcessor Effects { get; }
        public MonoDownmixSampleProvider Mono { get; }
        public int SourceChannels { get; }
        public float TargetVolume { get; set; }
        public bool Paused { get; private set; }
        public bool Finished { get; private set; }

        public Session(
            LinuxMediaPlayer owner, IntPtr pulse, FfmpegSampleProvider source,
            GainSampleProvider normalizer, EqualizerSampleProvider equalizer, DfxEffectProcessor effects,
            MonoDownmixSampleProvider mono, double startSeconds, int sampleRate, int channels,
            int sourceChannels, float volume, bool paused)
        {
            _owner = owner;
            _pulse = pulse;
            _source = source;
            Normalizer = normalizer;
            Equalizer = equalizer;
            Effects = effects;
            Mono = mono;
            _chain = mono;
            _startSeconds = startSeconds;
            _sampleRate = sampleRate;
            _channels = channels;
            SourceChannels = sourceChannels;
            _volume = volume;
            TargetVolume = volume;
            Paused = paused;
            _running = new ManualResetEventSlim(!paused);
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "str-amp audio" };
        }

        public void Start() => _thread.Start();

        public double PositionSeconds
        {
            get
            {
                var played = _framesWritten / (double)_sampleRate;
                // While playing, the last buffered stretch hasn't reached the speakers yet. Paused or
                // finished, it drains out, so everything written counts.
                var latency = Paused || Finished ? 0 : Interlocked.Read(ref _latencyMicros) / 1_000_000.0;
                return _startSeconds + Math.Max(0, played - latency);
            }
        }

        public void SetPaused(bool paused)
        {
            if (Finished || Paused == paused)
                return;

            Paused = paused;
            if (paused)
                _running.Reset();
            else
                _running.Set();
        }

        private unsafe void RenderLoop()
        {
            var buffer = new float[BufferFrames * _channels];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    _running.Wait(_cts.Token);

                    var floats = _chain.Read(buffer);
                    var frames = floats / _channels;
                    if (frames == 0)
                        break;

                    // Volume as a short ramp across the buffer, so changing it never clicks.
                    var target = TargetVolume;
                    var start = _volume;
                    var count = frames * _channels;
                    if (start != target || target != 1f)
                    {
                        for (var f = 0; f < frames; f++)
                        {
                            var v = start + (target - start) * (f + 1) / frames;
                            for (var c = 0; c < _channels; c++)
                                buffer[f * _channels + c] *= v;
                        }
                        _volume = target;
                    }

                    fixed (float* p = buffer)
                    {
                        if (PulseSimple.Write(_pulse, p, (nuint)(count * sizeof(float)), out _) < 0)
                            break;
                    }

                    _framesWritten += frames;
                    _latencyMicros = (long)PulseSimple.GetLatency(_pulse, out _);
                }

                if (!_cts.IsCancellationRequested && _source.ReachedEnd && !_source.Failed)
                {
                    PulseSimple.Drain(_pulse, out _);
                    if (!_cts.IsCancellationRequested)
                    {
                        Finished = true;
                        _owner.OnSessionEnded(this);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Closed while paused.
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _cts.Cancel();
            _running.Set();
            _source.Dispose();
            if (Thread.CurrentThread != _thread)
                _thread.Join(2000);

            PulseSimple.Free(_pulse);
            Effects.Dispose();
            _running.Dispose();
            _cts.Dispose();
        }
    }
}
