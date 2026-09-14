using System.Text.Json.Serialization;

namespace Stramp.Core.Playback;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoopMode
{
    Off,
    Playlist,
    Track
}
