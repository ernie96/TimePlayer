# TimePlayer Dependency Management and Verification Architecture

This document describes how TimePlayer pins, verifies, updates, and protects local media playback dependencies (`yt-dlp` and `mpv`).

---

## 1. Managed Dependencies & Official Sources

| Dependency | Purpose | Distribution Format | Official Upstream Source | Verification |
|---|---|---|---|---|
| **yt-dlp** | Standalone metadata extractor, fallback search engine, stream resolver | Single x64 executable (`yt-dlp.exe`) | [yt-dlp GitHub Releases](https://github.com/yt-dlp/yt-dlp/releases) | SHA-256 hash verified against official `SHA2-256SUMS` |
| **mpv** | Native video rendering surface, hardware decoding, audio playback | Standalone x64 bundle (`mpv.exe`, `mpv.com`, `d3dcompiler_43.dll`) | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases) | Version tag verification and executable checksum |
| **NVDA Client** | Direct braille display message delivery | Native 64-bit DLL (`nvdaControllerClient64.dll`) | [NV Access / NVDA](https://github.com/nvaccess/nvda) | C-ABI dynamic loading with graceful fallback |

---

## 2. Directory Resolution Priority (Portability)

To ensure TimePlayer can run either as a self-contained portable application (e.g. from a USB drive) or as an installed app, dependencies are resolved in strict priority order:

1. **User Custom Configuration**:
   - `Playback.MpvCustomPath` and `Backend.YtDlpCustomPath` in `appsettings.json` (if specified and file exists on disk).
2. **Application Portable Directory**:
   - `AppContext.BaseDirectory\bin\yt-dlp.exe`
   - `AppContext.BaseDirectory\bin\mpv\mpv.exe`
   - `AppContext.BaseDirectory\yt-dlp.exe` / `mpv.exe`
3. **Application Managed Data Directory**:
   - `%LocalAppData%\TimePlayer\bin\yt-dlp.exe`
   - `%LocalAppData%\TimePlayer\bin\mpv\mpv.exe`

---

## 3. Safe Download & Verification Strategy

TimePlayer adheres strictly to the rule: **Never silently install unverified executables.**

1. **Staged Downloading**:
   - New binaries are downloaded to an isolated temporary file (e.g. `yt-dlp-{guid}.tmp` or `mpv-{guid}.7z`) in the target folder.
   - The original active executable remains untouched during the entire download process.
2. **Cryptographic Checksum Verification**:
   - For `yt-dlp`, the official `SHA2-256SUMS` file is downloaded from the release assets.
   - The SHA-256 hash of the downloaded `.tmp` file is computed locally and matched against the official release hash.
   - If the hash differs or the release metadata is corrupt, the `.tmp` file is purged immediately, and an error is returned.
3. **Running Process Protection (Zero In-Use Replacement)**:
   - Before attempting to swap an executable, TimePlayer checks if any running `mpv.exe` or `yt-dlp.exe` processes exist.
   - If a media playback session is active, the update is postponed and the user is informed. TimePlayer **never** attempts to overwrite an executable while it is executing.
4. **Atomic Rollback & Fault Tolerance**:
   - Existing binaries are backed up to `.bak` prior to replacement.
   - If replacement fails (due to antivirus locks, permissions, or system reboot), the `.bak` file is restored automatically.
   - Even if network connection is lost midway through an update check, the existing installed version remains fully operational.

---

## 4. Automatic & Manual Update Mechanisms

- **Startup Verification**: At application startup, TimePlayer invokes `EnsureInstalledOrUpdatedAsync()` in the background to verify the binary exists and has valid metadata.
- **Manual Check**: Users can navigate to the **Settings and Diagnostics** dialog (`F2`), click **"Check for Updates"**, and inspect the verified version report without technical knowledge.
