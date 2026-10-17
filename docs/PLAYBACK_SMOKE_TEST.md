# Playback Engine Manual Smoke-Test Procedure

This document provides step-by-step instructions for verifying the **TimePlayer** embedded playback engine (`mpv` embedded via Win32 `HwndHost` and controlled via local Named Pipe JSON IPC).

---

## Prerequisites

1. **Operating System**: Windows 11 or Windows 10 (version 1809+, x64).
2. **.NET SDK**: .NET 10.0 SDK installed.
3. **Application Binaries**:
   - `mpv.exe` in `%LOCALAPPDATA%\TimePlayer\bin\mpv\mpv.exe` (automatically downloaded/verified or configured via `appsettings.json`).
   - `yt-dlp.exe` in `%LOCALAPPDATA%\TimePlayer\bin\yt-dlp.exe`.

---

## Smoke-Test Execution Steps

### Test 1: Native Video Embedding in Application Window

* **Action**: Launch the application from terminal:
  ```powershell
  dotnet run --project src/TimePlayer.Wpf
  ```
* **Action**: Press `Tab` to navigate to the **"Test Video"** button (or click it) and activate it (`Enter` or `Space`).
* **Expected Result**:
  - The embedded video surface inside the main application window displays a high-definition color bars test pattern with an active running timecode counter (`av://lavfi:testsrc`).
  - **No external, popup, or floating player window is created**. The video is rendered strictly inside the WPF application layout boundaries.
  - Resizing the main window or moving the splitter dynamically scales the video surface smoothly without graphical tearing or black borders.

---

### Test 2: Programmatic Playback Control & State Synchronization

* **Action**:
  1. Press `Space` (or activate the **"Play / Pause"** button).
  2. Observe the running timecode counter on the video.
  3. Press `Space` again.
* **Expected Result**:
  - The video test pattern freezes immediately upon pause.
  - The status text and screen reader live region announce *"Paused"*.
  - Activating play resumes video rendering and timecode advance immediately.
  - The status text and screen reader live region announce *"Playing"*.

---

### Test 3: Programmatic Seeking & Position Read-Back

* **Action**:
  1. Activate the **"+5s"** button or press the `Right Arrow` key.
  2. Activate the **"-5s"** button or press the `Left Arrow` key.
  3. Observe the `CurrentTime` timestamp display and `SeekSlider`.
* **Expected Result**:
  - The player seeks forward by 5 seconds, advancing the timecode.
  - The player seeks backward by 5 seconds.
  - The UI position label (`mm:ss`) updates in real-time from mpv's `time-pos` IPC property change events.
  - Screen readers announce the relative seek offset and new position.

---

### Test 4: Volume and Playback Speed Control

* **Action**:
  1. Activate the **"+"** / **"-"** volume buttons or press `+` / `-` keys.
  2. Activate the **">"** / **"<"** speed buttons or press `Shift+.` / `Shift+,`.
  3. Activate the speed reset button (`1x`).
* **Expected Result**:
  - Volume increments/decrements in 5% steps and clamps strictly between 0% and 100%.
  - Playback speed accelerates/decelerates cleanly in 0.25x increments and audio pitch is preserved.
  - Resetting speed restores 1.0x normal speed.

---

### Test 5: Audio-Only Playback

* **Action**: In the search bar, enter an audio stream or direct audio test source:
  `av://lavfi:sine=frequency=440:duration=60`
  Press `Enter`.
* **Expected Result**:
  - A clean 440 Hz audio tone plays.
  - The video display area remains clean without visual artifacts.
  - Volume, pause, and seek operate identically to video playback.

---

### Test 6: Captions & Subtitle Track Management

* **Action**: Load a media file with embedded or sidecar subtitle tracks (`IPlaybackEngine.LoadSubtitleTrackAsync`).
* **Expected Result**:
  - Subtitle track is registered via mpv's `sub-add` command.
  - Subtitles render directly over the video canvas without blocking UI Automation controls.

---

### Test 7: Unexpected Process Termination & Engine Recovery

* **Action**:
  1. While test video is playing, open PowerShell and kill the running `mpv` process tree:
     ```powershell
     Stop-Process -Name mpv -Force
     ```
  2. Observe application behavior.
  3. Activate the **"Test Video"** button again.
* **Expected Result**:
  - The application **does not crash**.
  - The WPF window remains responsive.
  - The error status displays *"Player error: mpv process terminated unexpectedly"* and announces via UI Automation live regions.
  - Activating "Test Video" invokes `RestartAsync()`, spawning a fresh mpv process and reconnecting the IPC pipe. Video playback resumes normally.

---

## Summary Matrix

| Capability | IPC Mechanism | Verification Method |
|---|---|---|
| Window Embedding | Win32 `HwndHost` (`--wid=<HWND>`) | Native child HWND inside WPF grid |
| Local IPC Security | Named Pipe `\\.\pipe\timeplayer-mpv-*` | Strictly local (`.` server identifier) |
| Play / Pause | `set_property pause <bool>` | Verified via `Space` / buttons |
| Absolute / Relative Seek | `seek <seconds> <absolute\|relative>` | Verified via buttons / arrow keys |
| Position & Duration | `observe_property time-pos / duration` | Live UI update from mpv events |
| Volume & Speed | `set_property volume / speed` | Real-time adjustment & clamping |
| Subtitles | `sub-add <url> <select>` | Documented mpv command |
| Fault Tolerance | Process exit handler + `RestartAsync()` | Process kill test recovers cleanly |
