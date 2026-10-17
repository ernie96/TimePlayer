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
  - **Local Fallback**: Standalone `yt-dlp` extraction when public instances are degraded or blocked.
  - **Instance Directory Caching**: Caches last known good instances locally to prevent outages if directory endpoints are temporarily down.
- **Embedded Video Playback Engine**:
  - High-performance `mpv` engine embedded inside the WPF window using Win32 `HwndHost` (`--wid=<HWND>`).
  - Controlled strictly via local Windows Named Pipe JSON IPC (`\\.\pipe\timeplayer-mpv-*`).
- **First-Class Accessibility**:
  - Native WPF controls and UI Automation integration throughout.
  - **Live Region Announcements**: Status updates, search counts, relative seek operations, volume, and playback state changes are announced directly to screen readers using UI Automation live regions (inspired by the trypsynth `live-region` pattern).
  - **Canonical Key Chord Engine**: Standardized keyboard chord parsing and screen reader spoken text (inspired by the trypsynth `key-chord` pattern).
  - **Visible Focus**: High-contrast, custom dashed focus visuals for low-vision and keyboard navigation.
  - **Natural Spoken Text**: Converts durations and metrics into human speech (e.g., "3 minutes, 12 seconds", "1.5 million views") rather than robotic shorthand.
  - **Actionable, Friendly Errors**: Prevents app crashes with centralized dispatcher error handling and reports friendly guidance instead of raw stack traces.

---

## Project Structure

```
├── Directory.Build.props            # Solution-wide compiler & versioning settings
├── Directory.Packages.props         # Central Package Management (CPM)
├── TimePlayer.sln                   # Visual Studio / .NET Solution
├── ACCESSIBILITY_CHECKLIST.md       # Screen reader & keyboard accessibility checklist
├── src/
│   ├── TimePlayer.Core/             # Target: net10.0 (Class Library)
│   │   ├── Accessibility/           # IAccessibilityService, KeyChord, AccessibleTextFormatter
│   │   ├── Backends/                # IYouTubeService, IYouTubeBackendProvider, IInstanceCache
│   │   ├── Configuration/           # AppConfig, IConfigurationService, settings validation
│   │   ├── Models/                  # SearchResult, VideoMetadata, StreamInfo, InstanceInfo, Enums
│   │   ├── Playback/                # IPlayerEngine, PlaybackEvents, PlayerOptions
│   │   └── Services/                # Core DI service registration extensions
│   │
│   └── TimePlayer.Wpf/              # Target: net10.0-windows (WPF Application)
│       ├── Accessibility/           # WpfAccessibilityService (UIA live region updates)
│       ├── Configuration/           # appsettings.json
│       ├── ViewModels/              # MainViewModel (CommunityToolkit.Mvvm)
│       ├── Views/                   # MainWindow.xaml & MainWindow.xaml.cs
│       ├── App.xaml                 # Application styling and resources
│       └── App.xaml.cs              # DI container, centralized logging & exception handling
│
└── tests/
    └── TimePlayer.Tests/            # Target: net10.0 (xUnit Test Suite)
        ├── Accessibility/           # KeyChordTests, AccessibleTextFormatterTests
        ├── Configuration/           # ConfigurationTests
        ├── Logging/                 # LoggingAndErrorTests
        └── Models/                  # SearchResultTests, InstanceInfoTests
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
| `Tab` / `Shift+Tab` | Navigate between controls (Search, Results, Transport, Sliders) |
| `Enter` (in search) | Execute search or load direct URL |
| `Up` / `Down` | Move through search results |
| `Space` | Toggle Play / Pause |
| `Left` / `Right` | Seek backward / forward by 5 seconds |
| `+` / `-` | Increase / decrease volume by 5% |
| `>` / `<` | Increase / decrease playback speed by 0.25x |
| `F1` | Show keyboard shortcuts help |
