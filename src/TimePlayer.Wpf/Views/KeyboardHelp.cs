using TimePlayer.Wpf.ViewModels;

using static TimePlayer.Core.Localization.Loc;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// F1 help, written for the area that has focus. Each text starts with what matters most there,
/// so a screen reader user can stop listening after the first line or two.
/// </summary>
public static class KeyboardHelp
{
    public static string SearchBox =>
        T("Type words or paste a YouTube link, then press Enter. Results open as their own page; Alt+Left goes back.\n") +
        T("Escape clears the box, and Escape again returns to the list. Tab moves to Search for (Videos or Playlists) and Settings.\n") +
        T("From anywhere, slash, Control+F or Control+E come back here. Control+Shift+V plays a YouTube link from the clipboard. Control+Shift+B reports a problem to TimePlayer's developer, and Control+Shift+F suggests a feature.\n") +
        T("A YouTube playlist link opens the playlist.");

    public static string Sections =>
        T("Left and Right arrows switch sections. Tab moves through this section's buttons and list, and stays in the section.\n") +
        T("Video: Control+1 Featured, 2 For You, 3 What's New, 4 Channels, 5 Playlists, 6 Watch Later, 7 History, 8 Queue.\n") +
        T("Music: Control+1 Charts, 2 Recently played, 3 Playlists, 4 Queue. Control+M switches between Video and Music.\n") +
        T("Control+9 is YouTube (your account's home, subscriptions, history and likes) when you use your account: Settings, YouTube account.\n") +
        T("Control+Shift+S starts Shorts mode: Up and Down move between Shorts, and each one repeats.\n") +
        T("Control+Tab goes to the next section. Slash searches. F6 moves between the search box, the sections, the list and Now playing.\n") +
        NowPlayingKeys;

    /// <summary>Keys that work anywhere while something plays.</summary>
    public static string NowPlayingKeys =>
        T("While something plays: Control+Space plays or pauses, Control+Shift+N and Control+Shift+P go to the next and previous, and the keyboard's media keys work. ") +
        T("In Music, Alt+P opens the player.");

    public static string NowPlaying =>
        T("Now playing: music keeps playing while you browse. Tab moves through Play, Previous, Next, Open player and Stop.\n") + NowPlayingKeys;

    private static string VideoListKeys =>
        T("Enter plays. Control+Q adds to the queue, Control+L saves to Watch Later, ") +
        T("Control+K likes, Control+P adds to a playlist, ") +
        T("Control+S subscribes to the channel, Control+G opens the channel, Control+C copies a share link, ") +
        T("Control+D downloads the audio or video. ") +
        T("Control+R starts a radio of songs like it. ") +
        T("Control+Shift+S plays the list's Shorts in Shorts mode. ") +
        T("The Applications key lists every action.");

    public static string ForList(BrowseSection section) => section switch
    {
        BrowseSection.Search =>
            T("Search results. ") + VideoListKeys + T("\nMore results load as you reach the end of the list. Slash searches again; Alt+Left, Backspace or Escape goes back."),
        BrowseSection.Featured =>
            T("Featured videos. ") + VideoListKeys + T("\nShift+Tab reaches the Category box: Trending, Music (the top 100 music videos), Gaming, Movies (trailers), News, Sports and Live. ") +
            T("Music, Gaming and Movies keep loading as you reach the end. F5 refreshes."),
        BrowseSection.WhatsNew =>
            T("Latest videos from your channels, newest first. Videos you have not seen yet start with New.\n") + VideoListKeys + T("\nF5 checks for new videos."),
        BrowseSection.Channels =>
            T("Your subscribed channels. Enter shows a channel's videos. Delete or Control+S unsubscribes.\n") +
            T("Shift+Tab reaches Import and Export, for NewPipe, LibreTube, FreeTube, Google Takeout and OPML files."),
        BrowseSection.History =>
            T("Videos you watched, newest first. Partly watched ones say where they will resume.\n") + VideoListKeys + T("\nDelete removes one video from history."),
        BrowseSection.WatchLater =>
            T("Videos you saved. ") + VideoListKeys + T("\nDelete removes a video from Watch Later."),
        BrowseSection.Channel =>
            T("A channel's videos, newest first. Older videos load as you reach the end.\n") + VideoListKeys +
            T("\nShift+Tab reaches the Tab box: Videos, Shorts, Live and Playlists. Control+D on a video offers to download the whole list.") +
            T("\nAlt+Left, Backspace or Escape goes back."),
        BrowseSection.Queue => Queue,
        BrowseSection.MusicCharts =>
            T("Charts: the top 100 music videos for your region (Settings, Library). Enter plays a song; music keeps playing while you browse. ") + VideoListKeys + "\n" + NowPlayingKeys,
        BrowseSection.MusicRecent =>
            T("Songs you played in Music, newest first. ") + VideoListKeys + T("\nDelete removes a song from history.\n") + NowPlayingKeys,
        BrowseSection.ForYou =>
            T("Videos picked for you from what you watch, like and subscribe to. Nothing leaves this computer.\n") + VideoListKeys +
            T("\nDelete means not interested; Shift+Delete stops recommending that channel. F5 finds new picks."),
        BrowseSection.Playlists =>
            T("Liked videos and your playlists (Control+5). Enter opens one. Shift+Tab reaches Play all, Shuffle, New playlist, Rename, Delete and Import from Google Takeout.\n") +
            T("Control+C copies a link to share the playlist. Delete deletes the selected playlist after asking."),
        BrowseSection.Playlist =>
            T("The videos in this playlist. ") + VideoListKeys + "\n" +
            T("In your own playlists, Delete removes a video and Alt+Up or Alt+Down moves it. ") +
            T("Shift+Tab reaches Play all, Shuffle, Copy link, and Save to Playlists for YouTube playlists. F5 updates a saved YouTube playlist. ") +
            T("Alt+Left, Backspace or Escape goes back."),
        _ => VideoListKeys
    };

    public static string YouTubeSubscriptionsSteps =>
        T("""
        Your YouTube subscriptions, playlists, liked videos and watch history can be exported from Google with Google Takeout. Steps:

        1. Choose Yes below to open Google Takeout in your browser, set to YouTube only. Sign in to Google if asked.
        2. YouTube and YouTube Music is already checked. Activate the button "All YouTube data included", choose Deselect all, check "subscriptions", "playlists" and "history", then OK.
        3. For watch history, activate "Multiple formats" and set history to JSON instead of HTML. Otherwise history is skipped.
        4. Activate "Next step". Leave the delivery method as "Send download link via email" and the file type as .zip, then activate "Create export".
        5. Google emails you a download link, usually within a few minutes. Download the zip file.
        6. Back in TimePlayer: Playlists (Control+5), Import from Google Takeout, and choose that zip. There is no need to unzip it. Channels, Import subscriptions does the same.

        Open Google Takeout now?
        """);

    public static string Queue =>
        T("Videos play in this order. Enter plays one now, Delete removes it. The Applications key also offers Clear queue.");

    public static string Player(double smallSeek, double largeSeek) =>
        T("K plays or pauses (Space too, unless a button has focus). Left and Right move {0}, Control+Left and Right {1}.\n", Seconds(smallSeek), Seconds(largeSeek)) +
        T("0 to 9 jump to 0 to 90 percent. Home starts over. Up and Down change the volume, M mutes.\n") +
        T("T says the time, I says what is playing, U says what plays next.\n") +
        T("Shift+N next video, Shift+P previous. Shift+Period faster, Shift+Comma slower.\n") +
        T("On the Position slider, Left and Right move {0} and Page Up and Page Down {1}.\n", Seconds(smallSeek), Seconds(largeSeek)) +
        T("Alt+U Up next, Alt+C comments, Alt+D description, Alt+Q queue. F6 moves between the controls, those panels and the top bar.\n") +
        T("A turns autoplay on or off. C turns reading captions aloud on or off.\n") +
        T("S skips the SponsorBlock segment playing now, H jumps to the video's highlight.\n") +
        T("E and Shift+E switch the sound preset, Control+M turns music mode (no video, music sound) on or off.\n") +
        T("D turns audio description on or off. Shift+Left and Shift+Right go to the previous and next chapter.\n") +
        T("Alt+H chapters, Alt+T transcript, Alt+L lyrics. L reads lyrics aloud as they're sung, and R starts a radio of songs like this one.\n") +
        T("Control+K likes, Control+P adds to a playlist, Control+S subscribes, Control+L Watch Later, Control+G goes to the channel.\n") +
        T("Control+C copies a share link that starts where you are; Control+Shift+C copies one from the beginning. Control+D downloads audio or video.\n") +
        T("Alt+A or the Applications key opens Actions: like, subscribe, Watch Later, playlist, share, download, captions, speed, sound and more.\n") +
        T("Slash leaves the player and goes to search. Escape leaves the player: a video stops, music keeps playing.\n") +
        T("Control+M switches between Video and Music.");

    private static string Seconds(double value) => value % 1 == 0
        ? Plural((long)value, "{0} second", "{0} seconds")
        : T("{0:0.#} seconds", value);

    public static string UpNext =>
        T("Related videos. Enter plays one now, Control+Q queues it, Control+K likes it, Control+P adds it to a playlist. With autoplay on, the first unwatched one plays when the queue ends.\n") +
        T("Escape returns to the player controls.");

    public static string Comments =>
        T("Comments are a tree. Each is read as: who, what they said, then when, likes and replies. More comments load as you reach the end.\n") +
        T("A comment with replies is collapsed: Right arrow opens its replies, which are one level deeper and start with \"Reply from\". ") +
        T("Right again moves into them; Left goes back up to the comment, and Left on the comment closes it. Enter also opens or closes. ") +
        T("\"More replies\" at the end of a reply list loads the rest with Enter.\n") +
        T("Control+C copies a comment. Escape returns to the player controls.");

    public static string Chapters =>
        T("Chapters come from the timestamps in the video's description. Enter goes to a chapter. While playing, Shift+Left and Shift+Right move between chapters, and T says which chapter you are in. ") +
        T("On a braille display, dots 7 and 8 mark where chapters start, and a routing key on one goes to that chapter.");

    public static string Transcript =>
        T("The video's captions as a document, each line followed by its time. Up and Down read it, the first letter of a word jumps to a line starting with it, and Enter plays from the selected line. ") +
        T("It uses the captions chosen in the player, or else the best for your language.");

    public static string Lyrics =>
        T("The song's lyrics from LRCLIB, a free lyrics library. Up and Down read them, and Enter plays from a timed line. ") +
        T("L in the player reads timed lyrics aloud as they're sung, like captions; Settings, Speech and braille, can keep it on.");

    public static string Description =>
        T("The video description. Use the arrow keys to read it line by line. Escape returns to the player controls.");
}
