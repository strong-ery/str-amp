using Stramp.Audio.Linux;
using Stramp.Core.Playback;

namespace Stramp.Audio;

/// <summary>Picks the playback backend for the platform the app is running on.</summary>
public static class MediaPlayerFactory
{
    public static IMediaPlayer Create()
    {
        if (OperatingSystem.IsWindows())
            return new WasapiMediaPlayer();
        if (OperatingSystem.IsLinux())
            return new LinuxMediaPlayer();

        throw new PlatformNotSupportedException("str-amp has no audio backend for this platform.");
    }
}
