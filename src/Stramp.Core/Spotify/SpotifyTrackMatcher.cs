using System.Text.RegularExpressions;
using Stramp.Core.Models;

namespace Stramp.Core.Spotify;

/// <summary>Finds the local song that each Spotify track refers to.</summary>
/// <remarks>
/// Tries passes from strictest to loosest and takes the first song a pass finds: exact title, artist,
/// album and year; exact title and artist; title without punctuation; title without "feat." credits
/// and version tags such as "remastered" or "live"; one base title containing the other; a long base
/// title alone; and finally the file name, only for songs whose file name says something their title
/// tag does not. Artist checks accept any shared credited artist, comparing case-insensitively. A
/// track no pass matches is reported back rather than guessed.
/// </remarks>
public sealed class SpotifyTrackMatcher
{
    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Regex Punctuation = new(@"[^\w\s]");
    private static readonly Regex[] VersionTags =
    [
        new(@"\s*[\(\[](?:feat|ft|with)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase),
        new(@"\s*-\s*(?:feat|ft|with)\.?\s+.*$", RegexOptions.IgnoreCase),
        new(@"\s*[\(\[][^\)\]]*(?:remix|mix|edit|version|remaster|recorded|deluxe|live|acoustic)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase),
        new(@"\s*-\s*.*?(?:remix|mix|edit|version|remaster|recorded|deluxe|live|acoustic).*$", RegexOptions.IgnoreCase),
    ];

    private readonly List<LocalTrack> _library;

    public SpotifyTrackMatcher(IReadOnlyList<Song> library) =>
        _library = library.Select(LocalTrack.From).ToList();

    public Song? Match(SpotifyTrack track)
    {
        var title = Clean(track.Name);
        var baseTitle = BaseTitle(track.Name);
        var bareTitle = StripPunctuation(track.Name);
        var album = Clean(track.Album);
        var year = Year(track.ReleaseDate);
        var artists = track.Artists.Append(string.Join(", ", track.Artists)).Select(Clean)
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool SharesArtist(LocalTrack t) => artists.Count == 0 || artists.Overlaps(t.Artists);

        Func<LocalTrack, bool>[] passes =
        [
            t => t.Title == title && artists.Overlaps(t.Artists) && t.Album == album && t.Year == year,
            t => t.Title == title && SharesArtist(t),
            t => t.BareTitle == bareTitle && SharesArtist(t),
            t => baseTitle.Length > 0 && t.BaseTitle == baseTitle && SharesArtist(t),
            t => baseTitle.Length >= 5 && t.BaseTitle.Length >= 5 && SharesArtist(t) &&
                 (t.BaseTitle.Contains(baseTitle) || baseTitle.Contains(t.BaseTitle)),
            t => baseTitle.Length >= 8 && t.BaseTitle == baseTitle,
            t => t.BareFileName != t.BareTitle &&
                 (t.BareFileName == bareTitle || baseTitle.Length > 0 && t.BaseFileName == baseTitle),
        ];

        foreach (var pass in passes)
        {
            if (_library.FirstOrDefault(pass) is { } found)
                return found.Song;
        }

        return null;
    }

    private static string Clean(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "" : Whitespace.Replace(text, " ").Trim().ToLowerInvariant();

    private static string StripPunctuation(string? text) =>
        Whitespace.Replace(Punctuation.Replace(Clean(text), " "), " ").Trim();

    private static string BaseTitle(string? title)
    {
        var text = Clean(title);
        foreach (var tag in VersionTags)
            text = tag.Replace(text, "");
        return StripPunctuation(text);
    }

    // The first four-digit run, which is the year in Spotify dates and in album names like "Hits (1999)".
    private static string Year(string? text)
    {
        var match = Regex.Match(Clean(text), @"\d{4}");
        return match.Success ? match.Value : Clean(text);
    }

    private sealed record LocalTrack(
        Song Song, string Title, string BaseTitle, string BareTitle, string BareFileName, string BaseFileName,
        string Album, string Year, HashSet<string> Artists)
    {
        public static LocalTrack From(Song song)
        {
            var fileName = Path.GetFileNameWithoutExtension(song.Path);
            var artists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var credit in new[] { song.Artist, song.AlbumArtist }.Select(Clean).Where(c => c.Length > 0))
            {
                artists.Add(credit);
                foreach (var part in credit.Split([',', ';', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    artists.Add(part);
            }

            return new LocalTrack(
                song, Clean(song.Title), SpotifyTrackMatcher.BaseTitle(song.Title), StripPunctuation(song.Title),
                StripPunctuation(fileName), SpotifyTrackMatcher.BaseTitle(fileName),
                Clean(song.Album), SpotifyTrackMatcher.Year(song.Album), artists);
        }
    }
}
