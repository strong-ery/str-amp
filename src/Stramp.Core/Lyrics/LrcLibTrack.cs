using System.Text.Json.Serialization;

namespace Stramp.Core.Lyrics;

/// <summary>One record from lrclib.net. Field names mirror the API's JSON.</summary>
public sealed class LrcLibTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("trackName")]
    public string? TrackName { get; set; }

    [JsonPropertyName("artistName")]
    public string? ArtistName { get; set; }

    [JsonPropertyName("albumName")]
    public string? AlbumName { get; set; }

    /// <summary>Track length in seconds, as LRCLIB has it. Used to score search candidates.</summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    /// <summary>The uploader marked this track as having no words at all.</summary>
    [JsonPropertyName("instrumental")]
    public bool Instrumental { get; set; }

    [JsonPropertyName("plainLyrics")]
    public string? PlainLyrics { get; set; }

    /// <summary>Timestamped .lrc text. Null when only an unsynced transcription was uploaded.</summary>
    [JsonPropertyName("syncedLyrics")]
    public string? SyncedLyrics { get; set; }

    public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasPlain => !string.IsNullOrWhiteSpace(PlainLyrics);

    /// <summary>The synced copy when there is one, since it drives the highlight; plain otherwise.</summary>
    public string? BestLyrics => HasSynced ? SyncedLyrics : HasPlain ? PlainLyrics : null;
}
