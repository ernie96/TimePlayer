TimePlayer
==========

TimePlayer is a YouTube player for Windows made for screen reader and keyboard users
(NVDA, JAWS, Narrator), with optional braille display support through NVDA.
No Google account or sign-in is needed.


Installing
----------

Installer (TimePlayer_setup.exe): run it and follow the steps. It installs for your
Windows user only, so it does not ask for administrator rights. Your settings and library
are kept in %LOCALAPPDATA%\TimePlayer.

Portable zip (TimePlayer.zip): extract the whole folder anywhere you can write to (not Program Files) and
run TimePlayer.exe. Because the folder contains portable.txt, your settings, library and
logs are kept inside that folder, so you can carry it on a USB drive. Delete portable.txt
to keep them in %LOCALAPPDATA%\TimePlayer instead.


First start
-----------

Focus starts in the search box. Type and press Enter, or press Control+2 for Featured videos.

If NVDA is installed, TimePlayer asks once whether you use a braille display. Answer Yes to
install a small NVDA add-on that shows the playback position on the display and lets the
routing keys jump through the video; restart NVDA once afterwards. Answer No if you do not
use braille. You can change this later in Settings (F2). The add-on is also included as
TimePlayerBraille.nvda-addon if you prefer to install it from NVDA yourself.


Keys
----

Press F1 anywhere: it explains the keys for the part of the app that has focus.
F6 moves between the main areas. The Applications key lists the actions for any item.

Library: Control+1 to Control+7 open Search, Featured, What's New, Channels, History,
Watch Later and Queue. In any list, Enter plays, Control+Q queues, Control+L saves to
Watch Later, Control+S subscribes, Control+G opens the channel, Control+C copies a share
link and Control+D downloads.

Player: K plays and pauses, Left and Right seek, 0 to 9 jump through the video, T says the
time, Alt+U, Alt+C, Alt+D and Alt+Q open Up next, Comments, Description and Queue.
Escape goes back.


Subscriptions
-------------

Press Control+4 for Channels. "How to get my YouTube subscriptions" walks you through
exporting them from Google Takeout; then use Import subscriptions and choose the zip
Google sends you. NewPipe, LibreTube, FreeTube and OPML files can be imported too, and
Export subscriptions saves a file NewPipe can read.


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
Settings also has "Check for TimePlayer updates" to look right away.


Problems
--------

Settings (F2) has "Open log folder". Please attach the newest log file when reporting a
problem. TimePlayer updates its YouTube downloader (yt-dlp) by itself at startup; if
videos stop playing, "Update YouTube downloader" in Settings usually helps.


Uninstalling
------------

Installer: use Windows Settings, Apps, TimePlayer, Uninstall. Your library stays in
%LOCALAPPDATA%\TimePlayer; delete that folder to remove it too.
Portable: delete the folder.
If you installed the braille add-on, remove "TimePlayer Braille progress track" in NVDA's
Add-on Store, or untick it in TimePlayer's Settings first.


Third-party software
--------------------

TimePlayer includes mpv (GPL), yt-dlp (Unlicense) and the NVDA Controller Client (LGPL).
See THIRD_PARTY_NOTICES.md for details and source code locations.
