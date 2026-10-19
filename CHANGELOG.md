# Changelog

The release workflow publishes the section for each version as its release notes, and
TimePlayer reads them out when it offers that update. Keep each entry short and plain.

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
