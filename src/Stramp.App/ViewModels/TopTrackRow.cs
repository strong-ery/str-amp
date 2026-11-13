namespace Stramp.App.ViewModels;

/// <summary>One ranked row in a "most played" track list.</summary>
public sealed class TopTrackRow(int rank, string title, string artist, int playCount, string formattedTime)
{
    public int Rank { get; } = rank;
    public string Title { get; } = title;
    public string Artist { get; } = artist;
    public int PlayCount { get; } = playCount;
    public string FormattedTime { get; } = formattedTime;

    public string PlayCountLabel => PlayCount == 1 ? "1 play" : $"{PlayCount} plays";
}
