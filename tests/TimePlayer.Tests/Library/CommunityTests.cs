using TimePlayer.Core.Community;
using TimePlayer.Core.Configuration;
using Xunit;

namespace TimePlayer.Tests.Library;

public class SponsorBlockTests
{
    private const string HashAnswer = """
    [
      { "videoID": "someOtherId", "segments": [ { "category": "sponsor", "actionType": "skip", "segment": [1, 2], "UUID": "x" } ] },
      { "videoID": "aqz-KE-bpKQ", "segments": [
          { "category": "outro", "actionType": "skip", "segment": [494.6, 618.7], "UUID": "c" },
          { "category": "sponsor", "actionType": "skip", "segment": [30.5, 60.0], "UUID": "a" },
          { "category": "poi_highlight", "actionType": "poi", "segment": [120.0, 120.0], "UUID": "h" },
          { "category": "exclusive_access", "actionType": "full", "segment": [0, 0], "UUID": "f" },
          { "category": "chapter", "actionType": "chapter", "segment": [0, 10], "UUID": "ignored" }
      ] }
    ]
    """;

    [Fact]
    public void Parse_KeepsOnlyThisVideosKnownSegmentsInOrder()
    {
        var segments = SponsorBlockService.Parse(HashAnswer, "aqz-KE-bpKQ");

        Assert.Equal(new[] { "f", "a", "h", "c" }, segments.Select(s => s.Uuid));
        Assert.Equal(SegmentKind.WholeVideo, segments[0].Kind);
        Assert.Equal(SegmentCategory.Sponsor, segments[1].Category);
        Assert.Equal(SegmentKind.Highlight, segments[2].Kind);
        Assert.Equal(TimeSpan.FromSeconds(29.5), segments[1].Length);
    }

    [Fact]
    public void Tracker_SkipsOnceAnnouncesOnceAndRespectsOff()
    {
        var tracker = new SegmentTracker();
        tracker.Reset("v", SponsorBlockService.Parse(HashAnswer, "aqz-KE-bpKQ"));
        var settings = new SponsorBlockConfig { Sponsor = SegmentAction.Skip, Outro = SegmentAction.Announce };

        var first = tracker.Update(1, settings);
        Assert.Contains(first, e => e is SegmentEvent.WholeVideo);
        Assert.Contains(first, e => e is SegmentEvent.HighlightAvailable);

        Assert.Empty(tracker.Update(20, settings));
        Assert.IsType<SegmentEvent.Skip>(Assert.Single(tracker.Update(31, settings)));
        // Seeking back into a skipped segment plays it.
        Assert.Empty(tracker.Update(40, settings));
        Assert.IsType<SegmentEvent.Announce>(Assert.Single(tracker.Update(500, settings)));
        Assert.Empty(tracker.Update(510, settings));

        // S still finds the segment playing now.
        Assert.Equal("c", tracker.SegmentAt(510, settings)?.Uuid);
        Assert.Null(tracker.SegmentAt(510, settings with { Outro = SegmentAction.Off }));

        tracker.Rewind();
        Assert.Contains(tracker.Update(31, settings), e => e is SegmentEvent.Skip);
    }

    [Fact]
    public void Tracker_DoesNothingWhenTurnedOff()
    {
        var tracker = new SegmentTracker();
        tracker.Reset("v", SponsorBlockService.Parse(HashAnswer, "aqz-KE-bpKQ"));
        Assert.Empty(tracker.Update(31, new SponsorBlockConfig { Enabled = false }));
    }

    [Fact]
    public void Tracker_LeavesASegmentAloneInItsLastHalfSecond()
    {
        var tracker = new SegmentTracker();
        tracker.Reset("v", SponsorBlockService.Parse(HashAnswer, "aqz-KE-bpKQ"));
        var settings = new SponsorBlockConfig { AnnounceHighlight = false, AnnounceWholeVideoLabels = false };
        Assert.Empty(tracker.Update(59.8, settings));
    }
}

public class DislikeTests
{
    [Fact]
    public void Parse_ReadsLikesAndDislikes()
    {
        var votes = DislikeService.Parse("""{"id":"kxOuG8jMIgI","likes":31885,"dislikes":579721,"rating":1.2,"viewCount":3762293}""");
        Assert.Equal(new VideoVotes(31885, 579721), votes);
        Assert.Equal("31,885 likes, 579,721 dislikes", votes!.Spoken.Replace(' ', ','));
    }

    [Fact]
    public void Parse_ReturnsNullForUnexpectedAnswers()
    {
        Assert.Null(DislikeService.Parse("""{"error":"not found"}"""));
        Assert.Null(DislikeService.Parse("[]"));
    }
}
