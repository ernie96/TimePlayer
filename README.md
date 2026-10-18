# TimePlayer

**TimePlayer** is a native, accessible Windows 11 desktop YouTube player built with C# and WPF on **.NET 10 LTS**.

It is designed from the ground up for assistive technologies (screen readers like NVDA, JAWS, and Windows Narrator) and keyboard-only operation, without requiring Google API credentials, OAuth authentication, user registration, or manual server configuration.

---

## Key Features & Architecture

- **Zero Configuration & Privacy**:
  - No Google API keys, account registration, or cookies required.
  - No credentials or authorization headers are forwarded to external servers.
- **Triple-Tier Resilient Backend Architecture**:
  - **Primary**: Piped API public instance discovery with automatic failover.
  - **Secondary**: Invidious API public instance directory with health and latency scoring.
  - **Local Fallback**: Standalone application-managed `yt-dlp` binary with verified SHA256 integrity, automatic startup updates, safe process tree termination, and download progress reporting.
  - **Instance Directory Caching**: Caches last known good instances locally to prevent outages if directory endpoints are temporarily down.
- **Resilient Search-to-Playback Workflow Orchestration**:
  - **Search Workflow**: Bounded search queries try healthiest Piped instance first, fail over to alternative Piped servers, fall back to Invidious, and seamlessly degrade to local `yt-dlp` search without freezing the UI or reporting premature failure.
  - **Verified Playback Startup**: Confirms that mpv actually transitions to `Playing` or `Buffering` before declaring playback success. If a resolved stream returns HTTP 403 or decoding fails, it automatically falls back to Invidious, yt-dlp extracted streams, and ultimately direct yt-dlp hook playback.
  - **Safe Stream Selection**: Protects against silent video playback by strictly requiring companion audio streams whenever DASH video-only formats are chosen, falling back to progressive formats if audio tracks are unavailable.
  - **Cancellation Support**: Full `CancellationTokenSource` integration allows users to cancel searches and media loading at any time, restoring focus smoothly.
- **Accessible User Interface & Modal Dialog Player**:
  - **Catalog Mode**: Search box with paste & play, search results list with accessible summaries, and queue preview.
  - **Dialog Player Mode**: When a video opens, a focused dialog-type player opens. Pressing `Escape` at any time exits the player and returns focus predictably to the search results.
  - **Connected Braille Display Progress Indicator**:
    - Refreshable Braille displays and screen readers act as a physical track progress indicator.
    - Supports **14, 18, 20, 32, 40, and 80 cell** displays (e.g. halfway on a 32-cell is cell 16, 20-cell is cell 10, 18-cell is cell 9).
    - The display line remains blank except for a "lower g" indicator (dots 2-3-5-6, Unicode `\u2836` `⠶`).
    - Increments adjust dynamically based on video duration (a 5-minute track advances slower than a 3-minute track).
    - Touch cursor / routing buttons on the Braille display seek directly to that exact track position in either direction.
    - When paused, the lower g blinks slowly (600ms interval) at the stopped cell.
    - **Instant Braille Toggle (`Ctrl+Shift+D`)**: Lets users instantly toggle between the track progress bar and standard screen reader text/subtitles on their Braille display.
    - Direct NVDA Braille bridge (`nvdaControllerClient64.dll`) and accessible onscreen monospace WPF control.
  - **Screen Reader Live Subtitles**: Displays captions and raises UI Automation live region announcements (`AutomationProperties.LiveSetting="Polite"`) so screen reader users hear captions in real time as subtitles move.
  - **Transport & Audio Controls**: Play/Pause, Previous/Next track, Stop, Seek (-5s, +5s), Time/Duration labels, Volume slider & mute toggle, playback speed selection (0.5x–2.0x), and subtitle track selector.
  - **Playback Queue**: Enqueue results, play next/previous, remove items (`Delete`), and clear queue. Videos in queue auto-advance when a track ends.
  - **Resilient Error Banner & Retry**: User-friendly notification banner with one-click `Retry` action for transient network/instance issues, keeping technical diagnostics separated.
  - **Settings & Diagnostics**: Accessible dialog (`F2`) with quality preference selection, hardware decoding toggle, Braille display cell count configuration (14, 18, 20, 32, 40, 80 cells), yt-dlp/mpv dependency verification and update checks, and a one-click "Run Diagnostics" test suite.

---

## Documentation

- [Accessible YouTube Player Architecture](file:///C:/Users/eblin/.gemini/antigravity/brain/ebd48c66-0893-4019-910f-96c1097ee4c6/accessible_youtube_player_architecture.md)
- [Accessibility & Screen Reader Checklist](ACCESSIBILITY_CHECKLIST.md)
- [Dependency Management & Verification](docs/DEPENDENCIES.md)
- [Public Instance Directories](docs/INSTANCE_DIRECTORIES.md)
- [Third-Party Notices & Licenses](docs/THIRD_PARTY_NOTICES.md)

---

## Project Structure

```
├── Directory.Build.props            # Solution-wide compiler & versioning settings
├── Directory.Packages.props         # Central Package Management (CPM)
├── TimePlayer.sln                   # Visual Studio / .NET Solution
├── ACCESSIBILITY_CHECKLIST.md       # Screen reader & keyboard accessibility checklist
├── docs/
│   ├── INSTANCE_DIRECTORIES.md      # Piped & Invidious directory specifications
│   └── THIRD_PARTY_NOTICES.md       # yt-dlp, mpv, and library licenses
├── src/
│   ├── TimePlayer.Core/             # Target: net10.0 (Class Library)
│   │   ├── Accessibility/           # IAccessibilityService, KeyChord, AccessibleTextFormatter
│   │   ├── Backends/                # Piped, Invidious, and YtDlp backend providers
│   │   │   ├── Piped/               # Piped API catalog & stream provider
│   │   │   ├── Invidious/           # Invidious API catalog & stream provider
│   │   │   └── YtDlp/               # Local managed yt-dlp binary, download, and resolver
│   │   ├── Common/                  # Result<T>, AppError, IProcessRunner, DefaultProcessRunner
│   │   ├── Configuration/           # AppConfig, IConfigurationService, settings validation
│   │   ├── Contracts/               # IVideoCatalogProvider, IStreamResolver, IPublicInstanceDirectory
│   │   ├── Models/                  # VideoDetails, SearchResult, StreamCandidate, Enums
│   │   └── Services/                # Core DI service registration extensions
│   │
│   └── TimePlayer.Wpf/              # Target: net10.0-windows (WPF Application)
│       ├── Accessibility/           # WpfAccessibilityService (UIA live region updates)
│       ├── Configuration/           # appsettings.json
│       ├── ViewModels/              # MainViewModel (CommunityToolkit.Mvvm)
│       ├── Views/                   # MainWindow.xaml & MainWindow.xaml.cs
│       ├── App.xaml                 # Application styling and resources
│       └── App.xaml.cs              # DI container, startup yt-dlp update, logging & error handling
│
└── tests/
    └── TimePlayer.Tests/            # Target: net10.0 (xUnit Test Suite)
        ├── Accessibility/           # KeyChordTests, AccessibleTextFormatterTests
        ├── Backends/                # Piped, Invidious, YtDlp, ErrorClassifier, DownloadProgress tests
        ├── Common/                  # ProcessRunnerTests, ResultTests, InstanceUrlValidatorTests
        └── Models/                  # Model conversion and validation tests
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

**F1 explains the keys for wherever focus is** (search box, a section's list, the player, Up next, comments, settings).
**F6 / Shift+F6** move between the main areas: search box, section tabs and list; or player controls, side panel and top bar.

### Library

| Key | Action |
|---|---|
| `Ctrl+1` ... `Ctrl+7` | Search, Featured, What's New, Channels, History, Watch Later, Queue |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous section |
| `Ctrl+E` | Search box (`Escape` there clears it) |
| `Enter` | Play the video, or open the channel |
| `Ctrl+Q` / `Ctrl+L` / `Ctrl+S` / `Ctrl+G` | Queue, Watch Later, subscribe, go to channel |
| `Ctrl+C` | Copy a share link (`https://youtu.be/...`) |
| `Ctrl+D` | Download: audio (MP3) or video (MP4), open the downloads folder, cancel |
| `Delete` | Remove from History, Watch Later, Channels or the Queue |
| `Applications` / `Shift+F10` | Every action for the item |
| `Alt+Left`, `Backspace`, `Escape` | Back from a channel page |
| `F5` | Refresh the section |
| `F2` | Settings (a normal dialog; Escape closes and saves) |

Lists keep loading as you reach the end: search results, channel pages and comments are not limited to the first 20.

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
| `C` | Read captions aloud on/off (off by default) |
| `Ctrl+S` / `Ctrl+L` / `Ctrl+G` | Subscribe, Watch Later, go to channel |
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
unless you turn on "Read captions aloud" (`C`).

## Library, Subscriptions and Feeds

TimePlayer keeps an account-free library on your computer, in the spirit of NewPipe, stored as plain JSON
in `%LOCALAPPDATA%\TimePlayer\library` (or a `library` folder next to `TimePlayer.exe` for portable installs):

- `subscriptions.json` is written in **NewPipe's export format**, so it can be imported directly into NewPipe, Tubular or LibreTube.
- **Import** (Channels section): the Google Takeout zip (no need to unzip), NewPipe / Tubular / LibreTube JSON, Takeout `subscriptions.csv`, OPML, FreeTube `profiles.db`. The "How to get my YouTube subscriptions" button walks through Google Takeout step by step and opens it.
- **Export**: NewPipe JSON or OPML.
- **What's New**: latest uploads of every subscribed channel from YouTube's public RSS feeds, newest first; uploads since your
  last visit are spoken as "New", Shorts are marked and can be hidden, and new uploads are announced at startup.
- **Featured**: trending videos (with Music, Gaming and Movies categories) for a configurable region, via Invidious.
- **History** with **resume**: videos continue where you left off ("Resuming at 4 minutes 12 seconds. Press Home to start from the beginning"),
  and lists say "Watched" or "Resume at ..." before the title.
- **Watch Later** list and **channel pages** reachable from any video.

---

## Making a release

```powershell
pwsh build/package.ps1
```

This runs the tests, publishes a self-contained single-file build, and writes to `release/`:

- `TimePlayer-<version>-win-x64-portable.zip`: extract and run; `portable.txt` keeps settings, library and logs in the folder.
- `TimePlayer-<version>-Setup.exe`: per-user installer (no administrator prompt), built only when Inno Setup 6 is installed (`winget install JRSoftware.InnoSetup`).
- `TimePlayerBraille.nvda-addon`: the NVDA braille add-on for manual installation.
- `SHA256SUMS.txt`.

mpv and yt-dlp are bundled from `%LOCALAPPDATA%\TimePlayerin` (where TimePlayer downloads them); pass `-ToolsFrom <folder>` to use others.
The version comes from `Directory.Build.props`. `build/README.txt` is the plain-text guide shipped to users.
