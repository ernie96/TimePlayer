using TimePlayer.Wpf.ViewModels;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// F1: one or two sentences, spoken, about where the focus is: what this is and the keys that matter
/// most. The interface itself stays quiet; the full help (<see cref="KeyboardHelp"/>) is Control+F1.
/// Each ends by offering that, as KeySoft's help did on the BrailleNote.
/// </summary>
public static class QuickHelp
{
    private static string WithMore(string text) => text + " " + T("Control+F1 for more help.");

    public static string Startup => T("Press F1 for help.");

    public static string Search => WithMore(
        T("Search box. Type words or paste a YouTube link, then press Enter. Escape clears the box, and Escape again goes back to the list."));

    public static string Sections(bool music) => WithMore(music
        ? T("Sections. Left and Right arrows switch sections, or Control+1 to 4 go straight to one.")
        : T("Sections. Left and Right arrows switch sections, or Control+1 to 8 go straight to one."));

    public static string NowPlaying => WithMore(
        T("Now playing. Tab moves through Play, Previous, Next, Open player and Stop. Control+Space plays or pauses from anywhere."));

    /// <summary>A list of videos, channels or playlists. An empty list says what would fill it.</summary>
    public static string List(BrowseSection section, int count, string emptyText)
    {
        if (count == 0)
            return WithMore(emptyText.Replace('\n', ' '));
        var move = T("Up and Down move through the list; the first letter of a title jumps to it.");
        var act = section switch
        {
            BrowseSection.Channels => T("Enter shows the channel's videos, and Delete unsubscribes."),
            BrowseSection.Playlists => T("Enter opens the playlist. The Applications key lists what you can do with it."),
            BrowseSection.Queue => T("Enter plays the video now, and Delete takes it out of the queue."),
            _ => T("Enter plays. The Applications key lists what you can do with the selected video.")
        };
        return WithMore(move + " " + act);
    }

    public static string Player => WithMore(
        T("Player. K or Space plays or pauses, Left and Right move through the video, and Up and Down change the volume. Alt+A or the Applications key lists every action, and Escape leaves the player."));

    public static string UpNext => WithMore(
        T("Up next. Enter plays the selected video, and the Applications key lists more. Escape returns to the player controls."));

    public static string Comments => WithMore(
        T("Comments. Right arrow opens a comment's replies and Left closes them. Escape returns to the player controls."));

    public static string Description => WithMore(
        T("Description. Read it with the arrow keys. Escape returns to the player controls."));

    public static string PlayerQueue => WithMore(
        T("Queue. Enter plays the video now, and Delete takes it out of the queue. Escape returns to the player controls."));

    public static string Settings(string page) => WithMore(
        T("Settings, {0}. Tab moves between settings, and Control+Tab goes to the next page. Escape closes and saves.", page));

    public static string ReportForm =>
        T("Describe it in the box, then Tab to Send. Escape closes without sending.");
}
