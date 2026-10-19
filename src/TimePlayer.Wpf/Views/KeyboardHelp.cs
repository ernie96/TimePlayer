using TimePlayer.Wpf.ViewModels;

namespace TimePlayer.Wpf.Views;

/// <summary>
/// F1 help, written for the area that has focus. Each text starts with what matters most there,
/// so a screen reader user can stop listening after the first line or two.
/// </summary>
public static class KeyboardHelp
{
    public const string SearchBox =
        "Type words or paste a YouTube link, then press Enter.\n" +
        "Escape clears the box. Tab moves to the buttons: Search, Paste link and play (plays a YouTube link from the clipboard) and Settings. F6 moves to the sections.\n" +
        "Control+1 to Control+9 open Search, Featured, What's New, Channels, History, Watch Later, Queue, For You and Playlists.\n" +
        "To find playlists instead of videos, Shift+Tab to the list's Find box (Alt+F) and choose Playlists. A YouTube playlist link opens the playlist.";

    public const string Sections =
        "Left and Right arrows switch sections. Tab moves to the list.\n" +
        "From anywhere: Control+1 to Control+9 jump to a section, Control+Tab goes to the next one.";

    private const string VideoListKeys =
        "Enter plays. Control+Q adds to the queue, Control+L saves to Watch Later, " +
        "Control+K likes, Control+P adds to a playlist, " +
        "Control+S subscribes to the channel, Control+G opens the channel, Control+C copies a share link, " +
        "Control+D downloads the audio or video. " +
        "The Applications key lists every action.";

    public static string ForList(BrowseSection section) => section switch
    {
        BrowseSection.Search =>
            "Search results. " + VideoListKeys + "\nMore results load as you reach the end of the list. Control+E goes back to the search box.",
        BrowseSection.Featured =>
            "Featured videos. " + VideoListKeys + "\nShift+Tab reaches the Category box: Trending, Music (the top 100 music videos), Gaming, Movies (trailers), News, Sports and Live. " +
            "Music, Gaming and Movies keep loading as you reach the end. F5 refreshes.",
        BrowseSection.WhatsNew =>
            "Latest videos from your channels, newest first. Videos you have not seen yet start with New.\n" + VideoListKeys + "\nF5 checks for new videos.",
        BrowseSection.Channels =>
            "Your subscribed channels. Enter shows a channel's videos. Delete or Control+S unsubscribes.\n" +
            "Shift+Tab reaches Import and Export, for NewPipe, LibreTube, FreeTube, Google Takeout and OPML files.",
        BrowseSection.History =>
            "Videos you watched, newest first. Partly watched ones say where they will resume.\n" + VideoListKeys + "\nDelete removes one video from history.",
        BrowseSection.WatchLater =>
            "Videos you saved. " + VideoListKeys + "\nDelete removes a video from Watch Later.",
        BrowseSection.Channel =>
            "A channel's videos, newest first. Older videos load as you reach the end.\n" + VideoListKeys + "\nAlt+Left, Backspace or Escape goes back.",
        BrowseSection.Queue => Queue,
        BrowseSection.ForYou =>
            "Videos picked for you from what you watch, like and subscribe to. Nothing leaves this computer.\n" + VideoListKeys +
            "\nDelete means not interested; Shift+Delete stops recommending that channel. F5 finds new picks.",
        BrowseSection.Playlists =>
            "Liked videos and your playlists. Enter opens one. Shift+Tab reaches Play all, Shuffle, New playlist, Rename, Delete and Import from Google Takeout.\n" +
            "Control+C copies a link to share the playlist. Delete deletes the selected playlist after asking.",
        BrowseSection.Playlist =>
            "The videos in this playlist. " + VideoListKeys + "\n" +
            "In your own playlists, Delete removes a video and Alt+Up or Alt+Down moves it. " +
            "Shift+Tab reaches Play all, Shuffle, Copy link, and Save to Playlists for YouTube playlists. F5 updates a saved YouTube playlist. " +
            "Alt+Left, Backspace or Escape goes back.",
        _ => VideoListKeys
    };

    public const string YouTubeSubscriptionsSteps =
        """
        Your YouTube subscriptions, playlists, liked videos and watch history can be exported from Google with Google Takeout. Steps:

        1. Choose Yes below to open Google Takeout in your browser, set to YouTube only. Sign in to Google if asked.
        2. YouTube and YouTube Music is already checked. Activate the button "All YouTube data included", choose Deselect all, check "subscriptions", "playlists" and "history", then OK.
        3. For watch history, activate "Multiple formats" and set history to JSON instead of HTML. Otherwise history is skipped.
        4. Activate "Next step". Leave the delivery method as "Send download link via email" and the file type as .zip, then activate "Create export".
        5. Google emails you a download link, usually within a few minutes. Download the zip file.
        6. Back in TimePlayer: Playlists (Control+9), Import from Google Takeout, and choose that zip. There is no need to unzip it. Channels, Import subscriptions does the same.

        Open Google Takeout now?
        """;

    public const string Queue =
        "Videos play in this order. Enter plays one now, Delete removes it. The Applications key also offers Clear queue.";

    public static string Player(double smallSeek, double largeSeek) =>
        $"K plays or pauses (Space too, unless a button has focus). Left and Right move {Seconds(smallSeek)}, Control+Left and Right {Seconds(largeSeek)}.\n" +
        "0 to 9 jump to 0 to 90 percent. Home starts over. Up and Down change the volume, M mutes.\n" +
        "T says the time, I says what is playing, U says what plays next.\n" +
        "Shift+N next video, Shift+P previous. Shift+Period faster, Shift+Comma slower.\n" +
        $"On the Position slider, Left and Right move {Seconds(smallSeek)} and Page Up and Page Down {Seconds(largeSeek)}.\n" +
        "Alt+U Up next, Alt+C comments, Alt+D description, Alt+Q queue. F6 moves between the controls, those panels and the top bar.\n" +
        "A turns autoplay on or off. C turns reading captions aloud on or off.\n" +
        "S skips the SponsorBlock segment playing now, H jumps to the video's highlight.\n" +
        "Control+K likes, Control+P adds to a playlist, Control+S subscribes, Control+L Watch Later, Control+G goes to the channel.\n" +
        "Control+C copies a share link that starts where you are; Control+Shift+C copies one from the beginning. Control+D downloads audio or video.\n" +
        "Escape closes the player.";

    private static string Seconds(double value) => value == 1 ? "1 second" : $"{value:0.#} seconds";

    public const string UpNext =
        "Related videos. Enter plays one now, Control+Q queues it, Control+K likes it, Control+P adds it to a playlist. With autoplay on, the first unwatched one plays when the queue ends.\n" +
        "Escape returns to the player controls.";

    public const string Comments =
        "Comments are a tree. Each is read as: who, what they said, then when, likes and replies. More comments load as you reach the end.\n" +
        "A comment with replies is collapsed: Right arrow opens its replies, which are one level deeper and start with \"Reply from\". " +
        "Right again moves into them; Left goes back up to the comment, and Left on the comment closes it. Enter also opens or closes. " +
        "\"More replies\" at the end of a reply list loads the rest with Enter.\n" +
        "Control+C copies a comment. Escape returns to the player controls.";

    public const string Description =
        "The video description. Use the arrow keys to read it line by line. Escape returns to the player controls.";
}
