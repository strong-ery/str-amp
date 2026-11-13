namespace Stramp.App.ViewModels;

/// <summary>One ranked row in a "most played" artist list.</summary>
public sealed class TopArtistRow(int rank, string artist, int playCount, string formattedTime)
{
    public int Rank { get; } = rank;
    public string Artist { get; } = artist;
    public int PlayCount { get; } = playCount;
    public string FormattedTime { get; } = formattedTime;

    public string PlayCountLabel => PlayCount == 1 ? "1 play" : $"{PlayCount} plays";
}
