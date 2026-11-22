using System.Runtime.InteropServices;

namespace Stramp.Audio.Linux;

/// <summary>
/// The PulseAudio "simple" API (libpulse-simple). PipeWire ships a compatible server
/// (pipewire-pulse), so this is the one output path that works on effectively every desktop Linux.
/// The simple API is blocking, which is exactly what a pull-model render thread wants.
/// </summary>
internal static partial class PulseSimple
{
    private const string Library = "libpulse-simple.so.0";

    internal const int StreamPlayback = 1;
    internal const int SampleFloat32LE = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferAttr
    {
        public uint MaxLength;
        public uint TLength;
        public uint PreBuf;
        public uint MinReq;
        public uint FragSize;
    }

    [DllImport(Library, EntryPoint = "pa_simple_new")]
    private static extern IntPtr New(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int direction,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? device,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string streamName,
        ref SampleSpec spec,
        IntPtr channelMap,
        ref BufferAttr attr,
        out int error);

    [DllImport(Library, EntryPoint = "pa_simple_write")]
    internal static extern unsafe int Write(IntPtr s, void* data, nuint bytes, out int error);

    [DllImport(Library, EntryPoint = "pa_simple_drain")]
    internal static extern int Drain(IntPtr s, out int error);

    [DllImport(Library, EntryPoint = "pa_simple_flush")]
    internal static extern int Flush(IntPtr s, out int error);

    [DllImport(Library, EntryPoint = "pa_simple_get_latency")]
    internal static extern ulong GetLatency(IntPtr s, out int error);

    [DllImport(Library, EntryPoint = "pa_simple_free")]
    internal static extern void Free(IntPtr s);

    /// <summary>
    /// Connects a playback stream. <paramref name="device"/> is a sink name, or null for the
    /// server's default (which also lets the server move the stream when the default changes).
    /// </summary>
    internal static IntPtr Open(string? device, int sampleRate, int channels, int latencyMs, out int error)
    {
        var spec = new SampleSpec { Format = SampleFloat32LE, Rate = (uint)sampleRate, Channels = (byte)channels };
        var bytesPerMs = (uint)(sampleRate * channels * sizeof(float) / 1000);
        var attr = new BufferAttr
        {
            MaxLength = uint.MaxValue,
            TLength = bytesPerMs * (uint)latencyMs,
            PreBuf = uint.MaxValue,
            MinReq = uint.MaxValue,
            FragSize = uint.MaxValue,
        };
        return New(null, "str-amp", StreamPlayback, device, "Music", ref spec, IntPtr.Zero, ref attr, out error);
    }
}
