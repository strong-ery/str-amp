using Stramp.Core.Models;

namespace Stramp.Core.Library;

/// <summary>Ranks library search results without changing the source's normal ordering.</summary>
public static class LibrarySearch
{
    public static IEnumerable<Song> Rank(IEnumerable<Song> songs, string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return songs;

        return songs
            .Select((song, index) => new
            {
                Song = song,
                Index = index,
                Score = Score(song, terms),
            })
            .Where(result => result.Score < int.MaxValue)
            .OrderBy(result => result.Score)
            .ThenBy(result => result.Index)
            .Select(result => result.Song);
    }

    private static int Score(Song song, IReadOnlyList<string> terms)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var best = Math.Min(
                FieldScore(song.Title, term, fieldPenalty: 0),
                FieldScore(song.Artist, term, fieldPenalty: 5));
            if (best == int.MaxValue)
                return int.MaxValue;
            total += best;
        }
        return total;
    }

    private static int FieldScore(string value, string term, int fieldPenalty)
    {
        if (string.Equals(value, term, StringComparison.OrdinalIgnoreCase))
            return fieldPenalty;

        var index = value.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return int.MaxValue;

        var lengthPenalty = Math.Min(Math.Max(value.Length - term.Length, 0), 20);
        if (index == 0)
            return 20 + fieldPenalty + lengthPenalty;

        var startsWord = !char.IsLetterOrDigit(value[index - 1]);
        return startsWord
            ? 50 + fieldPenalty + index + lengthPenalty
            : 100 + fieldPenalty + index + lengthPenalty;
    }
}
