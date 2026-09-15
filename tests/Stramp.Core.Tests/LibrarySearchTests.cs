using Stramp.Core.Library;
using Stramp.Core.Models;

namespace Stramp.Core.Tests;

public class LibrarySearchTests
{
    [Fact]
    public void Rank_PutsExactAndPrefixMatchesBeforeLaterContainsMatches()
    {
        var songs = new[]
        {
            Song("Somewhere Only We Know", "Keane"),
            Song("Know Your Enemy", "Rage Against the Machine"),
            Song("Know", "System of a Down"),
        };

        var results = LibrarySearch.Rank(songs, "know").ToList();

        Assert.Equal(["Know", "Know Your Enemy", "Somewhere Only We Know"],
            results.Select(song => song.Title));
    }

    [Fact]
    public void Rank_AllowsTermsToMatchAcrossArtistAndTitle()
    {
        var songs = new[]
        {
            Song("LOYALTY.", "Kendrick Lamar"),
            Song("Loyal", "Odesza"),
        };

        var result = Assert.Single(LibrarySearch.Rank(songs, "kendrick loyalty"));

        Assert.Equal("LOYALTY.", result.Title);
    }

    [Fact]
    public void Rank_EmptyQueryPreservesOriginalOrder()
    {
        var songs = new[] { Song("B", "Artist"), Song("A", "Artist") };

        Assert.Equal(songs, LibrarySearch.Rank(songs, "  "));
    }

    private static Song Song(string title, string artist) => new()
    {
        Path = title,
        Title = title,
        Artist = artist,
    };
}
