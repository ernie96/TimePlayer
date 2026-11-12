# Porting TimePlayer 1 to TimePlayer 2 (Rust and wxDragon)

Built from TimePlayer 1's code: every `[RelayCommand]` in the view models (105), every setting in `AppConfig`,
the keys in `MainWindow.xaml.cs`, the help in `KeyboardHelp.cs` and `QuickHelp.cs`, the windows, and
`CHANGELOG.md` from 1.0.0 to 1.12.1. Nothing here is from memory.

`[x]` is done in 2.0 and behaves as in 1.x, `[~]` is partly done (what's missing is said), `[ ]` is not started.
A line only gets `[x]` once it has been run, not just compiled.

## Rules carried over

- **Help, menus and speech.** Every feature gets F1 (one or two spoken sentences) and Ctrl+F1 (the full help
  for that place). Speech stays quiet: one line per action, background events summed up.
- **Keys.** 1.x's keys and access letters stay unless a change is written down here. Shortcut keys only for
  actions people repeat.
- **Texts.** Every visible text goes through `t()`, `tf()` or `plural()`, using 1.x's English texts exactly, so
  their Romanian, Ukrainian and Spanish carry over (`crates/timeplayer-core/translations.json`, made from 1.x's
  `*.Strings.cs` by `build/translations/export.py`). New texts get all three languages in the same change.
- **Data.** 2.0 reads and writes 1.x's files in the same folder (settings, library, history, playlists, sign-in),
  so updating changes nothing for the user.
- **Updates.** 1.x's updater must install 2.0 by itself: same asset names (`TimePlayer_setup.exe`,
  `TimePlayer.zip`), same Ed25519 key and signed message.
- **Releases.** Commit, tag, push or release only when asked. No Claude attribution anywhere.

## Status (2026-11-10)

Core ported and unit-tested (57 tests; mpv IPC checked against a real mpv). The app is written end to end and
builds without warnings (clippy clean). The self-check passed 21 of 21 steps three runs in a row on Windows
(2026-11-10), and TimePlayer exits cleanly. That covers the main window's keys and windows opening and closing;
it doesn't play a video, search, or check speech with NVDA, so every app line below stays `[~]` until a session
with NVDA has gone through it. Not started: packaging, CI, the Linux and macOS builds.

Ernesto ran the release build with NVDA on 2026-11-10 and reported that everything he tried works. Which
features he tried wasn't listed one by one, so the lines below stay `[~]` until each is confirmed.

Since 2.0.0 (unreleased): an Import page in Settings, a first-start import offer, YouTube sync options (watch
history and Watch Later sent to the account, new items brought in at startup; written against YouTube's own
calls but not yet tried on a real account), the main list named after its section (quiet-list vendored in
`vendor/quiet-list` with `set_name` added), and Tab from the section tabs going straight to the section's first
control instead of the tab's empty page. The self-check has 28 steps, all passing, including the Tab order.

Fixed by the self-check: the startup questions blocked the first steps (the check now presets them), and
TimePlayer aborted at exit, because Windows runs the main thread's destructors after the other threads have
ended and dropping the HTTP client then waited on a thread that was gone (the app is now let go at exit
without being dropped, and nothing posts to the window once it's closing).

To run the self-check (debug builds only; it uses a temporary data folder, never the real library):
`$env:TIMEPLAYER_SELF_CHECK="1"; D:	in-pan2	arget\debug\TimePlayer.exe`, then read the log in
`%TEMP%\timeplayer-self-check\logs` for "Self-check passed" and "Self-check FAILED" lines.

## Workspace

- [~] `v2/` Cargo workspace: `timeplayer-core` (no windows: YouTube sources, playback logic, library, settings,
  translations, updates) and `timeplayer` (wxDragon windows, speech, mpv video window, network thread).
- [x] `env.ps1` for MSVC, CMake, Ninja and libclang, as Tin Pan 2.
- [ ] CI: `build-v2.yml` (test and build on main, cache), `release-v2.yml`.
- [ ] Linux (AppImage) and macOS builds.

## Core: settings, paths, translations

- [~] Data folder as 1.x (`AppPaths`): `%LOCALAPPDATA%\TimePlayer` on Windows; the XDG data folder on Linux,
  Application Support on macOS.
- [~] Settings file (`ConfigurationService`): same JSON, every section of `AppConfig` (Playback, Backend,
  Accessibility, Library, Update, SponsorBlock, Dislikes, Audio, Runs, Account, Language, OpenLinksFromOtherApps,
  ShareLinks), atomic writes, unknown fields kept.
- [~] Translations: t, tf, plural with 1.x's plural rules (Romanian three forms, Ukrainian three, Spanish two);
  language follows the system unless chosen.
- [~] Logging to the same log folder, with stream addresses and the user name taken out (`LogRedaction`).

## Core: models and links

- [~] YouTube video IDs, URLs (watch, youtu.be, shorts, embed, live, music.youtube.com, playlists, timestamps).
- [~] Incoming links (`IncomingLink`): normalize, YouTube Music detection, timeplayer:// links, LooksLikeLink
  rule (an 11-letter word is not a video ID).
- [~] Search results, video details, captions, comments, chapters, paged results, queue items.

## Core: YouTube sources (tried in order, as 1.x)

- [~] TubeRepair: search, details, streams, feeds (`TubeRepairFeedParser`).
- [~] Invidious: instance directory, health checks and cache, search, details, comments, channels, playlists.
- [~] InnerTube: search (with filters: Shorts, Live, Newest first, Most viewed, playlists), browse, player,
  next (Up next, radio mixes), comments, channel tabs (Videos, Shorts, Live, Playlists), playlists, Shorts
  lockups, audio-description check (IOS client), signed-in pages, likes and subscriptions sent to YouTube,
  account import (subscriptions, history, liked, Watch Later, playlists, with progress and cancel).
- [~] yt-dlp: download and verify (per platform), self-update, the JavaScript runtime (Deno, Node, or pinned
  QuickJS), config file, details and streams, audio description formats, captions, error classifier, cookies.
- [~] Downloads: audio MP3/M4A, video, ffmpeg detection, whole-list downloads into numbered folders, safe folder
  names, signed-in retry for age-restricted videos.

## Core: playback

- [~] mpv: find or download (Windows, checksum), start with IPC (named pipe on Windows, socket elsewhere),
  commands, properties, events, restart after a crash.
- [~] Engine: play, pause, seek, volume (boost to 200), mute, speed, loop (Shorts), audio device, captions
  track, audio filters (sound presets), music mode (no video).
- [~] Coordinator: the source tiers, audio description first when wanted, signed-in retry, prefetch (Shorts),
  queue with autoplay and Up next, skipping unplayable songs (three in a row stop), resume positions, history.
- [~] Sound effects and presets (`AudioEffects`): Flat, Music, Bass boost, Headphones, Voice and podcasts,
  Night, Concert hall, Loudness, Custom.

## Core: library and discovery

- [~] Library (`LibraryService`): subscriptions, history (resume, watch counts, music history), Watch Later,
  likes, playlists (create, rename, delete, reorder, saved YouTube playlists), not interested, blocked channels,
  queue; same files as 1.x.
- [~] What's New from channel RSS feeds, new-since-last-visit, Shorts marked and optionally hidden, startup
  check.
- [~] Featured categories: Trending, Music (top 100), Gaming, Movies, News, Sports, Live; region.
- [~] For You recommendations (`RecommendationService`), three-hour cache.
- [~] Music charts and Recently played (the Music category of Featured, and the history's songs played in Music).
- [~] Imports: Takeout zip (subscriptions, playlists, likes, Watch Later, JSON history), NewPipe/LibreTube JSON,
  subscriptions.csv, OPML, FreeTube profiles.db; export for NewPipe.

## Core: extras

- [~] SponsorBlock: categories with skip, announce or off; highlight; whole-video labels.
- [~] Return YouTube Dislike.
- [~] Chapters from descriptions; transcript from captions; lyrics from LRCLIB (timed lines).
- [~] YouTube account: cookies files, from a browser through yt-dlp, from the sign-in window; encrypted at rest
  (DPAPI on Windows, the system keyring elsewhere); SAPISIDHASH; leftover temp files swept.
- [~] Bug reports and feature requests through the relay, log anonymised.
- [~] Run counter with the optional shared total (Abacus).
- [~] Updates: GitHub releases, Ed25519 signatures, notes read aloud, skip a version, installer run.
- [~] Braille progress track and the NVDA add-on (Windows only): install and update, state file with chapter marks.

## The app

Written and compiling, not yet run (see Status). Where it lives in `crates/timeplayer/src`:

- [~] One instance; a second start hands its link to the open TimePlayer, or brings it forward (`instance.rs`:
  wxWidgets' checker, then a per-user named pipe on Windows, a socket in the data folder elsewhere).
- [~] timeplayer:// links and the browser extension's native messaging host (`link_registration.rs`: the
  registry on Windows; a desktop entry with xdg-mime on Linux; host manifests for Chrome, Chromium, Edge and
  Brave on Linux and macOS; macOS takes the scheme from the app bundle).
- [~] Main window (`window.rs`, `keys.rs`): search box (/, Ctrl+F, Ctrl+E), Search for box, Sections strip
  (Video: Ctrl+1 to 8, Ctrl+9 YouTube; Music: Ctrl+1 to 4), Ctrl+M, F6 and Shift+F6, Ctrl+Tab.
- [~] Lists (`sections.rs`, `actions.rs`): first-letter jumps (quiet-list), load more near the end, Enter,
  Ctrl+Q, L, K, P, S, G, C, D, R, Ctrl+Shift+S, Delete and Shift+Delete, Alt+Up and Alt+Down, the Applications
  key menu, Alt+Left, Backspace and Escape back, F5.
- [~] Category, channel Tab and YouTube Page boxes; playlist buttons; Channels Import and Export; Clear history
  and Clear queue.
- [~] Now playing bar: Play, Previous, Next, Open player, Stop; Ctrl+Space, Ctrl+Shift+N and P, media keys, Alt+P.
- [~] Player (`player.rs`): video area (mpv --wid), Back, Actions, Play, Previous, Next, Mute, Position slider,
  braille line, all of 1.x's player keys, the panels (Up next, Comments tree, Description, Queue, Chapters,
  Transcript, Lyrics), the Actions menu with Captions, Speed and Sound submenus.
- [~] Shorts mode, radio, autoplay, resume, SponsorBlock, dislikes, audio description, captions read aloud,
  timed lyrics, chapters, transcript.
- [~] Settings, eight pages (`settings_window.rs`), every change applied and saved at once.
- [~] Report a problem and Suggest a feature (`report.rs`); Google sign-in in a web view with cookies read from
  WebView2, WebKitGTK or WKWebView (`signin.rs`); import progress (`import_progress.rs`); the update offer,
  download and install (`update_flow.rs`); text prompt and Takeout steps (`dialogs.rs`, `actions.rs`).
- [~] F1 and Ctrl+F1 everywhere (`help.rs`, 1.x's texts); "Press F1 for help" at startup; problem panel with
  Retry and Dismiss; the run count and braille add-on questions; subscriptions and update checks 4 seconds in.
- [~] Speech while in front (live region) and in the background (prism) (`speech.rs`).
- [~] NVDA add-on window messages and the braille state file (`nvda.rs`).
- [~] Debug self-check (`selfcheck.rs`), 21 steps through the main window's keys.

Different on purpose:

- Settings keeps the main window disabled while it's open instead of running a modal loop, so a sign-in or
  diagnostics finishing in the background still reaches it.
- The region list is a fixed list of the regions YouTube has charts for (1.x listed every .NET region).
- The links pipe is named `TimePlayer2-Links-<user>`; 1.x and 2.0 can't run side by side anyway.
- Texts: everything goes through 1.x's translations; four new texts have their own Romanian, Ukrainian and
  Spanish (`build/translations/v2_texts.json`). The Takeout steps and the diagnostics placeholder stay English,
  as they were in 1.x. `python build/translations/check.py` lists any text without a translation.
- Not done yet: a checker for duplicate access keys per page and menu.

## Packaging

- [ ] Installer (Inno Setup, four languages) shipping the Rust exe as `TimePlayer_setup.exe`.
- [ ] Portable zip, browser extension zip, NVDA add-on, SHA256SUMS, signatures.
- [ ] Linux AppImage and macOS build.
