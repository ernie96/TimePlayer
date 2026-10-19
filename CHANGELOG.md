# Changelog

The release workflow publishes the section for each version as its release notes, and
TimePlayer reads them out when it offers that update. Keep each entry short and plain.

## 1.10.0

- Quieter, with help when you want it. F1 now says, in a sentence or two, where you are and the keys that matter most there, without opening a window; Control+F1 opens the full help for that place. TimePlayer says "Press F1 for help" when it starts, and an empty section just says it's empty: F1 tells you how to fill it.
- In lists, the first letter of a title now jumps to it. Before, the first letter of "New", "Watched" or "Playlist" was used.
- A new Settings page, Links and browser, has step-by-step help for adding the Play in TimePlayer browser extension, a button to copy the extension's folder location, the browser bookmark, the Control+C sharing choice, and "Register TimePlayer for links again" if links stop opening in TimePlayer.
- The installer speaks Romanian and Ukrainian too, as well as English and Spanish.
- Fixed: the Featured section's name was always in English.
- The user guide and README are up to date again (the guide still said Control+2 for Featured; it's Control+1).

## 1.9.0

- Report a problem: press Control+Shift+B (or Settings, Updates and help, Report a problem), say what went wrong, and send it, with TimePlayer's log if you like. It goes privately to TimePlayer's developer; no account is needed, and your Windows user name is taken out of the log.
- Suggest a feature: press Control+Shift+F (or Settings, Updates and help, Suggest a feature) and describe your idea. It also goes privately to TimePlayer's developer.
- TimePlayer speaks Spanish. Choose it in Settings, Speech and braille, or let it follow Windows.
- Open YouTube links in TimePlayer from other apps. A browser extension (Chrome, Edge and other Chromium browsers) adds "Play in TimePlayer" after YouTube links on websites such as Reddit or webmail, or makes them open in TimePlayer directly; on YouTube, use its toolbar button, Alt+Shift+P or the context menu. Its idea comes from the Open in RedditForBlind extension.
- Without the extension: a bookmark (Settings, Updates and help, "Copy the browser bookmark"), timeplayer:// links, or "TimePlayer.exe <link>". Control+Shift+V still plays a copied link.
- A link from elsewhere goes to the TimePlayer that's already open and brings it to the front. Only YouTube videos and playlists are accepted from outside, and Settings can turn timeplayer:// links off.
- YouTube Music links (music.youtube.com) work everywhere links do, and switch TimePlayer to Music.
- Sharing with Control+C or Control+Shift+C now asks: a YouTube link, which opens anywhere, or a TimePlayer link, which opens straight in TimePlayer for people who have it. Settings, Updates and help, "Control+C shares" can skip the question.
- Fixed: searching for a word that happens to be 11 letters long, such as "taylorswift", tried to play it as a video instead of searching.
- Fixed: in the player, the Applications key opened the Actions menu and closed it again at once. It now stays open, like Alt+A.
- Fixed: Ukrainian seemed to be missing from Settings, Language, because many English voices read Cyrillic letters as nothing. Each language is now also named in the language TimePlayer is using, for example "Українська (Ukrainian)".
- Menus and Settings pages no longer have two items on the same underlined letter, in any language (in English: Add to playlis_t and Cop_y share link in the list menu, and Download audio (_MP3) and (M_4A)).
- Fixed: the OK and Cancel buttons when naming a playlist were always in English.
- Tidier inside: about 1,900 lines of code nothing used are gone, including the old Piped backend, unused settings and the console logger, which a window app never shows.

## 1.8.0

- TimePlayer can tell you at startup how many times it has been run on your computer. Turn this off in Settings, Updates and help.
- You can also add your runs to a shared total for everyone, and hear that total too. TimePlayer asks once at startup. Each start adds 1 to a public counter (Abacus); nothing else is sent, not your name, your computer or what you watch. Change it in Settings, Updates and help.

## 1.7.1

- Fixed: updates could fail silently when a second TimePlayer window was open (for example, if TimePlayer was started twice). The installer won't replace TimePlayer while any copy runs, so the old version simply came back. Now TimePlayer runs once: starting it again brings the open window to the front.
- Before installing an update, TimePlayer checks for other open copies and tells you to close them. If the installer fails for any other reason, TimePlayer tells you at its next start instead of quietly reopening the old version.

## 1.7.0

- TimePlayer speaks Romanian and Ukrainian. Choose the language in Settings, Speech and braille (it follows Windows by default); it applies after a restart. Everything is translated: what TimePlayer says, the screens, the menus and F1 help, with the right word forms for counts ("1 videoclip, 25 de videoclipuri", "1 канал, 3 канали, 5 каналів").
- Thank you to Stacy Carter for Ukrainian support.
- Faster on older computers: the player's position is passed on four times a second instead of on every video frame, long queues update only the rows that change, lists reuse their rows while scrolling, and Music no longer starts a separate caption lookup for every song.
- Importing a large Google Takeout zip (with uploaded videos) no longer loads the whole file into memory.
- Safer updates: the update helper now handles curly apostrophes in Windows user names (such as O’Brien) correctly, and the bundled player (mpv) is checked against its published checksum when downloaded.
- Logs no longer record full video stream addresses, which can contain your internet address. Logs are often shared when reporting problems.
- Fixed a memory leak when the player restarts after a crash.
- Fixed: the first section's announcement could be silenced if you switched sections while Featured was still loading at startup.

## 1.6.0

- Music is now its own mode: Control+M switches the whole app between Video and Music. Music has its own sections: Control+1 Charts, 2 Recently played, 3 Playlists and 4 Queue.
- In Music, songs keep playing while you browse. A Now playing bar shows what's on. Control+Space plays or pauses, Control+Shift+N and Control+Shift+P go to the next and previous, and the keyboard's media keys work. Alt+P opens the player, and Escape returns to the lists without stopping.
- The music player has no video area; Up next, Description and Queue fill the window.
- Switching to Video while a song plays opens it in the video player.
- Songs that can't be played no longer stop a playlist. TimePlayer says "Skipped" with the title and moves on to the next song; three in a row stop the queue with an explanation.
- A video YouTube won't play (removed, private or blocked where you are) is reported in about 10 seconds instead of 25 or more, and the message says why.
- Playback problems are announced once instead of twice.
- Thank you, Jenni, for the logs that found the skipping, waiting and double-announcement problems above.

## 1.5.0

- Search from anywhere: press slash (or Control+F). Results open as their own page, and Alt+Left goes back. The Search tab is gone.
- Sections are now Control+1 Featured, 2 For You, 3 What's New, 4 Channels, 5 Playlists, 6 Watch Later, 7 History and 8 Queue. Tab stays inside the section you're in, and F6 moves between the search box, the sections and the list.
- The video player is simpler: Back, Actions, Play, Previous, Next and Mute. Everything else is in the Actions menu (Alt+A or the Applications key), and every shortcut still works.
- Fixed: Shuffle and Play all froze TimePlayer on long playlists.
- Fixed: playing something new now starts a fresh queue instead of adding to the old one. Autoplay and Next still continue the current queue.
- Fixed: error messages can now be read. F6 reaches the reason, Retry and Dismiss, and a message always says what went wrong.
- Fixed: results from the YouTube downloader could occasionally be cut short.

## 1.4.0

- Sound effects for TimePlayer only, never the rest of Windows: equalizer, bass boost, clarity, stereo width, room ambience, headphone crossfeed, a compressor and volume leveling.
- Sound presets: Music, Bass boost, Headphones, Voice and podcasts, Night, Concert hall and Loudness, plus your own Custom sound. In the player, E and Shift+E switch presets.
- Music mode, Control+M: no video, which saves processing power and battery, and its own sound preset.
- Settings has a new Sound page.
- Fixed: in Settings, screen readers said "TimePlayer.Wpf.ViewModels.SegmentCategorySetting, data item" before each SponsorBlock choice. They now read just the setting.

## 1.3.0

- SponsorBlock: sponsor reads are skipped automatically, and other marked parts such as self-promotion are announced with how long they last. S skips the part playing now, H jumps to the video's highlight. Each kind can be set to skip, tell you, or do nothing.
- Likes and estimated dislikes from Return YouTube Dislike, at the top of the description and when you press I.
- Settings is now split into pages: Playback, Speech and braille, Library, SponsorBlock and dislikes, and Updates and help. Control+Tab moves between them.
- New settings: how far the arrow keys seek, how much Up and Down change the volume, and the region chosen from a list of countries.
- Screen readers now hear the name of every list in Settings; before, they only heard "combo box". Video quality reads as "720p, HD" instead of "P720".
- Hide Shorts now also hides them in Featured.
- Enter no longer closes Settings by accident; Escape still does.
- Fixed: "Not interested" could freeze TimePlayer while For You was still loading.

## 1.2.2

- Fixed "GitHub answered 404" when checking for updates. Copies first installed from an early test build had saved a placeholder update address in their settings, which kept pointing at a place that doesn't exist. TimePlayer now ignores that address and always finds official updates.

## 1.2.1

- Featured shows the right videos again. YouTube retired its Trending page, which left the old lists short and mixed up. Music is now the top 100 music videos for your region, Gaming is this week's gaming videos, and Movies is this week's most watched trailers. Trending mixes what YouTube features for music, sports, news and gaming. Music, Gaming and Movies keep loading as you reach the end.
- New Featured categories: News, Sports and Live.

## 1.2.0

- For You (Control+8): videos picked from what you watch, like and subscribe to, worked out on your computer. Delete means not interested; Shift+Delete stops recommending a channel.
- Playlists (Control+9): Liked videos, your own playlists, and YouTube playlists. Control+K likes a video, Control+P adds it to a playlist. Play all and Shuffle play a whole playlist.
- Paste a YouTube playlist link to open it, and save it to your Playlists. Searches can find playlists too: choose Playlists in the Find box.
- Import from Google Takeout now brings your playlists, liked videos and watch history as well as your subscriptions.
- More search results now load correctly; before, "more" could load a differently filtered list.

## 1.1.0

- TimePlayer now updates itself. It checks for a new version when it starts, tells you what changed, and installs it if you choose Yes. Updates are signed, and TimePlayer refuses any download that is not signed by its publisher.
- Settings has "Check for TimePlayer updates" and an option to turn off the startup check.

## 1.0.1

- Playing a video again in the same session now continues where you left off.
- Videos whose stream comes without captions now get their captions looked up separately. C says plainly when a video has none.
- More reliable with NVDA: the braille add-on no longer queries NVDA ten times a second, and captions are spoken without making TimePlayer wait on NVDA.

## 1.0.0

- First release: search, Featured, What's New, Channels, History, Watch Later and Queue; Up next with autoplay; comments with replies; downloads; share links; braille progress through NVDA.
