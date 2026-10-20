using TimePlayer.Core.Localization;
using TimePlayer.Tests.Accessibility;
using TimePlayer.Wpf.ViewModels;
using TimePlayer.Wpf.Views;
using Xunit;

namespace TimePlayer.Tests.Accessibility;

[Collection("Language")]
public class QuickHelpTests : IDisposable
{
    public void Dispose() => Loc.Use("en");

    public static IEnumerable<object[]> Places() => new[]
    {
        new object[] { QuickHelp.Search }, new object[] { QuickHelp.Sections(false) }, new object[] { QuickHelp.Sections(true) },
        new object[] { QuickHelp.NowPlaying }, new object[] { QuickHelp.Player }, new object[] { QuickHelp.UpNext },
        new object[] { QuickHelp.Comments }, new object[] { QuickHelp.Description }, new object[] { QuickHelp.PlayerQueue },
        new object[] { QuickHelp.Chapters }, new object[] { QuickHelp.Transcript }, new object[] { QuickHelp.Lyrics },
        new object[] { QuickHelp.Shorts },
        new object[] { QuickHelp.Settings("Sound") },
        new object[] { QuickHelp.List(BrowseSection.Featured, 5, "") }, new object[] { QuickHelp.List(BrowseSection.Channels, 2, "") },
        new object[] { QuickHelp.List(BrowseSection.WatchLater, 0, "Watch Later is empty.\nPress Control+L on any video.") },
    };

    /// <summary>F1 is short: a sentence or two, then the way to more.</summary>
    [Theory]
    [MemberData(nameof(Places))]
    public void F1_IsShort_AndOffersMore(string help)
    {
        Assert.EndsWith("Control+F1 for more help.", help);
        Assert.InRange(help.Split(". ").Length, 2, 5);
        Assert.True(help.Length < 260, help);
    }

    [Fact]
    public void Lists_SayHowToMoveAndAct()
    {
        var help = QuickHelp.List(BrowseSection.Featured, 5, "");
        Assert.Contains("first letter", help);
        Assert.Contains("Applications key", help);
    }

    [Fact]
    public void AnEmptyList_SaysWhatWouldFillIt() =>
        Assert.StartsWith("Watch Later is empty. Press Control+L on any video.", QuickHelp.List(BrowseSection.WatchLater, 0, "Watch Later is empty.\nPress Control+L on any video."));

    [Fact]
    public void QuickHelp_IsTranslated()
    {
        Loc.Use("es");
        Assert.Equal("Pulsa F1 para la ayuda.", QuickHelp.Startup);
        Assert.EndsWith("Control+F1 para más ayuda.", QuickHelp.Search);
    }

    [Theory]
    [InlineData("You have no subscriptions yet.\nPress Control+S on any video.", "You have no subscriptions yet.")]
    [InlineData("No results. Press slash to search again.", "No results.")]
    [InlineData("No playlists yet.", "No playlists yet.")]
    public void EmptySections_SpeakOnlyTheFirstSentence(string text, string spoken) =>
        Assert.Equal(spoken, MainViewModel.FirstSentence(text));
}
