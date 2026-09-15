using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Runtime.Versioning;
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
    private AudioEffectChain? _effects;
    private MonoDownmixSampleProvider? _mono;
    private float[] _equalizerFrequencies = [.. DefaultBandFrequencies];
    private double[] _equalizerGains = new double[DefaultBandFrequencies.Length];
    private bool _equalizerEnabled;
    private AudioEffectSettings _effectSettings = AudioEffectSettings.None;
    private double _volume = 100;
    private bool _muted;
    private double _normalizationGainDb;
    private bool _monoOutput;
    private string? _outputDeviceId;
    private string? _currentPath;
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

        var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
            RepositionInRead = true,
        });
        if (startSeconds > 0)
            reader.CurrentTime = TimeSpan.FromSeconds(
                Math.Clamp(startSeconds, 0, reader.TotalTime.TotalSeconds));

        var normalizer = new GainSampleProvider(reader.ToSampleProvider(), _normalizationGainDb);
        var equalizer = new EqualizerSampleProvider(normalizer, _equalizerFrequencies);
        equalizer.Update(_equalizerFrequencies, _equalizerGains, _equalizerEnabled);
        var effects = new AudioEffectChain(equalizer, _effectSettings);

        // Last in the chain, after the limiter: mono is about what leaves for the speakers, so
        // it collapses the finished signal rather than something the later stages then widen.
        var mono = new MonoDownmixSampleProvider(effects, _monoOutput);

        var device = ResolveOutputDevice(_outputDeviceId);
        var output = BuildOutput(device);

        output.Init(mono);
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

    /// <summary>
    /// Interleaved, per-channel graphic EQ: a bank of peaking biquads, one per band, each sized to
    /// the gap it owns. Because neighbouring bells overlap, the filters are not driven straight off
    /// the sliders — <see cref="SolveBandGains"/> first works out the gains whose combined response
    /// passes through the requested level at every band centre, so a slider moves its own part of
    /// the spectrum and leaves the rest where it was.
    ///
    /// Boosts are real boosts: nothing is pre-attenuated to make room, so raising one band does not
    /// quieten the others. Gain and centre-frequency changes crossfade rather than snap. Boosting
    /// can push the signal past full scale; holding it back is the job of the peak limiter at the
    /// end of the chain, not of this stage.
    /// </summary>
    private sealed class EqualizerSampleProvider : ISampleProvider
    {
        /// <summary>Gain difference below which a band counts as unchanged / flat.</summary>
        private const double GainEpsilon = 1e-9;

        /// <summary>Centre-frequency difference below which a band counts as unmoved.</summary>
        private const float FrequencyEpsilon = 1e-3f;

        /// <summary>Length of the crossfade that retunes a band after a slider moves.</summary>
        private const float RetuneSeconds = 0.02f;

        /// <summary>
        /// Bands centred above this fraction of the sample rate are left flat: a peaking biquad
        /// that close to Nyquist is too warped to be the band it claims to be.
        /// </summary>
        private const float MaxCentreFrequencyRatio = 0.45f;

        /// <summary>Lowest centre frequency a band may be placed at.</summary>
        private const float MinCentreFrequency = 10;

        /// <summary>
        /// Smallest ratio allowed between one band's centre and the next. Bands are kept strictly
        /// ascending so band <i>n</i> on screen is always band <i>n</i> in the bank, but the margin
        /// is deliberately tiny — some presets really do put two bands a sixth of an octave apart.
        /// </summary>
        private const float MinBandSpacingRatio = 1.01f;

        /// <summary>Refinement passes used to linearise the band-interaction solve.</summary>
        private const int SolverPasses = 3;

        /// <summary>
        /// Ridge term on the interaction solve. Neighbouring bells are nearly collinear, so an
        /// undamped fit answers extreme slider combinations with enormous opposing gains; this
        /// costs about 0.02 dB of accuracy at the band centres and removes that failure mode.
        /// </summary>
        private const double SolverDamping = 0.0005;

        /// <summary>Filter gain past which the interaction solve is treated as unusable.</summary>
        private const double MaxSolvedGainDb = 60;

        /// <summary>
        /// How far each band's bell is widened beyond the gap it owns. Wider bells overlap more,
        /// which the solve below compensates for at the centres and which fills in the response
        /// between them; past roughly 2x the bands become collinear and the solve falls apart.
        /// </summary>
        private const double BandwidthScale = 1.5;

        private readonly ISampleProvider _source;
        private readonly int _crossfadeSamples;
        private readonly object _settingsGate = new();
        private BandPlan? _pendingPlan;
        private bool _resetRequested;

        // Touched only by the audio thread once construction is done.
        private BandPlan _activePlan;
        private CrossfadingBiQuadFilter[][]? _filters;
        private int _tailSamples;

        public WaveFormat WaveFormat => _source.WaveFormat;

        public EqualizerSampleProvider(ISampleProvider source, IReadOnlyList<float> frequencies)
        {
            ArgumentNullException.ThrowIfNull(frequencies);
            _source = source;
            _crossfadeSamples = Math.Max(1, (int)(RetuneSeconds * WaveFormat.SampleRate));
            _activePlan = CreatePlan(frequencies, new double[frequencies.Count], enabled: false);
        }

        public int Read(Span<float> buffer)
        {
            var read = _source.Read(buffer);

            if (!RefreshFilters() || _filters is null)
                return read;

            var channels = WaveFormat.Channels;
            for (var frame = 0; frame + channels <= read; frame += channels)
            {
                for (var channel = 0; channel < channels; channel++)
                {
                    var sample = buffer[frame + channel];
                    foreach (var filter in _filters[channel])
                        sample = filter.Transform(sample);

                    // A bad sample from the decoder can latch in the feedback path; flush the
                    // delay lines rather than play out the result of a diverged filter.
                    if (!float.IsFinite(sample))
                    {
                        ResetFilterState();
                        sample = 0;
                    }

                    buffer[frame + channel] = sample;
                }
            }

            if (_tailSamples > 0)
                _tailSamples = Math.Max(0, _tailSamples - read);

            return read;
        }

        /// <summary>
        /// Stages a new band layout. Everything expensive — sanitising the frequencies, sizing each
        /// band's Q, and solving the interaction between them — happens here on the caller's thread,
        /// so the audio thread only ever picks up a finished plan.
        /// </summary>
        public void Update(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled)
        {
            ArgumentNullException.ThrowIfNull(centreFrequencies);
            ArgumentNullException.ThrowIfNull(gainsDb);

            var plan = CreatePlan(centreFrequencies, gainsDb, enabled);
            lock (_settingsGate)
                _pendingPlan = plan;
        }

        public void Reset()
        {
            lock (_settingsGate)
                _resetRequested = true;
        }

        private BandPlan CreatePlan(IReadOnlyList<float> centreFrequencies, IReadOnlyList<double> gainsDb, bool enabled)
        {
            var frequencies = SanitiseFrequencies(centreFrequencies);
            var qFactors = ComputeQFactors(frequencies);

            var target = new double[frequencies.Length];
            if (enabled)
                for (var i = 0; i < Math.Min(target.Length, gainsDb.Count); i++)
                    target[i] = Math.Clamp(gainsDb[i], -20, 20);

            // "Off" is a flat bank rather than a bypass, so switching it fades instead of cutting;
            // a peaking biquad at 0 dB is an exact pass-through.
            var filterGains = HasAnyGain(target)
                ? SolveBandGains(frequencies, qFactors, target)
                : target;

            return new BandPlan(frequencies, qFactors, filterGains, HasAnyGain(filterGains));
        }

        /// <summary>
        /// Picks up a pending plan and reports whether the bank still has to run. A flattened EQ
        /// keeps processing until its bands have crossfaded back to unity, and only then bypasses.
        /// </summary>
        private bool RefreshFilters()
        {
            BandPlan? incoming;
            bool reset;
            lock (_settingsGate)
            {
                reset = _resetRequested;
                _resetRequested = false;

                if (_pendingPlan is null && !reset)
                    return _filters is not null && (_tailSamples > 0 || _activePlan.HasAnyGain);

                // Restarting a crossfade that is still running snaps the output back to the old
                // response, and dragging a slider sends updates far faster than a crossfade takes.
                // Hold the newest plan and pick it up when the one in flight finishes.
                if (_pendingPlan is not null && _tailSamples > 0 && !reset)
                    return true;

                incoming = _pendingPlan;
                _pendingPlan = null;
            }

            if (reset)
            {
                ResetFilterState();
                _tailSamples = 0;
            }

            var plan = incoming ?? _activePlan;

            // A different band count is a different bank; there is nothing to crossfade from.
            if (_filters is null || plan.Frequencies.Length != _activePlan.Frequencies.Length)
            {
                _activePlan = plan;
                BuildFilters(plan);
                return plan.HasAnyGain;
            }

            for (var band = 0; band < plan.Frequencies.Length; band++)
            {
                var moved = Math.Abs(plan.Frequencies[band] - _activePlan.Frequencies[band]) >= FrequencyEpsilon;
                var regained = Math.Abs(plan.FilterGains[band] - _activePlan.FilterGains[band]) >= GainEpsilon;
                if (!moved && !regained)
                    continue;

                for (var channel = 0; channel < _filters.Length; channel++)
                {
                    var filter = _filters[channel][band];
                    filter.Standby.SetPeakingEq(
                        WaveFormat.SampleRate, CentreFrequency(plan, band), plan.QFactors[band], BandGain(plan, band));
                    filter.BeginCrossfade();
                }

                _tailSamples = _crossfadeSamples * WaveFormat.Channels;
            }

            _activePlan = plan;
            return _tailSamples > 0 || plan.HasAnyGain;
        }

        private void BuildFilters(BandPlan plan)
        {
            var channels = WaveFormat.Channels;
            _filters = new CrossfadingBiQuadFilter[channels][];
            for (var channel = 0; channel < channels; channel++)
            {
                _filters[channel] = new CrossfadingBiQuadFilter[plan.Frequencies.Length];
                for (var band = 0; band < plan.Frequencies.Length; band++)
                {
                    var frequency = CentreFrequency(plan, band);
                    var q = plan.QFactors[band];
                    var gainDb = BandGain(plan, band);
                    _filters[channel][band] = new CrossfadingBiQuadFilter(
                        BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, frequency, q, gainDb),
                        BiQuadFilter.PeakingEQ(WaveFormat.SampleRate, frequency, q, gainDb),
                        _crossfadeSamples);
                }
            }
        }

        private void ResetFilterState()
        {
            if (_filters is not null)
                foreach (var channel in _filters)
                    foreach (var filter in channel)
                        filter.Reset();
        }

        /// <summary>
        /// Puts a caller-supplied band list into a shape the filter bank can use: finite, positive,
        /// strictly ascending and inside the sample rate. Bands are nudged rather than sorted, so a
        /// frequency typed under one slider can never jump to a different slider.
        /// </summary>
        private float[] SanitiseFrequencies(IReadOnlyList<float> centreFrequencies)
        {
            var ceiling = WaveFormat.SampleRate * MaxCentreFrequencyRatio;
            var result = new float[centreFrequencies.Count];
            var floor = MinCentreFrequency;

            for (var i = 0; i < result.Length; i++)
            {
                var value = centreFrequencies[i];
                if (!float.IsFinite(value) || value <= 0)
                    value = floor;

                // Keep ascending, but never push a band past what the sample rate can represent —
                // one parked at the ceiling is rendered flat rather than being misplaced.
                result[i] = Math.Min(Math.Max(value, floor), ceiling);
                floor = result[i] * MinBandSpacingRatio;
            }

            return result;
        }

        /// <summary>
        /// Whether a band's centre is far enough below Nyquist for a peaking filter to actually be
        /// the band it claims to be. At 48 kHz a 16 kHz band is; at 22 kHz it is not.
        /// </summary>
        private bool IsRealisable(BandPlan plan, int band) =>
            plan.Frequencies[band] < WaveFormat.SampleRate * MaxCentreFrequencyRatio;

        /// <summary>Centre frequency the biquad is built at; always legal for the sample rate.</summary>
        private float CentreFrequency(BandPlan plan, int band) =>
            Math.Min(plan.Frequencies[band], WaveFormat.SampleRate * MaxCentreFrequencyRatio);

        /// <summary>Gain a band's filter is driven at: zero, i.e. pass-through, if unrealisable.</summary>
        private float BandGain(BandPlan plan, int band) =>
            IsRealisable(plan, band) ? (float)plan.FilterGains[band] : 0;

        private static bool HasAnyGain(double[] gains)
        {
            foreach (var gain in gains)
                if (Math.Abs(gain) >= GainEpsilon)
                    return true;
            return false;
        }

        /// <summary>
        /// Turns slider positions into the filter gains that actually produce them. Neighbouring
        /// bells overlap, so driving each filter straight off its slider stacks the skirts and
        /// overshoots — ten sliders at +6 dB measure nearer +9. This solves the interaction between
        /// the bands so the response at each centre frequency lands on the number that was asked
        /// for, which is what makes one slider move one part of the spectrum and nothing else.
        /// </summary>
        private double[] SolveBandGains(float[] frequencies, float[] qFactors, double[] target)
        {
            var count = target.Length;
            var solved = (double[])target.Clone();
            var ceiling = WaveFormat.SampleRate * MaxCentreFrequencyRatio;

            for (var pass = 0; pass < SolverPasses; pass++)
            {
                var interaction = new double[count, count];
                for (var column = 0; column < count; column++)
                {
                    // A bell's skirts widen with gain, so linearise around the running estimate
                    // rather than a fixed reference. A band sitting at zero still needs a usable
                    // column, since it may be asked to trim a neighbour's spill.
                    var reference = Math.Max(Math.Abs(solved[column]), 1);
                    var realisable = frequencies[column] < ceiling;
                    for (var row = 0; row < count; row++)
                        interaction[row, column] = realisable
                            ? PeakingResponseDb(frequencies[row], frequencies[column], qFactors[column], reference) / reference
                            : row == column ? 1 : 0;
                }

                // Normal equations rather than a direct solve, so the ridge term above can damp
                // the near-collinear cases instead of letting them run away.
                var normal = new double[count, count];
                var projected = new double[count];
                for (var i = 0; i < count; i++)
                {
                    for (var j = 0; j < count; j++)
                    {
                        double sum = 0;
                        for (var row = 0; row < count; row++)
                            sum += interaction[row, i] * interaction[row, j];
                        normal[i, j] = sum + (i == j ? SolverDamping : 0);
                    }

                    double rhs = 0;
                    for (var row = 0; row < count; row++)
                        rhs += interaction[row, i] * target[row];
                    projected[i] = rhs;
                }

                if (!TrySolve(normal, projected, out var next))
                    break;
                solved = next;
            }

            foreach (var gain in solved)
                if (!double.IsFinite(gain) || Math.Abs(gain) > MaxSolvedGainDb)
                    return target;

            return solved;
        }

        /// <summary>Response, in dB, that one band's peaking filter has at a given frequency.</summary>
        private double PeakingResponseDb(double frequency, float centre, float q, double gainDb)
        {
            // The RBJ peaking-EQ coefficients the biquad itself is built from.
            var a = Math.Pow(10, gainDb / 40);
            var w0 = 2 * Math.PI * centre / WaveFormat.SampleRate;
            var alpha = Math.Sin(w0) / (2 * q);
            var cosW0 = Math.Cos(w0);
            var w = 2 * Math.PI * frequency / WaveFormat.SampleRate;

            var numerator = Magnitude(1 + alpha * a, -2 * cosW0, 1 - alpha * a, w);
            var denominator = Magnitude(1 + alpha / a, -2 * cosW0, 1 - alpha / a, w);
            return denominator > 0 ? 20 * Math.Log10(numerator / denominator) : 0;
        }

        /// <summary>Magnitude of a second-order polynomial in z⁻¹ evaluated on the unit circle.</summary>
        private static double Magnitude(double c0, double c1, double c2, double w)
        {
            var real = c0 + c1 * Math.Cos(w) + c2 * Math.Cos(2 * w);
            var imaginary = -(c1 * Math.Sin(w) + c2 * Math.Sin(2 * w));
            return Math.Sqrt(real * real + imaginary * imaginary);
        }

        /// <summary>Gaussian elimination with partial pivoting; false if the system is singular.</summary>
        private static bool TrySolve(double[,] matrix, double[] rhs, out double[] solution)
        {
            var count = rhs.Length;
            var work = (double[,])matrix.Clone();
            solution = [.. rhs];

            for (var pivot = 0; pivot < count; pivot++)
            {
                var best = pivot;
                for (var row = pivot + 1; row < count; row++)
                    if (Math.Abs(work[row, pivot]) > Math.Abs(work[best, pivot]))
                        best = row;

                if (Math.Abs(work[best, pivot]) < 1e-12)
                    return false;

                if (best != pivot)
                {
                    for (var column = 0; column < count; column++)
                        (work[pivot, column], work[best, column]) = (work[best, column], work[pivot, column]);
                    (solution[pivot], solution[best]) = (solution[best], solution[pivot]);
                }

                for (var row = pivot + 1; row < count; row++)
                {
                    var factor = work[row, pivot] / work[pivot, pivot];
                    if (factor == 0)
                        continue;
                    for (var column = pivot; column < count; column++)
                        work[row, column] -= factor * work[pivot, column];
                    solution[row] -= factor * solution[pivot];
                }
            }

            for (var row = count - 1; row >= 0; row--)
            {
                var sum = solution[row];
                for (var column = row + 1; column < count; column++)
                    sum -= work[row, column] * solution[column];
                solution[row] = sum / work[row, row];
            }

            return true;
        }

        /// <summary>
        /// Derives each band's Q from the spacing of its neighbours so its bell spans roughly the
        /// gap it owns. A single Q shared by unevenly spaced bands makes the closely spaced ones
        /// pile up, which both skews the response and leaves the solve above more to undo. Since
        /// the centres are user-adjustable, this is recomputed whenever they move.
        /// </summary>
        private static float[] ComputeQFactors(float[] frequencies)
        {
            var qFactors = new float[frequencies.Length];
            for (var band = 0; band < frequencies.Length; band++)
            {
                // The wider of the two neighbouring gaps, so a band always reaches its furthest
                // neighbour; sizing to the average instead leaves a hole wherever one gap is much
                // bigger than the other.
                var below = band > 0 ? Math.Log2(frequencies[band] / frequencies[band - 1]) : 0;
                var above = band < frequencies.Length - 1
                    ? Math.Log2(frequencies[band + 1] / frequencies[band])
                    : 0;
                var octaves = Math.Max(Math.Max(below, above), 0.05) * BandwidthScale;

                // Standard bandwidth-to-Q relation for an RBJ peaking filter.
                var width = Math.Pow(2, octaves);
                qFactors[band] = (float)(Math.Sqrt(width) / (width - 1));
            }
            return qFactors;
        }

        /// <summary>
        /// A finished band layout: where the bands sit, how wide each one ended up, and the gains
        /// the filters are driven at (which are not the slider values — see
        /// <see cref="SolveBandGains"/>). Immutable, so the audio thread adopts one atomically.
        /// </summary>
        private sealed record BandPlan(
            float[] Frequencies,
            float[] QFactors,
            double[] FilterGains,
            bool HasAnyGain);
    }
}
