using Stramp.Core.Lyrics;

namespace Stramp.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parse_ReadsTimestampsAndText()
    {
        var document = LrcParser.Parse("""
            [ti:Test Song]
            [ar:Test Artist]
            [00:12.50]First line
            [01:05.25]Second line
            """);

        Assert.True(document.IsSynced);
        Assert.Equal("Test Song", document.Title);
        Assert.Equal("Test Artist", document.Artist);
        Assert.Equal(
            [(12.5, "First line"), (65.25, "Second line")],
            document.Lines.Select(line => (line.TimeSeconds, line.Text)));
    }

    [Fact]
    public void Parse_ExpandsRepeatedTimestampsOnOneLine()
    {
        var document = LrcParser.Parse("[00:10.00][00:40.00]Chorus");

        Assert.Equal([10d, 40d], document.Lines.Select(line => line.TimeSeconds));
        Assert.All(document.Lines, line => Assert.Equal("Chorus", line.Text));
    }

    [Fact]
    public void Parse_SortsByTime_RegardlessOfFileOrder()
    {
        var document = LrcParser.Parse("""
            [00:30.00]Later
            [00:10.00]Earlier
            """);

        Assert.Equal(["Earlier", "Later"], document.Lines.Select(line => line.Text));
    }

    /// <summary>A positive offset means the words land earlier than their timestamps say.</summary>
    [Fact]
    public void Parse_AppliesOffset_EvenWhenDeclaredAfterTheLines()
    {
        var document = LrcParser.Parse("""
            [00:10.00]Line
            [offset:+500]
            """);

        Assert.Equal(9.5, document.Lines.Single().TimeSeconds);
    }

    [Fact]
    public void Parse_ClampsOffsetShiftAtZero()
    {
        var document = LrcParser.Parse("""
            [offset:2000]
            [00:00.50]Line
            """);

        Assert.Equal(0, document.Lines.Single().TimeSeconds);
    }

    [Fact]
    public void Parse_AcceptsTheColonFractionForm()
    {
        var document = LrcParser.Parse("[01:23:45]Line");

        Assert.Equal(83.45, document.Lines.Single().TimeSeconds!.Value, precision: 3);
    }

    [Fact]
    public void Parse_StripsEnhancedWordTimings()
    {
        var document = LrcParser.Parse("[00:01.00]<00:01.00>Every <00:01.50>word <00:02.00>timed");

        Assert.Equal("Every word timed", document.Lines.Single().Text);
    }

    [Fact]
    public void Parse_KeepsTimedBlankLinesAsGaps()
    {
        var document = LrcParser.Parse("""
            [00:01.00]Verse
            [00:05.00]
            [00:09.00]Chorus
            """);

        Assert.Equal(["Verse", "", "Chorus"], document.Lines.Select(line => line.Text));
    }

    [Fact]
    public void Parse_TreatsAnUntaggedFileAsUnsynced()
    {
        var document = LrcParser.Parse("""

            First line
            Second line


            Third line

            """);

        Assert.False(document.IsSynced);
        Assert.All(document.Lines, line => Assert.Null(line.TimeSeconds));
        Assert.Equal(
            ["First line", "Second line", "", "Third line"],
            document.Lines.Select(line => line.Text));
    }

    /// <summary>An unsynced file can still carry header tags; those are metadata, not lyrics.</summary>
    [Fact]
    public void Parse_DoesNotTreatHeaderTagsAsUnsyncedLines()
    {
        var document = LrcParser.Parse("""
            [ti:Test Song]
            [by:Someone]
            Only real line
            """);

        Assert.False(document.IsSynced);
        Assert.Equal("Test Song", document.Title);
        Assert.Equal("Only real line", document.Lines.Single().Text);
    }

    [Fact]
    public void Parse_IgnoresUnsyncedLines_WhenTheFileIsSynced()
    {
        var document = LrcParser.Parse("""
            stray text
            [00:01.00]Real line
            """);

        Assert.True(document.IsSynced);
        Assert.Equal("Real line", document.Lines.Single().Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void Parse_ReturnsEmpty_ForNothingUsable(string? content)
    {
        Assert.False(LrcParser.Parse(content).HasLines);
    }

    [Fact]
    public void Load_FindsTheSiblingLrc()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track = Path.Combine(dir.FullName, "Artist - Title.mp3");
            File.WriteAllBytes(track, []);
            File.WriteAllText(Path.Combine(dir.FullName, "Artist - Title.lrc"), "[00:02.00]Hello");

            var document = LyricsLoader.Load(track);

            Assert.True(document.IsSynced);
            Assert.Equal("Hello", document.Lines.Single().Text);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenNoLrcSitsBesideTheTrack()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var track = Path.Combine(dir.FullName, "Artist - Title.mp3");
            File.WriteAllBytes(track, []);
            File.WriteAllText(Path.Combine(dir.FullName, "Other Song.lrc"), "[00:02.00]Hello");

            Assert.False(LyricsLoader.Load(track).HasLines);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
