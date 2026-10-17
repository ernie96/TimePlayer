# TimePlayer - Comprehensive Accessibility & Screen Reader Audit Checklist

This checklist provides a structured audit covering automated verifications, live integration checks, and manual screen reader testing procedures specifically tailored for **NVDA (NonVisual Desktop Access)**, **JAWS for Windows**, and **Windows Narrator**, as well as **Refreshable Braille Displays**.

---

## 1. Verified Automated QA Tests (Executed & Passing)

The following tests are part of the automated regression suite (`dotnet test`, 218 passing unit/integration tests):

- [x] **Instance Discovery & Failover**: Automatic health scoring, latency measurement, and seamless failover from Piped to Invidious.
- [x] **Multi-Tier Search Fallback**: Bounded queries gracefully try Piped $\rightarrow$ Invidious $\rightarrow$ local `yt-dlp` search without UI thread blockage.
- [x] **Safe Media Stream Selection**: Enforces companion audio streams for DASH video-only streams; prevents silent video playback.
- [x] **Malformed Input & URL Validation**: Validates YouTube URLs, Shorts, Live, youtu.be, timestamps, and rejects playlists/channels with friendly error messages.
- [x] **Process Termination & Cancellation**: Clean job object process tree killing for `yt-dlp` and `mpv`; cancellation token support for all long-running tasks.
- [x] **Braille Mathematical Model**:
  - Cell calculations verified for 18-cell, 20-cell, 32-cell, 40-cell, and 80-cell displays.
  - 32-cell display halfway position verified at cell 16.
  - 18-cell display halfway position verified at cell 9.
  - 20-cell display halfway position verified at cell 10.
  - Dynamic scaling: longer tracks advance slower per second than shorter tracks.
  - Touch cursor routing calculations verified in both directions.
  - Pause blink state verified (600ms on/off phase).
- [x] **Keyboard & State Synchronization**: Seeking, volume (5% steps), speed (0.25x steps), mute, and queue modifications synchronize without screen reader position flooding.
- [x] **Settings Persistence**: User preferences (Quality, Hardware Decoding, Braille Cell Count, Enable Braille) persist cleanly.

---

## 2. Live Integration Tests Performed (Local Machine)

The following integration checks were physically verified against the published x64 self-contained release:

- [x] **Clean Launch**: Standalone executable (`TimePlayer.Wpf.exe`) launched from a clean portable directory (`dist/TimePlayer-portable-win-x64`) without requiring an installed .NET runtime (PID 15208, responding: True).
- [x] **Dependency Resolution**: App successfully located application-relative `bin/yt-dlp.exe` and `bin/mpv/mpv.exe` without downloading duplicates.
- [x] **Live yt-dlp Execution**: Executed `ytsearch1:Rick Astley Never Gonna Give You Up` with structured JSON output parsing (`id: dQw4w9WgXcQ`, duration: 213s).
- [x] **Live mpv Engine**: Verified embedded mpv process execution (v0.41.0-1200-gdde067107 with FFmpeg 63.20.100).
- [x] **Network Resilience**: When public Piped/Invidious servers returned HTTP 526/timeouts, app detected failure and switched to backup provider without crashing.

---

## 3. NVDA Manual Testing Checklist (Requires Human Tester with NVDA)

### Launch & Window Focus
- [ ] Start NVDA (`Ctrl + Alt + N`).
- [ ] Launch `TimePlayer.Wpf.exe`.
- [ ] **Verification**: NVDA announces window title `"TimePlayer - Accessible YouTube Player"` and immediately announces focus in the search box: `"Search YouTube or paste video link, edit, Enter search keywords..."`.

### Search & Navigation
- [ ] Type search keywords (e.g. `classical piano`) and press `Enter`.
- [ ] **Verification**: NVDA announces polite status: `"Searching YouTube for classical piano..."`.
- [ ] On completion, NVDA announces: `"X search results for classical piano. Use Up and Down arrow keys to review results."`
- [ ] Press `Down Arrow` through results.
- [ ] **Verification**: Each item reads title, creator channel, duration (formatted naturally, e.g. `"3 minutes, 15 seconds"`), and view count.

### Player Dialog Activation & Escape Key
- [ ] Press `Enter` on any search result to open the video.
- [ ] **Verification**: Video player dialog opens; focus shifts to `< Back to Search Results (Esc)`. NVDA announces `"Playing video: [Title]"` followed by polite state change `"Playing"`.
- [ ] Press `Escape`.
- [ ] **Verification**: Player dialog closes and focus immediately returns to the search results list on the exact item previously selected.

### Shortcuts During Playback
- [ ] Re-open video (`Enter`).
- [ ] Press `Space` or `K`: NVDA announces `"Paused"`.
- [ ] Press `Space` or `K`: NVDA announces `"Playing"`.
- [ ] Press `Left Arrow` / `Right Arrow`: NVDA announces `"Seeked backward 5 seconds to [Time]"` / `"Seeked forward 5 seconds to [Time]"`.
- [ ] Press `Up Arrow` / `Down Arrow`: NVDA announces `"Volume 85 percent"` / `"Volume 80 percent"`.
- [ ] Press `M`: NVDA announces `"Audio muted"` / `"Audio unmuted"`.

### Live Subtitles & Captions
- [ ] Select a video with captions and choose an English subtitle track in the Captions combo box.
- [ ] **Verification**: NVDA speaks live subtitle text as it appears on screen without interrupting key navigation.

---

## 4. JAWS Manual Testing Checklist (Requires Human Tester with JAWS)

### Virtual PC Cursor & Forms Mode
- [ ] Start JAWS (`Insert + J`).
- [ ] Launch TimePlayer.
- [ ] **Verification**: JAWS enters forms mode automatically in the search input box and speaks the accessible name and prompt.
- [ ] Press `Tab` through controls:
  - Search Box (`TextBox`)
  - Search Button (`Button`)
  - Paste & Play Button (`Button`)
  - Clear Search Button (`Button`)
  - Settings (F2) Button (`Button`)
  - Search Results (`ListBox`)
  - Playback Queue (`ListBox`)
- [ ] **Verification**: JAWS reads control type and label clearly for each element without reading hidden or off-screen controls.

### Queue Management
- [ ] Tab to a search result, press `Tab` to `+ Queue` button, and press `Space` to enqueue.
- [ ] Tab to the Playback Queue list.
- [ ] Press `Enter` on a queue item: Video immediately starts playback.
- [ ] Press `Delete` on a queue item: Item is removed from queue and JAWS announces updated queue count.

### Settings & Diagnostics Dialog (`F2`)
- [ ] Press `F2`.
- [ ] **Verification**: Settings modal opens; JAWS announces `"Settings and Diagnostics Dialog"`.
- [ ] Tab through Preferred Quality combo box, Hardware Decoding check box, Braille Cell Count combo box, and "Check for Updates" button.
- [ ] Press `Escape`: Settings dialog closes and focus returns to the previous control.

---

## 5. Refreshable Braille Display Verification Checklist

Applicable to **14-cell**, **18-cell**, **20-cell**, **32-cell**, **40-cell**, and **80-cell** Braille displays (Freedom Scientific Focus, HumanWare Brailliant, Orbit Reader, Alva, etc.):

- [ ] Connect refreshable Braille display via USB or Bluetooth.
- [ ] **Progress Line Format**: When playing, the Braille line displays all blank cells except for a single **lower g** indicator (dots 2-3-5-6, Unicode `\u2836` `⠶`).
- [ ] **Midpoint Verification**:
  - On an **18-cell display**, indicator is on **cell 9** at 50% track duration.
  - On a **20-cell display**, indicator is on **cell 10** at 50% track duration.
  - On a **32-cell display**, indicator is on **cell 16** at 50% track duration.
- [ ] **Pause Blinking**: Press `Space` to pause playback.
  - Verify the lower g blinks slowly (~600ms on/off) at the stopped cell.
- [ ] **Touch Cursor Routing**:
  - Press a cursor routing button near the start of the display: Video seeks to beginning.
  - Press a cursor routing button near the end of the display: Video seeks near the end.
  - Press a cursor routing button at the center: Video seeks to the 50% position.
- [ ] **Toggle Shortcut (`Ctrl + Shift + D`)**:
  - Press `Ctrl + Shift + D`: Braille progress line disappears; screen reader immediately displays standard focused UI text and subtitles on the Braille display.
  - Press `Ctrl + Shift + D` again: Braille progress line returns and resumes tracking track position.

---

## 6. Windows High Contrast & Display Scaling Audit

- [ ] **High Contrast Mode**: Press `Left Alt + Left Shift + PrintScreen` to enable Windows High Contrast (Contrast Black / Contrast White).
  - Verify all text, slider thumbs, focus rectangles, and buttons retain high contrast ($\ge 7:1$).
  - Focus borders must be prominently visible with dotted double-stroke styling.
- [ ] **DPI Scaling**:
  - Test at 100%, 125%, 150%, and 200% scaling in Windows Display Settings.
  - Verify layout does not clip text, overlap buttons, or distort video host aspect ratio.
- [ ] **Window Resizing**:
  - Resize window from minimum dimensions (850x560) to full screen (1920x1080 or higher).
  - Verify GridSplitter between search results and playback queue remains interactive.
