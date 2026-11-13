using Stramp.Core.Models;

namespace Stramp.Core.Playback;

public sealed class TrackListeningStat
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public double SecondsListened { get; set; }
    public string FormattedTime { get; set; } = "0s";
    public int PlayCount { get; set; }
}

public sealed class MonthlyListeningRecord
{
    public string Month { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double SecondsListened { get; set; }
    public string FormattedTime { get; set; } = "0s";
    public int TrackPlays { get; set; }
    public Dictionary<string, TrackListeningStat> TopTracks { get; set; } = [];
}

public sealed class ArtistListeningStat
{
    public string Artist { get; set; } = "";
    public double SecondsListened { get; set; }
    public string FormattedTime { get; set; } = "0s";
    public int PlayCount { get; set; }
}

/// <summary>
/// Persistent database tracking total and monthly listening metrics, including previous months history.
/// </summary>
public sealed class ListeningHistoryDatabase
{
    public int Version { get; set; } = 1;
    public double TotalSecondsListened { get; set; }
    public string FormattedTotalTime { get; set; } = "0s";
    public int TotalTracksPlayed { get; set; }
    public DateTime? LastListenedAtUtc { get; set; }
    public Dictionary<string, MonthlyListeningRecord> Months { get; set; } = [];

    public void AddListeningTime(double seconds, Song? song = null, DateTime? now = null)
    {
        if (seconds <= 0)
            return;

        var dt = now ?? DateTime.Now;
        TotalSecondsListened += seconds;
        FormattedTotalTime = ListeningDurationFormatter.Format(TotalSecondsListened);
        LastListenedAtUtc = DateTime.UtcNow;

        var monthKey = dt.ToString("yyyy-MM");
        if (!Months.TryGetValue(monthKey, out var record))
        {
            record = new MonthlyListeningRecord
            {
                Month = monthKey,
                DisplayName = dt.ToString("MMMM yyyy")
            };
            Months[monthKey] = record;
        }

        record.SecondsListened += seconds;
        record.DisplayName = dt.ToString("MMMM yyyy");
        record.FormattedTime = ListeningDurationFormatter.Format(record.SecondsListened);

        if (song is not null)
        {
            var trackKey = string.IsNullOrWhiteSpace(song.Path) ? $"{song.Artist} - {song.Title}" : song.Path;
            if (!record.TopTracks.TryGetValue(trackKey, out var trackStat))
            {
                trackStat = new TrackListeningStat
                {
                    Title = song.Title,
                    Artist = song.Artist,
                    Album = song.Album
                };
                record.TopTracks[trackKey] = trackStat;
            }

            trackStat.SecondsListened += seconds;
            trackStat.FormattedTime = ListeningDurationFormatter.Format(trackStat.SecondsListened);
        }
    }

    public void RecordTrackPlay(Song? song = null, DateTime? now = null)
    {
        var dt = now ?? DateTime.Now;
        TotalTracksPlayed++;
        LastListenedAtUtc = DateTime.UtcNow;

        var monthKey = dt.ToString("yyyy-MM");
        if (!Months.TryGetValue(monthKey, out var record))
        {
            record = new MonthlyListeningRecord
            {
                Month = monthKey,
                DisplayName = dt.ToString("MMMM yyyy")
            };
            Months[monthKey] = record;
        }

        record.TrackPlays++;

        if (song is not null)
        {
            var trackKey = string.IsNullOrWhiteSpace(song.Path) ? $"{song.Artist} - {song.Title}" : song.Path;
            if (!record.TopTracks.TryGetValue(trackKey, out var trackStat))
            {
                trackStat = new TrackListeningStat
                {
                    Title = song.Title,
                    Artist = song.Artist,
                    Album = song.Album
                };
                record.TopTracks[trackKey] = trackStat;
            }

            trackStat.PlayCount++;
        }
    }

    public double GetCurrentMonthSeconds(DateTime? now = null)
    {
        var key = (now ?? DateTime.Now).ToString("yyyy-MM");
        return Months.TryGetValue(key, out var record) ? record.SecondsListened : 0;
    }

    /// <summary>
    /// The most-played tracks, ranked by play count (ties broken by time listened). Pass
    /// <paramref name="monthKey"/> (as produced by <c>DateTime.ToString("yyyy-MM")</c>) to scope
    /// to one month, or omit it to combine every track across all months.
    /// </summary>
    public IReadOnlyList<TrackListeningStat> GetTopTracks(int count, string? monthKey = null)
    {
        var tracks = monthKey is null
            ? CombineTracksAcrossMonths()
            : Months.TryGetValue(monthKey, out var record) ? record.TopTracks.Values : [];

        return tracks
            .OrderByDescending(t => t.PlayCount)
            .ThenByDescending(t => t.SecondsListened)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// The most-played artists, ranked the same way as <see cref="GetTopTracks"/> but with each
    /// artist's tracks summed together.
    /// </summary>
    public IReadOnlyList<ArtistListeningStat> GetTopArtists(int count, string? monthKey = null)
    {
        var tracks = monthKey is null
            ? CombineTracksAcrossMonths()
            : Months.TryGetValue(monthKey, out var record) ? record.TopTracks.Values : [];

        return tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var seconds = group.Sum(t => t.SecondsListened);
                return new ArtistListeningStat
                {
                    Artist = group.First().Artist,
                    SecondsListened = seconds,
                    FormattedTime = ListeningDurationFormatter.Format(seconds),
                    PlayCount = group.Sum(t => t.PlayCount),
                };
            })
            .OrderByDescending(a => a.PlayCount)
            .ThenByDescending(a => a.SecondsListened)
            .Take(count)
            .ToList();
    }

    /// <summary>
    /// Sums each track's stats across every month, keyed the same way <see cref="AddListeningTime"/>
    /// keys <see cref="MonthlyListeningRecord.TopTracks"/>, so the same track in different months
    /// combines into one all-time entry instead of appearing once per month.
    /// </summary>
    private IEnumerable<TrackListeningStat> CombineTracksAcrossMonths()
    {
        var combined = new Dictionary<string, TrackListeningStat>();
        foreach (var month in Months.Values)
        {
            foreach (var (trackKey, stat) in month.TopTracks)
            {
                if (!combined.TryGetValue(trackKey, out var total))
                {
                    total = new TrackListeningStat { Title = stat.Title, Artist = stat.Artist, Album = stat.Album };
                    combined[trackKey] = total;
                }

                total.SecondsListened += stat.SecondsListened;
                total.PlayCount += stat.PlayCount;
            }
        }

        foreach (var total in combined.Values)
            total.FormattedTime = ListeningDurationFormatter.Format(total.SecondsListened);

        return combined.Values;
    }
}
