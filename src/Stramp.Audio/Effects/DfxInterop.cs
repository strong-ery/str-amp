using System.Runtime.InteropServices;

namespace Stramp.Audio.Effects;

/// <summary>
/// The entry points of DfxBridge.dll, which wraps FxSound's DSP library.
///
/// These five effects are provided by FxSound's own DfxDsp library rather than a reimplementation:
/// several of the parameter mappings are not derivable from the open-source release, and calling
/// the library directly means what plays is FxSound's actual processing, not an approximation of it.
///
/// The DLL ships prebuilt in native/prebuilt/x64. See COPYRIGHT for its source and how to rebuild.
/// </summary>
internal static partial class DfxInterop
{
    private const string Library = "DfxBridge";

    /// <summary>Effect ids, in the order DfxDsp::Effect declares them.</summary>
    internal enum Effect
    {
        Fidelity = 0,
        Ambience = 1,
        Surround = 2,
        DynamicBoost = 3,
        Bass = 4,
    }

    /// <summary>Every call returns this on success.</summary>
    internal const int Okay = 0;

    [LibraryImport(Library)]
    internal static partial IntPtr dfx_create();

    [LibraryImport(Library)]
    internal static partial void dfx_destroy(IntPtr handle);

    /// <summary>
    /// Declares the stream. Pass 32 for both bit counts to select the float path, where the
    /// library works on the buffer in place as its own float type and converts nothing.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial int dfx_set_signal_format(IntPtr handle, int bitsPerSample, int channels,
                                                      int sampleRate, int validBits);

    /// <summary>
    /// Processes interleaved floats, full scale at +/-1.0. <paramref name="sampleSets"/> counts
    /// frames, not samples. Input and output may be the same buffer.
    /// </summary>
    [LibraryImport(Library)]
    internal static unsafe partial int dfx_process_float(IntPtr handle, float* input, float* output,
                                                         int sampleSets, int checkForDuplicateBuffers);

    [LibraryImport(Library)]
    internal static partial void dfx_power_on(IntPtr handle, [MarshalAs(UnmanagedType.I4)] int on);

    /// <summary>
    /// Sets one effect on FxSound's own 0-10 scale, the numbers on its sliders. Zero also switches
    /// the effect off. Note <c>dfx_get_effect</c> is not symmetric with this and returns 0-1, which
    /// is why str-amp keeps its own copy of what it set rather than reading values back.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial void dfx_set_effect(IntPtr handle, Effect effect, float value);

    /// <summary>
    /// Switches the library's own equalizer. str-amp turns it off and keeps its own: this one has
    /// 31 fixed bands at a fixed Q, where str-amp's has ten the user can retune and solves for
    /// band interaction so the curve it draws is the curve that comes out.
    /// </summary>
    [LibraryImport(Library)]
    internal static partial void dfx_eq_on(IntPtr handle, [MarshalAs(UnmanagedType.I4)] int on);
}
