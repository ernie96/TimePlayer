# TimePlayer

**TimePlayer** is a native, accessible Windows 11 desktop YouTube player built with C# and WPF on **.NET 10 LTS**.

It is designed from the ground up for assistive technologies (screen readers like NVDA, JAWS, and Windows Narrator) and keyboard-only operation, without requiring Google API credentials, OAuth authentication, user registration, or manual server configuration.

## Download

- **Installer:** [TimePlayer_setup.exe](https://github.com/ernie96/TimePlayer/releases/latest/download/TimePlayer_setup.exe) installs for your user, with no administrator rights needed. It speaks English, Spanish, Romanian and Ukrainian.
- **Portable:** [TimePlayer.zip](https://github.com/ernie96/TimePlayer/releases/latest/download/TimePlayer.zip): extract anywhere you can write to.
- **Browser extension:** [TimePlayer-browser-extension.zip](https://github.com/ernie96/TimePlayer/releases/latest/download/TimePlayer-browser-extension.zip) (also installed with the app; see Settings, Links and browser).

TimePlayer keeps itself up to date: it checks for a new release at startup and offers to install it.
The plain-text user guide is [build/README.txt](build/README.txt), and [CHANGELOG.md](CHANGELOG.md) lists what changed in each version.

---

## Features

- **No account, no keys.** No Google sign-in, API key or cookies. Everything an account would keep (subscriptions,
  likes, playlists, Watch Later, history) is stored as plain JSON on your computer.
- **Built for screen readers and the keyboard.** NVDA, JAWS and Narrator. The interface stays quiet: F1 says in a
  sentence or two where you are and what to press, Control+F1 opens the full help for that place, and captions are
  read aloud only when you ask (`C`).
- **Library**: Search, Featured, What's New (subscription feed), Channels, History with resume, Watch Later, Queue,
  For You (local recommendations) and Playlists (Liked videos, your own and saved YouTube playlists).
- **Player**: Up next with autoplay, comments with threaded replies, description, captions, speed, share links that
  start at the current time, and audio or video downloads.
- **SponsorBlock**: sponsor reads and other marked segments are skipped or announced (`S` skips, `H` jumps to the
  highlight), each category configurable in Settings. **Return YouTube Dislike** adds estimated dislike counts.
- **Sound and music mode**: equalizer, bass, clarity, stereo width, room, crossfeed, compressor and volume leveling,
  with presets (Music, Bass boost, Headphones, Voice and podcasts, Night, Concert hall, Loudness) and a custom setting.
  They change TimePlayer's sound only. Music mode (`Ctrl+M`) turns video off and uses its own sound preset.
- **Braille progress**: an NVDA add-on shows playback position as a "lower g" (⠶) on the braille display; the routing
  keys seek to that point and `Ctrl+Shift+D` toggles it.
- **English, Spanish, Romanian and Ukrainian**: every spoken message, screen, menu and help text, with each language's
  plural forms; it follows the Windows language or Settings, Speech and braille, Language. Thanks to Stacy Carter for
  Ukrainian support.
- **Links from anywhere**: a browser extension, a bookmark, `timeplayer://` links, the command line and Control+Shift+V
  open YouTube and YouTube Music links in TimePlayer.
- **Report a problem** (Control+Shift+B) or **suggest a feature** (Control+Shift+F) from inside the app, with no account.
- **Updates itself** from GitHub Releases, with every download checked against an Ed25519 signature.

## How it works

- **Finding videos and streams** tries several sources in order and moves on when one fails, so a single outage
  doesn't stop playback:
  1. **TubeRepair** (`tuberepair.uptimetrackers.com`), a YouTube-compatible API: search, details and streams.
  2. **Invidious** public instances, picked by health and latency from the public directory (also used for
     channel pages and as a fallback for related videos, comments and playlists).
  3. **InnerTube**, YouTube's own web API, without signing in (also the main source for Up next, comments and playlists).
  4. **yt-dlp**, a local copy that TimePlayer downloads, verifies and keeps updated.
- **Playback** uses **mpv** in its own process, controlled over its JSON IPC pipe and drawn inside TimePlayer's window.
  Playback only counts as started once mpv reports it is playing; otherwise the next source is tried.
- **Subscription feeds** come from YouTube's public per-channel RSS feeds, as NewPipe's fast mode does.
- **Speech**: announcements go through a UI Automation live region; captions use the NVDA Controller Client when NVDA
  runs, so a new caption interrupts an old one instead of queueing behind it.

---

## Documentation

- [User guide](build/README.txt) (plain text, shipped with the app)
- [Changelog](CHANGELOG.md)
- [Accessibility & Screen Reader Checklist](ACCESSIBILITY_CHECKLIST.md)
- [Dependency Management & Verification](docs/DEPENDENCIES.md)
- [Public Instance Directories](docs/INSTANCE_DIRECTORIES.md)
- [Third-Party Notices & Licenses](docs/THIRD_PARTY_NOTICES.md)

---

## Project Structure

```
├── Directory.Build.props            # Version and solution-wide compiler settings
├── Directory.Packages.props         # Central Package Management
├── TimePlayer.sln
├── LICENSE                          # 0BSD
├── CHANGELOG.md                     # Release notes, read out in the update prompt
├── browser-extension/               # "Play in TimePlayer" for Chrome, Edge and Brave
├── build/                           # package.ps1, Inno Setup script and languages, user guide, update signer
├── docs/                            # Dependencies, instance directories, third-party notices
├── report-relay/                    # Cloudflare Worker that files problem reports and suggestions
├── src/
│   ├── TimePlayer.Core/             # net10.0 class library
│   │   ├── Accessibility/           # Spoken text, braille progress track, NVDA add-on and bridge
│   │   ├── Backends/                # TubeRepair, Invidious, InnerTube and yt-dlp providers
│   │   ├── Common/                  # Result<T>, errors, process runner
│   │   ├── Configuration/           # AppConfig, settings, portable/installed paths
│   │   ├── Library/                 # Subscriptions, history, playlists, For You, Takeout import, downloads
│   │   ├── Models/                  # SearchResult, VideoDetails, YouTubeUrl, comments
│   │   ├── Playback/                # PlaybackCoordinator and the mpv engine (JSON IPC)
│   │   └── Updates/                 # GitHub Releases updater and signature checks
│   │
│   └── TimePlayer.Wpf/              # net10.0-windows WPF application
│       ├── Accessibility/           # UI Automation announcements, caption speech
│       ├── ViewModels/              # MainViewModel (library, player, playlists, sharing)
│       └── Views/                   # Main window, settings, F1 and Control+F1 help, report form, update flow
│
└── tests/TimePlayer.Tests/          # xUnit tests
```

---

## Prerequisites

- **Operating System**: Windows 10 (version 1809+) or Windows 11 (x64)
- **.NET SDK**: [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (LTS)

---

## Building and Testing

### Build the entire solution
```powershell
dotnet build
```

### Run automated unit tests
```powershell
dotnet test
```

### Run the application
```powershell
dotnet run --project src/TimePlayer.Wpf
```

---

## Keyboard Navigation

**F1 says where you are and the keys that matter most there**, in a sentence or two (search box, a section's list, the
player, Up next, comments, Settings). **Control+F1** opens the full help for that place in a window.
**F6 / Shift+F6** move between the main areas: search box, section tabs and list; or player controls, side panel and top bar.

### Library

| Key | Action |
|---|---|
| `Ctrl+1` ... `Ctrl+8` | Video: Featured, For You, What's New, Channels, Playlists, Watch Later, History, Queue. Music: Charts, Recently played, Playlists, Queue |
| `/`, `Ctrl+F`, `Ctrl+E` | Search from anywhere; results open as their own page |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous section |
| First letter | Jump to the next title starting with it |
| `Enter` | Play the video, or open the channel |
| `Ctrl+Q` / `Ctrl+L` / `Ctrl+S` / `Ctrl+G` | Queue, Watch Later, subscribe, go to channel |
| `Ctrl+K` / `Ctrl+P` | Like, add to a playlist |
| `Ctrl+C` | Copy a link to share: asks YouTube link or TimePlayer link (Settings, Links and browser, can fix the choice) |
| `Ctrl+D` | Download: audio (MP3) or video (MP4), open the downloads folder, cancel |
| `Delete` | Remove from History, Watch Later, Channels, a playlist or the Queue; in For You, not interested |
| `Shift+Delete` | Don't recommend this channel |
| `Alt+Up` / `Alt+Down` | Move a video in your own playlist |
| `Applications` / `Shift+F10` | Every action for the item |
| `Alt+Left`, `Backspace`, `Escape` | Back from search results, a channel or a playlist page |
| `F5` | Refresh the section |
| `F2` | Settings: Playback, Sound, Speech and braille, Library, SponsorBlock and dislikes, Links and browser, Updates and help (`Ctrl+Tab` between pages; Escape closes and saves) |
| `Ctrl+Shift+V` | Play a YouTube link from the clipboard |
| `Ctrl+Shift+B` / `Ctrl+Shift+F` | Report a problem / suggest a feature |

Lists keep loading as you reach the end: search results, channel pages, playlists and comments are not limited to the first 20.

### Player

| Key | Action |
|---|---|
| `K`, or `Space` when not on a button | Play / pause (after the end, plays again from the start) |
| `Left` / `Right`, `Ctrl+Left` / `Ctrl+Right` | Seek 5 / 30 seconds |
| `0` ... `9`, `Home` | Jump to 0-90%, start over |
| `Up` / `Down`, `M` | Volume, mute |
| `Shift+.` / `Shift+,` | Faster / slower |
| `T`, `I`, `U` | Speak the time, what is playing, what plays next |
| `Shift+N` / `Shift+P` | Next (queue, then Up next) / previous |
| `Alt+U`, `Alt+C`, `Alt+D`, `Alt+Q` | Up next, Comments, Description, Queue panels |
| `A` | Autoplay on/off (plays the first unwatched related video when the queue ends) |
| `S` / `H` | Skip the SponsorBlock segment playing now / jump to the highlight |
| `E` / `Shift+E` | Next / previous sound preset |
| `Ctrl+M` | Switch between Video and Music (from anywhere) |
| `Ctrl+Space`, `Ctrl+Shift+N` / `Ctrl+Shift+P`, media keys | Play/pause, next/previous from anywhere while something plays |
| `Alt+P` | Music: open the player (Escape returns to the lists; the music keeps playing) |
| `C` | Read captions aloud on/off (off by default) |
| `Ctrl+S` / `Ctrl+L` / `Ctrl+G` | Subscribe, Watch Later, go to channel |
| `Ctrl+K` / `Ctrl+P` | Like, add to a playlist |
| `Ctrl+C` / `Ctrl+Shift+C` | Copy a share link that starts at the current time / from the beginning |
| `Ctrl+D` | Download audio or video of what is playing |
| `Escape` | From a side panel: back to the controls; otherwise close the player |
| `Ctrl+Shift+D` | Braille progress on/off |
| Braille routing keys | Seek silently to that point |

Comments are a tree: `Right` opens a comment's replies (one level deeper, each starting with "Reply from"), `Right` again moves into them, `Left` goes back up and closes. `Enter` also opens or closes; "More replies" loads the rest. `Ctrl+C` copies a comment.

Downloads go to `Downloads\TimePlayer`. With ffmpeg installed (for example `winget install Gyan.FFmpeg`), audio is saved as MP3 and video at full quality; without it, audio is saved as M4A and video as a single MP4.

### Braille

On first start with NVDA installed, TimePlayer asks once whether you use a braille display. Yes installs the NVDA add-on (restart NVDA once); No turns braille progress off and is never asked again. Change it later in Settings.

### What is spoken

TimePlayer speaks results of what you do (a count of results, "Paused", a new position after seeking) and stays quiet
otherwise: buffering, loading related videos and comments in the background, and caption lines are not announced
unless you turn on "Read captions aloud" (`C`). At startup it only says "Press F1 for help". An empty section says
what it is ("Watch Later is empty."); how to fill it is in F1.

## Library, Subscriptions and Feeds

TimePlayer keeps an account-free library on your computer, in the spirit of NewPipe, stored as plain JSON
in `%LOCALAPPDATA%\TimePlayer\library` (or a `library` folder next to `TimePlayer.exe` for portable installs):

- `subscriptions.json` is written in **NewPipe's export format**, so it can be imported directly into NewPipe, Tubular or LibreTube.
- **Import** (Channels section): the Google Takeout zip (no need to unzip), NewPipe / Tubular / LibreTube JSON, Takeout `subscriptions.csv`, OPML, FreeTube `profiles.db`. The "How to get my YouTube subscriptions" button walks through Google Takeout step by step and opens it.
- **Google Takeout** (Playlists section): one zip brings subscriptions, playlists, liked videos, Watch later and watch history (`history/watch-history.json`, when JSON is chosen in Takeout). Takeout lists only video ids; titles are looked up through YouTube's public oEmbed endpoint.
- **For You**: recommendations built locally. Recent history, likes and subscription uploads are seeds; each seed's "Up next" list is pooled and scored by how often and how high a video appears, subscribed and familiar channels get a boost, and watched, liked, Not interested and blocked videos are left out, with at most two per channel. Cached in `recommendations.json` for three hours; new users get trending videos.
- **Playlists** (`playlists.json`): Liked videos, your own playlists (rename, reorder with Alt+Up/Down, Play all, Shuffle) and saved YouTube playlists (refresh with F5). YouTube playlists open from a pasted link or from playlist search (InnerTube `browse VL<id>`, Invidious fallback). Your own playlists share as a `watch_videos` link.
- **Not interested** (`hidden.json`): hidden videos and channels you don't want recommended.
- **Export**: NewPipe JSON or OPML.
- **What's New**: latest uploads of every subscribed channel from YouTube's public RSS feeds, newest first; uploads since your
  last visit are spoken as "New", Shorts are marked and can be hidden, and new uploads are announced at startup.
- **Featured**, for a configurable region. YouTube retired its Trending page in 2025, so each category reads a source
  YouTube still keeps current: Trending mixes YouTube's Music, Sports, News and Gaming pages; Music is the YouTube Charts
  Top 100 music videos; Gaming is this week's gaming videos; Movies is this week's most watched trailers; News, Sports
  and Live are YouTube's own pages for them. Music, Gaming and Movies page in as you reach the end.
- **History** with **resume**: videos continue where you left off ("Resuming at 4 minutes 12 seconds. Press Home to start from the beginning"),
  and lists say "Watched" or "Resume at ..." before the title.
- **Watch Later** list and **channel pages** reachable from any video.

## Sound and music mode

Effects run inside TimePlayer's own player as an mpv `af` chain of FFmpeg filters (`equalizer`, `bass`, `aexciter`,
`extrastereo`, `bs2b`, `aecho`, `acompressor`, `dynaudnorm`, ending in `alimiter` whenever anything boosts), so other apps
and Windows are untouched; nothing is installed into Windows' audio system. Settings, Sound has a preset for videos, a
preset for music mode, and a custom sound (10-band equalizer from 31 Hz to 16 kHz, -12 to +12 dB). Changes apply within a
quarter of a second. Music mode sets mpv's `vid=no`, so video isn't decoded; whether less data is downloaded depends on the
stream source.

## Opening links from other apps

TimePlayer can play YouTube links that come from your browser, email, chat or any other app. Only one TimePlayer runs at a time: a link from elsewhere goes to the open window.

- **Copied links:** copy a YouTube link anywhere, then press Control+Shift+V in TimePlayer.
- **The browser extension** (Chrome, Edge, Brave and other Chromium browsers) adds "Play in TimePlayer" after each YouTube link on other websites (Reddit, webmail, forums), or can make those links open in TimePlayer directly. On YouTube itself, use its toolbar button, Alt+Shift+P, or "Play this page in TimePlayer" in the context menu. Settings, Links and browser, has the steps: copy the extension folder's location, open `chrome://extensions` (or `edge://extensions`), turn on Developer mode, choose "Load unpacked" and paste the location. It's also `TimePlayer-browser-extension.zip` on the releases page.
- **A bookmark** does the same without an extension: Settings, Links and browser, "Copy the browser bookmark", then add a bookmark with that as its address. Choose it on a YouTube page.
- **`timeplayer://` links:** TimePlayer registers itself for links like `timeplayer://open?url=https%3A%2F%2Fyoutu.be%2FdQw4w9WgXcQ`, which any web page, document or app can use. The first time, the browser asks whether to open TimePlayer. If links stop opening (for example after moving a portable copy), Settings, Links and browser, "Register TimePlayer for links again" fixes it.
- **The command line:** `TimePlayer.exe "https://youtu.be/dQw4w9WgXcQ"`, which also covers shortcuts and scripts.

- **Sharing:** Control+C and Control+Shift+C ask whether to copy a YouTube link (opens anywhere) or a TimePlayer link (opens straight in TimePlayer for people who have it). "Control+C shares" in Settings, Links and browser, can always use one instead.

YouTube Music links (music.youtube.com) open in Music. Only YouTube videos and playlists are accepted; anything else in a link is ignored. "Let browsers and other apps open YouTube links in TimePlayer" in Settings turns the `timeplayer://` links off.

Windows doesn't let desktop apps take over ordinary youtube.com links, so a YouTube link clicked in an email still opens the browser. The extension or Control+Shift+V cover that.

## SponsorBlock and Return YouTube Dislike

- **SponsorBlock** ([sponsor.ajay.app](https://sponsor.ajay.app)): segments are fetched through the hash-prefix endpoint, so
  only the first four hex digits of the SHA-256 of the video id are sent. Each category (sponsor, self-promotion, reminders to
  like or subscribe, intro, outro, preview, filler, non-music) can be skipped automatically, announced (`S` skips it) or ignored;
  the highlight and whole-video labels can be announced. A skipped segment plays if you seek back into it. Skipping waits for the
  resume jump, so it never undoes "continue where I left off".
- **Return YouTube Dislike** ([returnyoutubedislike.com](https://returnyoutubedislike.com)): likes and estimated dislikes appear at
  the top of the description and in the `I` summary.

---

## Translating TimePlayer

TimePlayer speaks English, Romanian, Ukrainian and Spanish. Text is written in English in the code and looked up through
`Loc.T("...")` (and `Loc.Plural(n, "{0} video", "{0} videos")` for counts); XAML uses `{l:T 'text'}`. The translations live in
`src/TimePlayer.Core/Localization/Romanian.Strings.cs`, `Ukrainian.Strings.cs` and `Spanish.Strings.cs`, one line per text,
keyed by the English. Plural entries hold the language's forms separated by `|` (Romanian one|few|"de" form, Ukrainian
one|few|many, Spanish one|other). Anything without a translation stays in English. The test
`EveryTextInTheCode_HasBothTranslations` fails when new English text has no translation in every language, and
`EveryTranslation_KeepsItsPlaceholders_AndFormats` catches a broken `{0}`.

## Reporting problems

Control+Shift+B (or Settings, Updates and help, Report a problem) opens a short form: what went wrong, an optional
email or name for an answer, and optionally TimePlayer's log, with the Windows user name taken out. Reports go to
a small relay (`report-relay/`, a Cloudflare Worker), which files each as an issue in the private repository
ernie96/TimePlayer-reports. Control+Shift+F (Suggest a feature) uses the same form without the log; suggestions are
labelled "feature request". See `report-relay/README.md` for setup.

## Making a release

1. Set `<Version>` in `Directory.Build.props` and add a `## <version>` section to `CHANGELOG.md`. That section becomes the release notes users hear in the update prompt.
2. Commit, then tag and push: `git tag v1.2.0` and `git push origin main v1.2.0`.
3. The **Release** workflow tests, builds, signs and publishes `TimePlayer_setup.exe` and `TimePlayer.zip` (each with a `.sig`), the NVDA add-on, the browser extension zip and `SHA256SUMS.txt`. Installed copies offer the update at their next start.

Updates are signed with Ed25519. The private key is the `UPDATE_SIGNING_KEY` repository secret, with a copy kept outside the repository at `%APPDATA%\TimePlayer-release\update-signing.key`; its public half is `UpdateService.PublicKey`. The app refuses any update that does not verify, so keep that key safe: losing it means shipping a new public key in a release that users install by hand.

To build locally instead, `pwsh build/package.ps1` writes the same files to `release/`. It bundles mpv and yt-dlp from `%LOCALAPPDATA%\TimePlayer\bin` (or `-ToolsFrom <folder>`) and needs Inno Setup 6 for the installer (`winget install JRSoftware.InnoSetup`). Romanian isn't one of Inno Setup's own languages, so its translation is kept in `build/Languages`. `build/README.txt` is the plain-text guide shipped to users.

---

## License and dedication

TimePlayer is released under the [BSD Zero Clause License (0BSD)](LICENSE): use, change and share it for any purpose.
The programs and libraries it uses keep their own licenses; see the
[Third-Party Notices](docs/THIRD_PARTY_NOTICES.md) for the components used in this program.

TimePlayer, like all my apps, is dedicated to Jenni and Diana. The full dedication is at the top of the
[Third-Party Notices](docs/THIRD_PARTY_NOTICES.md).
