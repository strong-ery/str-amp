using System.Text.Json;
using Stramp.Core.Playback;
using Stramp.Core.Settings;

namespace Stramp.Core.Tests;

public class LoopModeTests
{
    [Fact]
    public void AppSettings_DefaultLoopMode_IsOff()
    {
        var settings = new AppSettings();
        Assert.Equal(LoopMode.Off, settings.LoopMode);
    }

    [Theory]
    [InlineData(LoopMode.Off, "\"Off\"")]
    [InlineData(LoopMode.Playlist, "\"Playlist\"")]
    [InlineData(LoopMode.Track, "\"Track\"")]
    public void AppSettings_SerializesLoopModeAsString(LoopMode mode, string expectedJsonFragment)
    {
        var settings = new AppSettings { LoopMode = mode };
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains(expectedJsonFragment, json);

        var deserialized = JsonSerializer.Deserialize<AppSettings>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(mode, deserialized.LoopMode);
    }

    [Fact]
    public void LoopMode_TransitionsCorrectly()
    {
        var mode = LoopMode.Off;

        mode = mode switch
        {
            LoopMode.Off => LoopMode.Playlist,
            LoopMode.Playlist => LoopMode.Track,
            LoopMode.Track => LoopMode.Off,
            _ => LoopMode.Off
        };
        Assert.Equal(LoopMode.Playlist, mode);

        mode = mode switch
        {
            LoopMode.Off => LoopMode.Playlist,
            LoopMode.Playlist => LoopMode.Track,
            LoopMode.Track => LoopMode.Off,
            _ => LoopMode.Off
        };
        Assert.Equal(LoopMode.Track, mode);

        mode = mode switch
        {
            LoopMode.Off => LoopMode.Playlist,
            LoopMode.Playlist => LoopMode.Track,
            LoopMode.Track => LoopMode.Off,
            _ => LoopMode.Off
        };
        Assert.Equal(LoopMode.Off, mode);
    }
}
