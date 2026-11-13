using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.MediaFoundation;
using NAudio.Wave;
using System.Runtime.Versioning;
using Stramp.Audio.Dsp;
using Stramp.Audio.Effects;
using Stramp.Core.Playback;

namespace Stramp.Audio;

/// <summary>
/// Native Windows playback: Media Foundation decodes to 32-bit float and WASAPI sends it through
/// the standard shared-mode media path. The stream follows Windows' default/per-app output route.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiMediaPlayer : IMediaPlayer
{
    /// <summary>
    /// Band centres used until the user picks their own. Ten bands, matching the layout the
    /// bundled presets were authored against.
    /// </summary>
    private static readonly float[] DefaultBandFrequencies =
        [63, 110, 250, 370, 650, 1200, 2130, 4550, 6850, 16000];

    private readonly object _gate = new();
    private readonly Timer _positionTimer;
    private WasapiPlayer? _output;
    private MMDevice? _outputDevice;
    private MediaFoundationReader? _reader;
    private GainSampleProvider? _normalizer;
    private EqualizerSampleProvider? _equalizer;
    private DfxEffectProcessor? _effects;
    private MonoDownmixSampleProvider? _mono;
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
    private string? _tempPlaybackFile;
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
                ApplyOutputVolume();
            }
        }
    }

    public bool Muted
    {
        get
        {
            lock (_gate)
                return _muted;
        }
        set
        {
            lock (_gate)
            {
                _muted = value;
                ApplyOutputVolume();
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

    public string? OutputDeviceId
    {
        get
        {
            lock (_gate)
                return _outputDeviceId;
        }
        set
        {
            lock (_gate)
            {
                var id = string.IsNullOrWhiteSpace(value) ? null : value;
                if (string.Equals(_outputDeviceId, id, StringComparison.OrdinalIgnoreCase))
                    return;

                _outputDeviceId = id;
                if (_disposed || _currentPath is null)
                    return;

                // Re-open on the new endpoint where the old one left off. WASAPI streams are bound
                // to a device for their lifetime, so switching means building the chain again.
                var position = _reader?.CurrentTime.TotalSeconds ?? 0;
                var wasPlaying = _output?.PlaybackState == PlaybackState.Playing;
                OpenPlayback(_currentPath, position, wasPlaying);
            }
        }
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            var devices = new List<AudioOutputDevice>(endpoints.Count);
            for (var i = 0; i < endpoints.Count; i++)
            {
                using var device = endpoints[i];
                devices.Add(new AudioOutputDevice(device.ID, device.FriendlyName));
            }
            return devices;
        }
        catch (Exception)
        {
            // Endpoint enumeration is best-effort: a caller that gets nothing back just shows the
            // system-default entry rather than failing.
            return [];
        }
    }

    public bool MonoOutput
    {
        get
        {
            lock (_gate)
                return _monoOutput;
        }
        set
        {
            lock (_gate)
            {
                _monoOutput = value;
                _mono?.SetEnabled(value);
            }
        }
    }

    public bool SurroundSoundEnabled
    {
        get
        {
            lock (_gate)
                return _surroundSoundEnabled;
        }
        set
        {
            lock (_gate)
            {
                if (_surroundSoundEnabled == value)
                    return;

                _surroundSoundEnabled = value;
                if (_disposed || _currentPath is null)
                    return;

                if (_reader is not null && _reader.WaveFormat.Channels > 2)
                {
                    var position = _reader.CurrentTime.TotalSeconds;
                    var wasPlaying = _output?.PlaybackState == PlaybackState.Playing;
                    OpenPlayback(_currentPath, position, wasPlaying);
                }
            }
        }
    }

    public IReadOnlyList<float> DefaultEqualizerBands => DefaultBandFrequencies;

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
            OpenPlayback(path, startSeconds: 0, play: true);
        }
    }

    /// <summary>
    /// Builds the whole decode -> DSP -> output chain for a track and hands it to the endpoint.
    /// The caller must hold <see cref="_gate"/>.
    /// </summary>
    private void OpenPlayback(string path, double startSeconds, bool play)
    {
        CloseCurrentPlayback();

        MediaFoundationReader? reader = null;
        try
        {
            reader = CreateReader(path);
            if (startSeconds > 0)
                reader.CurrentTime = TimeSpan.FromSeconds(
                    Math.Clamp(startSeconds, 0, reader.TotalTime.TotalSeconds));

            var device = ResolveOutputDevice(_outputDeviceId);
            var output = BuildOutput(device);

            var isMultiChannel = reader.WaveFormat.Channels > 2;
            var useSurround = isMultiChannel && _surroundSoundEnabled && CanDeviceSupportFormat(device, reader.WaveFormat);

            ISampleProvider source = reader.ToSampleProvider();
            if (!useSurround && isMultiChannel)
                source = new SurroundToStereoSampleProvider(source);

            var normalizer = new GainSampleProvider(source, _normalizationGainDb);
            var equalizer = new EqualizerSampleProvider(normalizer, _equalizerFrequencies);
            equalizer.Update(_equalizerFrequencies, _equalizerGains, _equalizerEnabled);
            var effects = new DfxEffectProcessor(equalizer, _effectSettings);

            // Last in the chain: mono is about what leaves for the speakers, so it collapses the
            // finished signal rather than something the later stages then widen.
            var mono = new MonoDownmixSampleProvider(effects, _monoOutput);

            try
            {
                output.Init(mono);
            }
            catch (Exception) when (useSurround)
            {
                // If native surround output failed on this endpoint, fall back to lossless stereo downmix.
                output.Dispose();
                source = new SurroundToStereoSampleProvider(reader.ToSampleProvider());
                normalizer = new GainSampleProvider(source, _normalizationGainDb);
                equalizer = new EqualizerSampleProvider(normalizer, _equalizerFrequencies);
                equalizer.Update(_equalizerFrequencies, _equalizerGains, _equalizerEnabled);
                effects = new DfxEffectProcessor(equalizer, _effectSettings);
                mono = new MonoDownmixSampleProvider(effects, _monoOutput);

                output = BuildOutput(device);
                output.Init(mono);
            }

            output.Volume = EffectiveOutputVolume;
            output.PlaybackStopped += (_, e) => HandlePlaybackStopped(output, e);

            _currentPath = path;
            _reader = reader;
            _normalizer = normalizer;
            _equalizer = equalizer;
            _effects = effects;
            _mono = mono;
            _outputDevice = device;
            _output = output;

            if (!play)
                return;

            output.Play();
            _positionTimer.Change(0, 100);
        }
        catch
        {
            reader?.Dispose();
            if (_tempPlaybackFile is not null)
            {
                try { File.Delete(_tempPlaybackFile); } catch { }
                _tempPlaybackFile = null;
            }
            throw;
        }
    }

    private MediaFoundationReader CreateReader(string path)
    {
        var settings = new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
            RepositionInRead = true,
        };

        try
        {
            return new MediaFoundationReader(path, settings);
        }
        catch (Exception ex) when (ex is MediaFoundationException or System.Runtime.InteropServices.COMException)
        {
            // Windows Media Foundation's FLAC byte stream handler fails with 0xC00D36C4 when
            // metadata blocks (such as high-res embedded cover art) exceed its internal buffer.
            // Create a temporary FLAC with picture metadata blocks stripped, keeping audio frames intact.
            if (path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            {
                var tempFlac = Path.Combine(Path.GetTempPath(), $"stramp_playback_{Guid.NewGuid():N}.flac");
                if (FlacSanitizer.TryStripPictureBlocks(path, tempFlac))
                {
                    try
                    {
                        var strippedReader = new MediaFoundationReader(tempFlac, settings);
                        _tempPlaybackFile = tempFlac;
                        return strippedReader;
                    }
                    catch
                    {
                        try { File.Delete(tempFlac); } catch { }
                    }
                }
            }

            // Fallback for other formats unsupported by Windows Media Foundation (e.g. .ogg, .opus)
            // or unusual files if ffmpeg is available.
            var tempWav = Path.Combine(Path.GetTempPath(), $"stramp_playback_{Guid.NewGuid():N}.wav");
            if (TryDecodeWithFfmpeg(path, tempWav))
            {
                try
                {
                    var wavReader = new MediaFoundationReader(tempWav, settings);
                    _tempPlaybackFile = tempWav;
                    return wavReader;
                }
                catch
                {
                    try { File.Delete(tempWav); } catch { }
                }
            }

            throw;
        }
    }

    private static bool TryDecodeWithFfmpeg(string sourcePath, string targetWavPath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("pcm_f32le");
            psi.ArgumentList.Add(targetWavPath);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is null)
                return false;

            if (!process.WaitForExit(10000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0 && File.Exists(targetWavPath) && new FileInfo(targetWavPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the stream on <paramref name="device"/>, or on whatever the system default currently
    /// is when that is null.
    /// </summary>
    private static WasapiPlayer BuildOutput(MMDevice? device)
    {
        // Shared mode is intentional: this is the normal Windows music-app path, supports the
        // system volume mixer, and lets Windows perform the one required 44.1 -> 48 kHz conversion
        // for devices such as the user's ME6S.
        var builder = new WasapiPlayerBuilder()
            .WithEventSync()
            .WithLatency(100)
            .WithMmcssThreadPriority("Audio")
            .WithCategory(AudioStreamCategory.Media);

        // A device the user picked explicitly stays put; only the "system default" choice follows
        // Windows around (and honors per-app routing), which is what stream routing is for.
        if (device is not null)
            return builder.WithDevice(device).Build();

        return builder
            .WithDefaultDeviceStreamRouting()
            .BuildAsync()
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Resolves a saved endpoint id, returning null — meaning "follow the system default" — when
    /// the id is empty or names a device that is gone, disabled, or unplugged.
    /// </summary>
    private static MMDevice? ResolveOutputDevice(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active)
                return device;

            device.Dispose();
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool CanDeviceSupportFormat(MMDevice? device, WaveFormat format)
    {
        try
        {
            if (device is not null)
            {
                using var client = device.CreateAudioClient();
                return client.IsFormatSupported(AudioClientShareMode.Shared, format, out _);
            }

            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (defaultDevice is null)
                return false;

            using var defaultClient = defaultDevice.CreateAudioClient();
            return defaultClient.IsFormatSupported(AudioClientShareMode.Shared, format, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Volume as WASAPI wants it, with mute folded in. The caller must hold the lock.</summary>
    private float EffectiveOutputVolume => _muted ? 0f : (float)(_volume / 100.0);

    /// <summary>Pushes <see cref="EffectiveOutputVolume"/> to the live stream, if there is one.</summary>
    private void ApplyOutputVolume()
    {
        if (_output is not null)
            _output.Volume = EffectiveOutputVolume;
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
            _effects?.Reset();
        }
    }

    public void ApplyEqualizer(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(centreFrequencies);
        ArgumentNullException.ThrowIfNull(gainsDb);

        lock (_gate)
        {
            // An empty band list means "whatever the backend defaults to", so a caller that has no
            // saved layout yet does not have to invent one.
            var frequencies = centreFrequencies.Count > 0 ? centreFrequencies : DefaultBandFrequencies;

            _equalizerFrequencies = [.. frequencies];
            _equalizerGains = new double[_equalizerFrequencies.Length];
            for (var i = 0; i < Math.Min(_equalizerGains.Length, gainsDb.Count); i++)
                _equalizerGains[i] = Math.Clamp(gainsDb[i], -20, 20);

            _equalizerEnabled = enabled;
            _equalizer?.Update(_equalizerFrequencies, _equalizerGains, enabled);
        }
    }

    public void ApplyEffects(AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            _effectSettings = settings.Clamped();
            _effects?.Update(_effectSettings);
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
        var device = _outputDevice;
        var effects = _effects;
        _output = null;
        _reader = null;
        _normalizer = null;
        _equalizer = null;
        _effects = null;
        _mono = null;
        _outputDevice = null;

        if (output is not null)
        {
            output.Stop();
            output.Dispose();
        }
        reader?.Dispose();

        // After the output has stopped, so the render thread is done with it. Holds a native
        // handle, which nothing else will free.
        effects?.Dispose();

        // Released only after the stream that was rendering to it. Whether the player took
        // ownership of the endpoint is unspecified, so tolerate it having been disposed already.
        try
        {
            device?.Dispose();
        }
        catch (Exception)
        {
            // Nothing left to release.
        }

        if (_tempPlaybackFile is not null)
        {
            try
            {
                File.Delete(_tempPlaybackFile);
            }
            catch
            {
                // Best-effort temp file cleanup.
            }
            _tempPlaybackFile = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _currentPath = null;
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

    /// <summary>
    /// Replaces every channel of a frame with their average, so both ears hear the same thing.
    ///
    /// Averaging rather than summing keeps the level where it was: identical channels stay at their
    /// own level instead of arriving 6 dB louder, and channels that disagree partially cancel, which
    /// is what a mono fold-down is for hearing in the first place.
    ///
    /// The switch fades over the same short ramp the gain stage uses rather than snapping, because
    /// flipping it mid-track is otherwise a step change in every channel at once — a click.
    /// </summary>
    private sealed class MonoDownmixSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly float _smoothingFactor;
        private float _blend;
        private float _targetBlend;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public MonoDownmixSampleProvider(ISampleProvider source, bool enabled)
        {
            _source = source;
            _blend = enabled ? 1 : 0;
            _targetBlend = _blend;
            _smoothingFactor = 1 - MathF.Exp(-1 / (0.02f * WaveFormat.SampleRate));
        }

        public void SetEnabled(bool enabled) =>
            Volatile.Write(ref _targetBlend, enabled ? 1 : 0);

        public int Read(Span<float> buffer)
        {
            var read = _source.Read(buffer);
            var channels = WaveFormat.Channels;
            var target = Volatile.Read(ref _targetBlend);

            // Off and fully faded out, which is the default and the common case: nothing to do.
            if (channels < 2 || (target == 0 && _blend == 0))
                return read;

            for (var frame = 0; frame + channels <= read; frame += channels)
            {
                _blend += (target - _blend) * _smoothingFactor;
                if (MathF.Abs(target - _blend) < 0.000001f)
                    _blend = target;

                var sum = 0f;
                for (var channel = 0; channel < channels; channel++)
                    sum += buffer[frame + channel];

                var mean = sum / channels;
                for (var channel = 0; channel < channels; channel++)
                    buffer[frame + channel] += (mean - buffer[frame + channel]) * _blend;
            }

            return read;
        }
    }

}
