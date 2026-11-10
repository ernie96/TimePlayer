//! Small things each system does its own way: opening a web page or folder, showing a file in its folder,
//! and bringing TimePlayer's window to the front.

use std::path::Path;

use wxdragon::prelude::*;

/// Opens a web page in the default browser.
pub fn open_url(url: &str) {
    #[cfg(windows)]
    let result = std::process::Command::new("rundll32").args(["url.dll,FileProtocolHandler", url]).spawn();
    #[cfg(target_os = "macos")]
    let result = std::process::Command::new("open").arg(url).spawn();
    #[cfg(all(unix, not(target_os = "macos")))]
    let result = std::process::Command::new("xdg-open").arg(url).spawn();
    if let Err(e) = result {
        log::warn!("Couldn't open {url}: {e}");
    }
}

/// Opens a folder in the file manager.
pub fn open_folder(folder: &Path) {
    let _ = std::fs::create_dir_all(folder);
    timeplayer_core::downloads::open_folder(folder);
}

/// Opens the folder with `file` selected, where the system can.
pub fn show_in_folder(file: &Path) {
    #[cfg(windows)]
    {
        let _ = std::process::Command::new("explorer.exe").arg(format!("/select,{}", file.display())).spawn();
    }
    #[cfg(target_os = "macos")]
    {
        let _ = std::process::Command::new("open").arg("-R").arg(file).spawn();
    }
    #[cfg(all(unix, not(target_os = "macos")))]
    {
        if let Some(folder) = file.parent() {
            open_folder(folder);
        }
    }
}

/// Brings the window to the front even when another app is in front (Windows only lets the app in front do
/// that, so TimePlayer joins its input queue for a moment, as Tin Pan does).
pub fn bring_to_front(frame: &Frame) {
    frame.iconize(false);
    frame.show(true);
    frame.raise();
    #[cfg(windows)]
    {
        use windows::Win32::Foundation::HWND;
        use windows::Win32::System::Threading::{AttachThreadInput, GetCurrentThreadId};
        use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId, SetForegroundWindow};
        let hwnd = frame.get_handle();
        if hwnd.is_null() {
            return;
        }
        // SAFETY: plain Win32 calls on our own window, from the window's thread.
        unsafe {
            let ours = GetCurrentThreadId();
            let foreground = GetForegroundWindow();
            let theirs = if foreground.is_invalid() { 0 } else { GetWindowThreadProcessId(foreground, None) };
            let attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true).as_bool();
            let _ = SetForegroundWindow(HWND(hwnd));
            if attached {
                let _ = AttachThreadInput(ours, theirs, false);
            }
        }
    }
}
