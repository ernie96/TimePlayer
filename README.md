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
    - The display line remains blank except for a "lower g" indicator (dots 2-3-5-6, Unicode `\u2836` `⠶`).
    - The indicator moves evenly along the display as playback progresses (e.g. on a 32-cell display halfway is cell 16; on an 18-cell display halfway is cell 9).
    - Increments adjust dynamically based on video duration (a 5-minute track advances slower than a 3-minute track).
    - Touch cursor / routing buttons on the Braille display seek directly to that exact track position in either direction.
    - When paused, the lower g blinks slowly (600ms interval) at the stopped cell.
    - Direct NVDA Braille bridge (`nvdaControllerClient64.dll`) and accessible onscreen monospace WPF control.
  - **Screen Reader Live Subtitles**: Displays captions and raises UI Automation live region announcements (`AutomationProperties.LiveSetting="Polite"`) so screen reader users hear captions in real time as subtitles move.
  - **Transport & Audio Controls**: Play/Pause, Previous/Next track, Stop, Seek (-5s, +5s), Time/Duration labels, Volume slider & mute toggle, playback speed selection (0.5x–2.0x), and subtitle track selector.
  - **Playback Queue**: Enqueue results, play next/previous, remove items (`Delete`), and clear queue. Videos in queue auto-advance when a track ends.
  - **Resilient Error Banner & Retry**: User-friendly notification banner with one-click `Retry` action for transient network/instance issues, keeping technical diagnostics separated.
  - **Settings & Diagnostics**: Accessible dialog (`F2`) with quality preference selection, hardware decoding toggle, Braille display cell count configuration (14, 18, 20, 32, 40, 80 cells), yt-dlp/mpv dependency verification and update checks, and a one-click "Run Diagnostics" test suite.

---

## Documentation

- [Accessible YouTube Player Architecture](file:///C:/Users/eblin/.gemini/antigravity/brain/ebd48c66-0893-4019-910f-96c1097ee4c6/accessible_youtube_player_architecture.md)
- [Accessibility Checklist](ACCESSIBILITY_CHECKLIST.md)
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
 
| Key / Shortcut | Action |
|---|---|
| `Ctrl+E` or `Ctrl+F` | Focus search field and select all text |
| `Enter` (in search) | Execute search or load direct YouTube URL |
| `Enter` (in results) | Open selected video in the player dialog |
| `Enter` (in queue) | Immediately play selected video from the queue |
| `Delete` (in queue) | Remove selected video from the queue |
| `Escape` | Close Settings if open, or close Video Player Dialog and return to search results |
| `Space` or `K` | Toggle Play / Pause |
| `Left` / `Right` | Seek backward / forward by 5 seconds |
| `Up` / `Down` | Increase / decrease volume by 5% |
| `M` | Toggle audio mute |
| `F2` | Open / close Settings and Diagnostics dialog |
| `Touch Cursor Routing` | On Braille display, pressing a routing button skips to that exact point in the track |
| `Tab` / `Shift+Tab` | Navigate logically through interactive controls |

