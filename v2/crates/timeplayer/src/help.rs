//! F1 (one or two spoken sentences) and Ctrl+F1 (the full help for the place), 1.x's `QuickHelp` and
//! `KeyboardHelp` with the same English texts, so their translations carry over.

use timeplayer_core::i18n::{plural, t, tf};

use crate::sections::Section;

fn with_more(text: &str) -> String {
    format!("{text} {}", t("Control+F1 for more help."))
}

pub fn startup() -> String {
    t("Press F1 for help.")
}

pub mod quick {
    use super::*;

    pub fn search() -> String {
        with_more(&t("Search box. Type words or paste a YouTube link, then press Enter. Escape clears the box, and Escape again goes back to the list."))
    }

    pub fn sections(music: bool) -> String {
        with_more(&if music {
            t("Sections. Left and Right arrows switch sections, or Control+1 to 4 go straight to one.")
        } else {
            t("Sections. Left and Right arrows switch sections, or Control+1 to 8 go straight to one.")
        })
    }

    pub fn now_playing() -> String {
        with_more(&t("Now playing. Tab moves through Play, Previous, Next, Open player and Stop. Control+Space plays or pauses from anywhere."))
    }

    pub fn list(section: Section, count: usize, empty_text: &str) -> String {
        if count == 0 {
            return with_more(&empty_text.replace('\n', " "));
        }
        let moving = t("Up and Down move through the list; the first letter of a title jumps to it.");
        let acting = match section {
            Section::Channels => t("Enter shows the channel's videos, and Delete unsubscribes."),
            Section::Playlists => t("Enter opens the playlist. The Applications key lists what you can do with it."),
            Section::Queue => t("Enter plays the video now, and Delete takes it out of the queue."),
            _ => t("Enter plays. The Applications key lists what you can do with the selected video."),
        };
        with_more(&format!("{moving} {acting}"))
    }

    pub fn player() -> String {
        with_more(&t("Player. K or Space plays or pauses, Left and Right move through the video, and Up and Down change the volume. Alt+A or the Applications key lists every action, and Escape leaves the player."))
    }

    pub fn shorts() -> String {
        with_more(&t("Shorts. Up and Down move between Shorts, and each one repeats. Shift+Up and Shift+Down change the volume, and Escape leaves Shorts."))
    }

    pub fn up_next() -> String {
        with_more(&t("Up next. Enter plays the selected video, and the Applications key lists more. Escape returns to the player controls."))
    }

    pub fn comments() -> String {
        with_more(&t("Comments. Right arrow opens a comment's replies and Left closes them. Escape returns to the player controls."))
    }

    pub fn description() -> String {
        with_more(&t("Description. Read it with the arrow keys. Escape returns to the player controls."))
    }

    pub fn chapters() -> String {
        with_more(&t("Chapters. Enter goes to the chapter; while playing, Shift+Left and Shift+Right move between chapters. Escape returns to the player controls."))
    }

    pub fn transcript() -> String {
        with_more(&t("Transcript. Up and Down read it line by line, and Enter plays from that line. Escape returns to the player controls."))
    }

    pub fn lyrics() -> String {
        with_more(&t("Lyrics. Up and Down read them line by line, and Enter plays from a timed line. L reads them aloud as they're sung."))
    }

    pub fn player_queue() -> String {
        with_more(&t("Queue. Enter plays the video now, and Delete takes it out of the queue. Escape returns to the player controls."))
    }

    pub fn settings(page: &str) -> String {
        with_more(&tf("Settings, {0}. Tab moves between settings, and Control+Tab goes to the next page. Escape closes and saves.", &[&page]))
    }

    pub fn report_form() -> String {
        t("Describe it in the box, then Tab to Send. Escape closes without sending.")
    }
}

pub mod full {
    use super::*;

    pub fn search_box() -> String {
        t("Type words or paste a YouTube link, then press Enter. Results open as their own page; Alt+Left goes back.\n")
            + &t("Escape clears the box, and Escape again returns to the list. Tab moves to Search for (Videos, Playlists, Shorts, Live, Newest first or Most viewed) and Settings.\n")
            + &t("From anywhere, slash, Control+F or Control+E come back here. Control+Shift+V plays a YouTube link from the clipboard. Control+Shift+B reports a problem to TimePlayer's developer, and Control+Shift+F suggests a feature.\n")
            + &t("A YouTube playlist link opens the playlist.")
    }

    pub fn sections() -> String {
        t("Left and Right arrows switch sections. Tab moves through this section's buttons and list, and stays in the section.\n")
            + &t("Video: Control+1 Featured, 2 For You, 3 What's New, 4 Channels, 5 Playlists, 6 Watch Later, 7 History, 8 Queue.\n")
            + &t("Music: Control+1 Charts, 2 Recently played, 3 Playlists, 4 Queue. Control+M switches between Video and Music.\n")
            + &t("Control+9 is YouTube (your account's home, subscriptions, history and likes) when you use your account: Settings, YouTube account.\n")
            + &t("Control+Shift+S starts Shorts mode: Up and Down move between Shorts, and each one repeats.\n")
            + &t("Control+Tab goes to the next section. Slash searches. F6 moves between the search box, the sections, the list and Now playing.\n")
            + &now_playing_keys()
    }

    pub fn now_playing_keys() -> String {
        t("While something plays: Control+Space plays or pauses, Control+Shift+N and Control+Shift+P go to the next and previous, and the keyboard's media keys work. ")
            + &t("In Music, Alt+P opens the player.")
    }

    pub fn now_playing() -> String {
        t("Now playing: music keeps playing while you browse. Tab moves through Play, Previous, Next, Open player and Stop.\n") + &now_playing_keys()
    }

    fn video_list_keys() -> String {
        t("Enter plays. Control+Q adds to the queue, Control+L saves to Watch Later, ")
            + &t("Control+K likes, Control+P adds to a playlist, ")
            + &t("Control+S subscribes to the channel, Control+G opens the channel, Control+C copies a share link, ")
            + &t("Control+D downloads the audio or video. ")
            + &t("Control+R starts a radio of songs like it. ")
            + &t("Control+Shift+S plays the list's Shorts in Shorts mode. ")
            + &t("The Applications key lists every action.")
    }

    pub fn for_list(section: Section) -> String {
        let keys = video_list_keys();
        match section {
            Section::Search => t("Search results. ") + &keys + &t("\nMore results load as you reach the end of the list. Slash searches again; Alt+Left, Backspace or Escape goes back."),
            Section::Featured => {
                t("Featured videos. ")
                    + &keys
                    + &t("\nShift+Tab reaches the Category box: Trending, Music (the top 100 music videos), Gaming, Movies (trailers), News, Sports and Live. ")
                    + &t("Music, Gaming and Movies keep loading as you reach the end. F5 refreshes.")
            }
            Section::WhatsNew => t("Latest videos from your channels, newest first. Videos you have not seen yet start with New.\n") + &keys + &t("\nF5 checks for new videos."),
            Section::Channels => {
                t("Your subscribed channels. Enter shows a channel's videos. Delete or Control+S unsubscribes.\n")
                    + &t("Shift+Tab reaches Import and export, the Import page of Settings: sign in to YouTube, Google Takeout, and files from NewPipe, LibreTube, FreeTube and OPML.")
            }
            Section::History => t("Videos you watched, newest first. Partly watched ones say where they will resume.\n") + &keys + &t("\nDelete removes one video from history."),
            Section::WatchLater => t("Videos you saved. ") + &keys + &t("\nDelete removes a video from Watch Later."),
            Section::Channel => {
                t("A channel's videos, newest first. Older videos load as you reach the end.\n")
                    + &keys
                    + &t("\nShift+Tab reaches the Tab box: Videos, Shorts, Live and Playlists. Control+D on a video offers to download the whole list.")
                    + &t("\nAlt+Left, Backspace or Escape goes back.")
            }
            Section::Queue => queue(),
            Section::MusicCharts => {
                t("Charts: the top 100 music videos for your region (Settings, Library). Enter plays a song; music keeps playing while you browse. ") + &keys + "\n" + &now_playing_keys()
            }
            Section::MusicRecent => t("Songs you played in Music, newest first. ") + &keys + &t("\nDelete removes a song from history.\n") + &now_playing_keys(),
            Section::ForYou => {
                t("Videos picked for you from what you watch, like and subscribe to. Nothing leaves this computer.\n")
                    + &keys
                    + &t("\nDelete means not interested; Shift+Delete stops recommending that channel. F5 finds new picks.")
            }
            Section::Playlists => {
                t("Liked videos and your playlists (Control+5). Enter opens one. Shift+Tab reaches Play all, Shuffle, New playlist, Rename, Delete, and Import and export, which opens the Import page of Settings.\n")
                    + &t("Control+C copies a link to share the playlist. Delete deletes the selected playlist after asking.")
            }
            Section::Playlist => {
                t("The videos in this playlist. ")
                    + &keys
                    + "\n"
                    + &t("In your own playlists, Delete removes a video and Alt+Up or Alt+Down moves it. ")
                    + &t("Shift+Tab reaches Play all, Shuffle, Copy link, and Save to Playlists for YouTube playlists. F5 updates a saved YouTube playlist. ")
                    + &t("Alt+Left, Backspace or Escape goes back.")
            }
            Section::YouTube => keys,
        }
    }

    pub fn youtube_subscriptions_steps() -> String {
        t("The quickest way is Settings (F2), Import, Sign in to YouTube and import. Sign in on Google's page; TimePlayer reads your subscriptions, history, likes, Watch Later and playlists, closes the page, and asks whether to stay signed in. If that doesn't work for you, Google Takeout exports the same lists.\n\n")
            + &t("Your YouTube subscriptions, playlists, liked videos and watch history can be exported from Google with Google Takeout. Steps:\n\
1. Choose Yes below to open Google Takeout in your browser, set to YouTube only. Sign in to Google if asked.\n\
2. YouTube and YouTube Music is already checked. Activate the button \"All YouTube data included\", choose Deselect all, check \"subscriptions\", \"playlists\" and \"history\", then OK.\n\
3. For watch history, activate \"Multiple formats\" and set history to JSON instead of HTML. Otherwise history is skipped.\n\
4. Activate \"Next step\". Leave the delivery method as \"Send download link via email\" and the file type as .zip, then activate \"Create export\".\n\
5. Google emails you a download link, usually within a few minutes. Download the zip file.\n\
6. Back in TimePlayer: Settings (F2), Import, Import from Google Takeout, and choose that zip. There is no need to unzip it.\n\
Open Google Takeout now?")
    }

    pub fn queue() -> String {
        t("Videos play in this order. Enter plays one now, Delete removes it. The Applications key also offers Clear queue.")
    }

    fn seconds(value: f64) -> String {
        if value.fract() == 0.0 { plural(value as i64, "{0} second", "{0} seconds") } else { tf("{0:0.#} seconds", &[&value]) }
    }

    pub fn player(small_seek: f64, large_seek: f64) -> String {
        let (small, large) = (seconds(small_seek), seconds(large_seek));
        tf("K plays or pauses (Space too, unless a button has focus). Left and Right move {0}, Control+Left and Right {1}.\n", &[&small, &large])
            + &t("0 to 9 jump to 0 to 90 percent. Home starts over. Up and Down change the volume, M mutes.\n")
            + &t("T says the time, I says what is playing, U says what plays next.\n")
            + &t("Shift+N next video, Shift+P previous. Shift+Period faster, Shift+Comma slower.\n")
            + &tf("On the Position slider, Left and Right move {0} and Page Up and Page Down {1}.\n", &[&small, &large])
            + &t("Alt+U Up next, Alt+C comments, Alt+D description, Alt+Q queue. F6 moves between the controls, those panels and the top bar.\n")
            + &t("A turns autoplay on or off. C turns reading captions aloud on or off.\n")
            + &t("S skips the SponsorBlock segment playing now, H jumps to the video's highlight.\n")
            + &t("E and Shift+E switch the sound preset, Control+M turns music mode (no video, music sound) on or off.\n")
            + &t("D turns audio description on or off. Shift+Left and Shift+Right go to the previous and next chapter.\n")
            + &t("Alt+H chapters, Alt+T transcript, Alt+L lyrics. L reads lyrics aloud as they're sung, and R starts a radio of songs like this one.\n")
            + &t("Control+K likes, Control+P adds to a playlist, Control+S subscribes, Control+L Watch Later, Control+G goes to the channel.\n")
            + &t("Control+C copies a share link that starts where you are; Control+Shift+C copies one from the beginning. Control+D downloads audio or video.\n")
            + &t("Alt+A or the Applications key opens Actions: like, subscribe, Watch Later, playlist, share, download, captions, speed, sound and more.\n")
            + &t("Slash leaves the player and goes to search. Escape leaves the player: a video stops, music keeps playing.\n")
            + &t("Control+M switches between Video and Music.")
    }

    pub fn up_next() -> String {
        t("Related videos. Enter plays one now, Control+Q queues it, Control+K likes it, Control+P adds it to a playlist. With autoplay on, the first unwatched one plays when the queue ends.\n")
            + &t("Escape returns to the player controls.")
    }

    pub fn comments() -> String {
        t("Comments are a tree. Each is read as: who, what they said, then when, likes and replies. More comments load as you reach the end.\n")
            + &t("A comment with replies is collapsed: Right arrow opens its replies, which are one level deeper and start with \"Reply from\". ")
            + &t("Right again moves into them; Left goes back up to the comment, and Left on the comment closes it. Enter also opens or closes. ")
            + &t("\"More replies\" at the end of a reply list loads the rest with Enter.\n")
            + &t("Control+C copies a comment. Escape returns to the player controls.")
    }

    pub fn chapters() -> String {
        t("Chapters come from the timestamps in the video's description. Enter goes to a chapter. While playing, Shift+Left and Shift+Right move between chapters, and T says which chapter you are in. ")
            + &t("On a braille display, dots 7 and 8 mark where chapters start, and a routing key on one goes to that chapter.")
    }

    pub fn transcript() -> String {
        t("The video's captions as a document, each line followed by its time. Up and Down read it, the first letter of a word jumps to a line starting with it, and Enter plays from the selected line. ")
            + &t("It uses the captions chosen in the player, or else the best for your language.")
    }

    pub fn lyrics() -> String {
        t("The song's lyrics from LRCLIB, a free lyrics library. Up and Down read them, and Enter plays from a timed line. ")
            + &t("L in the player reads timed lyrics aloud as they're sung, like captions; Settings, Speech and braille, can keep it on.")
    }

    pub fn description() -> String {
        t("The video description. Use the arrow keys to read it line by line. Escape returns to the player controls.")
    }
}
