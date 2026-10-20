using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Community;
using TimePlayer.Core.Models;
using Xunit;

namespace TimePlayer.Tests.Models;

public class ChapterAndTranscriptTests
{
    private const string Description = """
        Today we build a birdhouse.

        Chapters:
        0:00 Intro
        1:15 - Tools you need
        (4:02) Cutting the wood
        Painting 12:30
        1:02:03 Outro
        Links: https://example.com/2:30
        """;

    [Fact]
    public void Chapters_AreReadFromTheDescription_InEveryCommonForm()
    {
        var chapters = Chapters.FromDescription(Description);
        Assert.Equal(new[] { "Intro", "Tools you need", "Cutting the wood", "Painting", "Outro" }, chapters.Select(c => c.Title));
        Assert.Equal(new TimeSpan(1, 2, 3), chapters[^1].Start);
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, chapters.Select(c => c.Number));
    }

    [Theory]
    [InlineData("1:00 Starts late\n2:00 B\n3:00 C")]          // must start at 0:00
    [InlineData("0:00 A\n1:00 B")]                            // at least three
    [InlineData("Some text\nno times here")]
    public void NotChapters_AreIgnored(string description) =>
        Assert.Empty(Chapters.FromDescription(description));

    [Fact]
    public void ASecondTimestampList_DoesNotMixIn()
    {
        var chapters = Chapters.FromDescription("0:00 A\n1:00 B\n2:00 C\n\nSongs:\n0:00 Song one\n0:40 Song two");
        Assert.Equal(3, chapters.Count);
    }

    [Fact]
    public void ChaptersPastTheEnd_AreDropped() =>
        Assert.Equal(3, Chapters.FromDescription(Description, TimeSpan.FromMinutes(10)).Count);

    [Fact]
    public void NextAndPrevious_FollowWhereYouAre()
    {
        var chapters = Chapters.FromDescription(Description);
        Assert.Equal("Tools you need", Chapters.Next(chapters, TimeSpan.FromSeconds(10))!.Title);
        Assert.Equal("Tools you need", Chapters.Previous(chapters, TimeSpan.FromMinutes(2))!.Title);    // start of this one
        Assert.Equal("Intro", Chapters.Previous(chapters, TimeSpan.FromSeconds(76))!.Title);           // just began: the one before
        Assert.Null(Chapters.Next(chapters, TimeSpan.FromHours(2)));
        Assert.Equal("Cutting the wood", Chapters.At(chapters, TimeSpan.FromMinutes(5))!.Title);
    }

    [Fact]
    public void Braille_MarksChapterStarts_UnderTheLowerG()
    {
        var track = new BrailleProgressTrack(cellCount: 11);
        var line = track.GenerateDisplayLine(TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(100), isPlaying: true, isPaused: false,
            chapterStarts: new[] { TimeSpan.Zero, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(50) });
        Assert.Equal(BrailleProgressTrack.ChapterMark, line[2]);                          // 20%
        Assert.Equal((char)(BrailleProgressTrack.LowerG | BrailleProgressTrack.ChapterMark), line[5]); // position and chapter together
        Assert.Equal(BrailleProgressTrack.BrailleBlank, line[0]);                         // the first chapter isn't marked
    }

    [Fact]
    public void Transcript_ReadsWebVtt()
    {
        var lines = TranscriptService.Parse("""
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            Hello &amp; welcome

            00:01:02.500 --> 00:01:04.000
            <i>Second</i> line
            continues
            """);
        Assert.Equal(new[] { "Hello & welcome", "Second line continues" }, lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(62.5), lines[1].Start);
    }

    [Fact]
    public void Transcript_ReadsRollingAutomaticCaptionsOnce()
    {
        var lines = TranscriptService.Parse("""
            WEBVTT

            00:00:00.000 --> 00:00:02.000
            so today<00:00:00.500><c> we</c>

            00:00:02.000 --> 00:00:02.010
            so today we

            00:00:02.010 --> 00:00:04.000
            so today we
            look at<00:00:02.500><c> birds</c>
            """);
        Assert.Equal(new[] { "so today we", "look at birds" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void Transcript_ReadsJson3AndTimedText()
    {
        var json = TranscriptService.Parse("""{"events":[{"tStartMs":1500,"segs":[{"utf8":"One "},{"utf8":"two"}]},{"tStartMs":4000}]}""");
        Assert.Equal("One two", Assert.Single(json).Text);
        Assert.Equal(TimeSpan.FromSeconds(1.5), json[0].Start);

        var srv3 = TranscriptService.Parse("""<timedtext format="3"><body><p t="2000" d="1000">Hi <s>there</s></p></body></timedtext>""");
        Assert.Equal("Hi there", Assert.Single(srv3).Text);

        var old = TranscriptService.Parse("""<transcript><text start="3.5" dur="2">It&amp;#39;s here</text></transcript>""");
        Assert.Equal("It's here", Assert.Single(old).Text);
    }
}
