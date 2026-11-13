using CommunityToolkit.Mvvm.ComponentModel;
using Stramp.Core.Playback;

namespace Stramp.App.ViewModels;

/// <summary>Backs the "View Stats" panel: most-played songs and artists, all-time and this month.</summary>
public partial class ListeningStatsViewModel : ViewModelBase
{
    private const int RowCount = 10;

    [ObservableProperty]
    public partial IReadOnlyList<TopTrackRow> TopSongsAllTime { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TopTrackRow> TopSongsThisMonth { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TopArtistRow> TopArtistsAllTime { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TopArtistRow> TopArtistsThisMonth { get; set; } = [];

    [ObservableProperty]
    public partial string TopSongsThisMonthLabel { get; set; } = "TOP SONGS · THIS MONTH";

    [ObservableProperty]
    public partial string TopArtistsThisMonthLabel { get; set; } = "TOP ARTISTS · THIS MONTH";

    /// <summary>Recomputes every list from the current state of the listening history database.</summary>
    public void Refresh(ListeningHistoryDatabase history)
    {
        var now = DateTime.Now;
        var monthKey = now.ToString("yyyy-MM");
        var monthName = now.ToString("MMMM yyyy").ToUpperInvariant();
        TopSongsThisMonthLabel = $"TOP SONGS · {monthName}";
        TopArtistsThisMonthLabel = $"TOP ARTISTS · {monthName}";

        TopSongsAllTime = ToTrackRows(history.GetTopTracks(RowCount));
        TopSongsThisMonth = ToTrackRows(history.GetTopTracks(RowCount, monthKey));
        TopArtistsAllTime = ToArtistRows(history.GetTopArtists(RowCount));
        TopArtistsThisMonth = ToArtistRows(history.GetTopArtists(RowCount, monthKey));
    }

    private static IReadOnlyList<TopTrackRow> ToTrackRows(IReadOnlyList<TrackListeningStat> tracks)
    {
        var rows = new TopTrackRow[tracks.Count];
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            rows[i] = new TopTrackRow(i + 1, track.Title, track.Artist, track.PlayCount, track.FormattedTime);
        }
        return rows;
    }

    private static IReadOnlyList<TopArtistRow> ToArtistRows(IReadOnlyList<ArtistListeningStat> artists)
    {
        var rows = new TopArtistRow[artists.Count];
        for (var i = 0; i < artists.Count; i++)
        {
            var artist = artists[i];
            rows[i] = new TopArtistRow(i + 1, artist.Artist, artist.PlayCount, artist.FormattedTime);
        }
        return rows;
    }
}
