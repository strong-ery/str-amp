using NAudio.Wave;
using Stramp.Core.Playback;

namespace Stramp.Audio.Effects;

/// <summary>
/// Runs the five enhancement effects through FxSound's own DSP library.
///
/// This replaces a chain str-amp implemented itself. The effects, their names, their 0-10 scale and
/// the preset files all came from FxSound to begin with, so the honest way to get FxSound's sound
/// is to let FxSound's code produce it. What arrives here is a float buffer at the stream's rate;
/// it goes to the library as it is and comes back processed in place.
///
/// The library keeps no lock of its own and is not safe to call from two threads. Settings are
/// staged under <see cref="_settingsGate"/> and applied by the audio thread at the top of the next
/// <see cref="Read"/>. The handle itself is guarded separately by <see cref="_nativeGate"/>, held
/// across every call into the library including the one that frees it: without that, disposing
/// while a buffer is in flight would free the handle under the audio thread. Decoding happens
/// before that lock is taken, so a slow read never holds it.
///
/// With every amount at zero the library is not transparent: measured on a track it still applied
/// its own limiter, pulling the peak from full scale to -0.30 dBFS and leaving no sample unchanged.
/// So switching the effects off has to mean not using its output at all, which is what
/// <see cref="AudioEffectSettings.IsNeutral"/> selects here. The buffer is still fed through --
/// only the result is dropped -- because the library holds reverb tanks and level followers, and
/// one that had been starved while the effects were off would empty an old moment of the track
/// into the first buffer after they came back on.
///
/// If the DLL cannot be loaded this falls back to passing audio through untouched rather than
/// failing playback. A missing native dependency should cost the enhancements, not the music.
/// </summary>
internal sealed class DfxEffectProcessor : ISampleProvider, IDisposable
{
    /// <summary>Selects the library's float path, where nothing is converted or clipped.</summary>
    private const int FloatBits = 32;

    private readonly ISampleProvider _source;

    /// <summary>Guards the staged settings. Never held across a call into the library.</summary>
    private readonly object _settingsGate = new();

    /// <summary>Guards <see cref="_handle"/>, held for the whole of any call that uses it.</summary>
    private readonly object _nativeGate = new();

    private IntPtr _handle;
    private AudioEffectSettings? _pending;
    private bool _disposed;

    /// <summary>What was last handed to the library, so a no-op update costs nothing.</summary>
    private AudioEffectSettings _applied = AudioEffectSettings.None;

    /// <summary>
    /// Holds the dry signal while the library processes a copy, for the bypassed case. Grown to
    /// fit the largest buffer seen; the audio thread is the only thing that touches it.
    /// </summary>
    private float[] _dry = [];

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>True when the library loaded and audio is actually going through it.</summary>
    public bool IsActive => _handle != IntPtr.Zero;

    /// <summary>Why the library is not in use, for the log. Null when it is.</summary>
    public string? UnavailableReason { get; }

    public DfxEffectProcessor(ISampleProvider source, AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);

        _source = source;

        try
        {
            _handle = DfxInterop.dfx_create();
            if (_handle == IntPtr.Zero)
            {
                UnavailableReason = "DfxBridge returned no handle.";
                return;
            }

            var format = DfxInterop.dfx_set_signal_format(
                _handle, FloatBits, WaveFormat.Channels, WaveFormat.SampleRate, FloatBits);
            if (format != DfxInterop.Okay)
            {
                UnavailableReason = $"DfxBridge rejected {WaveFormat.SampleRate} Hz / "
                                  + $"{WaveFormat.Channels} ch (code {format}).";
                Destroy();
                return;
            }

            DfxInterop.dfx_power_on(_handle, 1);

            // str-amp's equalizer runs upstream of this and is the one the user sees. Leaving the
            // library's own on as well would put two curves on the signal.
            DfxInterop.dfx_eq_on(_handle, 0);

            Apply(settings.Clamped());
        }
        catch (DllNotFoundException e)
        {
            UnavailableReason = $"DfxBridge.dll was not found: {e.Message}";
            _handle = IntPtr.Zero;
        }
        catch (EntryPointNotFoundException e)
        {
            UnavailableReason = $"DfxBridge.dll is missing an entry point: {e.Message}";
            Destroy();
        }
        catch (BadImageFormatException e)
        {
            UnavailableReason = $"DfxBridge.dll is not loadable in this process: {e.Message}";
            _handle = IntPtr.Zero;
        }
    }

    /// <summary>Stages new amounts; the audio thread picks them up on its next pass.</summary>
    public void Update(AudioEffectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_settingsGate)
            _pending = settings.Clamped();
    }

    /// <summary>
    /// For seeks and track changes. The library holds delay lines and level followers that would
    /// otherwise carry a moment of the old position into the new one; re-sending the settings is
    /// what makes it rebuild them.
    /// </summary>
    public void Reset()
    {
        lock (_settingsGate)
            _pending = _applied;
    }

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);

        AudioEffectSettings? pending;
        lock (_settingsGate)
        {
            pending = _pending;
            _pending = null;
        }

        var channels = WaveFormat.Channels;
        var frames = read / channels;

        lock (_nativeGate)
        {
            if (_handle == IntPtr.Zero)
                return read;

            if (pending is not null)
                Apply(pending);

            if (frames <= 0)
                return read;

            // Keep the dry signal when the effects are off, so what the library returns can be
            // dropped while its delay lines still get fed. See the note on this class.
            var bypass = _applied.IsNeutral;
            if (bypass)
            {
                if (_dry.Length < read)
                    _dry = new float[read];
                buffer[..read].CopyTo(_dry);
            }

            unsafe
            {
                fixed (float* samples = buffer)
                {
                    // In place: the library copies input to output itself before processing. The
                    // duplicate check is for drivers that re-send a buffer; str-amp never does.
                    DfxInterop.dfx_process_float(_handle, samples, samples, frames, 0);
                }
            }

            if (bypass)
                _dry.AsSpan(0, read).CopyTo(buffer);
        }

        return read;
    }

    /// <summary>Caller must hold <see cref="_nativeGate"/>.</summary>
    private void Apply(AudioEffectSettings settings)
    {
        if (_handle == IntPtr.Zero)
            return;

        DfxInterop.dfx_set_effect(_handle, DfxInterop.Effect.Fidelity, (float)settings.Clarity);
        DfxInterop.dfx_set_effect(_handle, DfxInterop.Effect.Ambience, (float)settings.Ambience);
        DfxInterop.dfx_set_effect(_handle, DfxInterop.Effect.Surround, (float)settings.Surround);
        DfxInterop.dfx_set_effect(_handle, DfxInterop.Effect.DynamicBoost, (float)settings.DynamicBoost);
        DfxInterop.dfx_set_effect(_handle, DfxInterop.Effect.Bass, (float)settings.BassBoost);

        _applied = settings;
    }

    private void Destroy()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero)
            DfxInterop.dfx_destroy(handle);
    }

    public void Dispose()
    {
        // Waits for a buffer in flight to finish before the handle goes away.
        lock (_nativeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Destroy();
        }
    }
}
