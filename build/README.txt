TimePlayer
==========

TimePlayer is a YouTube player for Windows made for screen reader and keyboard users
(NVDA, JAWS, Narrator), with optional braille display support through NVDA.
No Google account or sign-in is needed.


Installing
----------

Installer (TimePlayer_setup.exe): run it and follow the steps. It speaks English, Spanish,
Romanian and Ukrainian, and installs for your Windows user only, so it does not ask for
administrator rights. Your settings and library are kept in %LOCALAPPDATA%\TimePlayer.

Portable zip (TimePlayer.zip): extract the whole folder anywhere you can write to (not
Program Files) and run TimePlayer.exe. Because the folder contains portable.txt, your
settings, library and logs are kept inside that folder, so you can carry it on a USB drive.
Delete portable.txt to keep them in %LOCALAPPDATA%\TimePlayer instead.


First start
-----------

TimePlayer says "Press F1 for help", and focus starts in the search box. Type and press
Enter, or press Control+1 for Featured videos.

If NVDA is installed, TimePlayer asks once whether you use a braille display. Answer Yes to
install a small NVDA add-on that shows the playback position on the display and lets the
routing keys jump through the video; restart NVDA once afterwards. Answer No if you do not
use braille. You can change this later in Settings (F2). The add-on is also included as
TimePlayerBraille.nvda-addon if you prefer to install it from NVDA yourself.


Help
----

TimePlayer keeps quiet while you work, and help is there when you ask for it:

F1 says, in a sentence or two, where you are and the keys that matter most there. Try it
in the search box, a list, the player or Settings.
Control+F1 opens the full help for that place in a window you can read at your own pace.
In lists, the Applications key lists everything you can do with the selected item.


Keys
----

F6 moves between the main areas. / (slash) searches from anywhere.

Video: Control+1 Featured, 2 For You, 3 What's New, 4 Channels, 5 Playlists,
6 Watch Later, 7 History, 8 Queue.
Music: Control+1 Charts, 2 Recently played, 3 Playlists, 4 Queue.
Control+M switches between Video and Music.

In any list, the first letter of a title jumps to it and Enter plays. Control+Q queues,
Control+L saves to Watch Later, Control+K likes, Control+P adds to a playlist,
Control+S subscribes, Control+G opens the channel, Control+C copies a link to share and
Control+D downloads.

Player: K plays and pauses, Left and Right seek, 0 to 9 jump through the video, T says the
time, Alt+U, Alt+C, Alt+D and Alt+Q open Up next, Comments, Description and Queue.
S skips a sponsor or other SponsorBlock segment, H jumps to the video's highlight.
Alt+A lists every action. Escape goes back.

Anywhere: Control+Shift+V plays a YouTube link you copied, Control+Shift+B reports a
problem, Control+Shift+F suggests a feature, F2 opens Settings.


Languages
---------

TimePlayer speaks English, Spanish, Romanian and Ukrainian. It follows the Windows language;
to choose, open Settings (F2), Speech and braille, Language, and restart TimePlayer.
Thank you to Stacy Carter for Ukrainian support.


Opening links from your browser and other apps
----------------------------------------------

Copy a YouTube link anywhere and press Control+Shift+V in TimePlayer to play it.

The "Play in TimePlayer" browser extension (Chrome, Edge, Brave) adds a "Play in TimePlayer"
link after YouTube links on other websites, and Alt+Shift+P plays the YouTube page you are
on. Settings (F2), Links and browser, has the steps to add it, a bookmark that works
without the extension, and "Register TimePlayer for links again" if links stop opening.
YouTube Music links open in Music.

Control+C asks whether to copy a YouTube link, which opens anywhere, or a TimePlayer link,
which opens straight in TimePlayer for people who have it. Settings, Links and browser,
can always use one of them instead.


Subscriptions, playlists and For You
------------------------------------

Everything an account would keep stays on this computer instead: subscriptions, likes,
playlists, Watch Later and history. Nothing is sent to Google.

For You (Control+2) suggests videos from what you watch, like and subscribe to. Delete on
a video means not interested; Shift+Delete stops recommending that channel.

Playlists (Control+5) holds Liked videos and your own playlists. Paste a YouTube playlist
link into the search box to open it, and Save to Playlists keeps it. To find playlists,
choose Playlists in the "Search for" box next to the search box.

Bring your YouTube account's data with Google Takeout: in Playlists, "How to get my
YouTube data" walks you through it; then Import from Google Takeout and choose the zip
Google sends you. It brings subscriptions, playlists, liked videos and watch history.
NewPipe, LibreTube, FreeTube and OPML subscription files can be imported in Channels
(Control+4), and Export subscriptions saves a file NewPipe can read.


Sound and music mode
--------------------

Settings (F2), Sound, has effects for TimePlayer's sound only: presets such as Music, Bass
boost, Headphones, Voice and podcasts, Night and Concert hall, and a custom sound with a
10-band equalizer. In the player, E and Shift+E switch presets.
Music has its own sections, no video, and its own sound preset. Songs keep playing while
you browse: Control+Space plays or pauses, Control+Shift+N and Control+Shift+P go to the
next and previous, Alt+P opens the player, and Escape returns to the lists.


SponsorBlock and dislikes
-------------------------

SponsorBlock skips sponsor reads automatically and tells you about other marked parts, such
as self-promotion, with how long they last; press S to skip one. Each kind can be set to skip,
tell you, or do nothing in Settings (F2), on the "SponsorBlock and dislikes" page.
Likes and estimated dislikes (from Return YouTube Dislike) are read at the top of the
description and when you press I in the player.


Downloads
---------

Control+D downloads the audio or video to Downloads\TimePlayer. With ffmpeg installed,
audio is saved as MP3 and video at full quality. Without it, audio is saved as M4A and
video as a single MP4. To install ffmpeg, open a command prompt and run:
    winget install Gyan.FFmpeg


Updates
-------

TimePlayer checks for a new version when it starts (you can turn this off in Settings, F2)
and offers it with a list of what changed. Choose Yes and it downloads the update, checks
that it really comes from TimePlayer's publisher, installs it and opens again by itself.
Settings, Updates and help, also has "Check for TimePlayer updates" to look right away.


Problems and ideas
------------------

Press Control+Shift+B to report a problem, or Control+Shift+F to suggest a feature. Say
what happened or what you would like; a report can include TimePlayer's log, with your
Windows user name taken out. It goes privately to TimePlayer's developer, and no account
is needed.

TimePlayer updates its YouTube downloader (yt-dlp) by itself at startup; if videos stop
playing, "Update the YouTube downloader" in Settings, Updates and help, usually helps.


Uninstalling
------------

Installer: use Windows Settings, Apps, TimePlayer, Uninstall. Your library stays in
%LOCALAPPDATA%\TimePlayer; delete that folder to remove it too.
Portable: delete the folder.
If you installed the braille add-on, remove "TimePlayer Braille progress track" in NVDA's
Add-on Store, or untick it in TimePlayer's Settings first.


Third-party software
--------------------

TimePlayer is free software under the BSD Zero Clause License (see LICENSE.txt).
It includes mpv (GPL), yt-dlp (Unlicense) and the NVDA Controller Client (LGPL), which keep
their own licenses. See THIRD_PARTY_NOTICES.md for the components used in this program and
where to get their source code.

TimePlayer is dedicated to Jenni and Diana. The full dedication is at the top of
THIRD_PARTY_NOTICES.md.
