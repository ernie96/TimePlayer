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

/// The native window mpv can draw into, from TimePlayer's video area: the HWND on Windows, the X11 window
/// on Linux. None on macOS (another process can't draw into an app's view there) and when Linux has no X11.
pub fn video_window_id(video: &Panel) -> Option<i64> {
    #[cfg(windows)]
    {
        let handle = video.get_handle();
        (!handle.is_null()).then_some(handle as i64)
    }
    #[cfg(target_os = "linux")]
    {
        x11::window_id(video.get_handle())
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = video;
        None
    }
}

/// GTK gives out widgets; mpv needs the X11 window behind one.
#[cfg(target_os = "linux")]
mod x11 {
    use std::ffi::{c_int, c_ulong, c_void};

    #[link(name = "gtk-3")]
    unsafe extern "C" {
        fn gtk_widget_realize(widget: *mut c_void);
        fn gtk_widget_get_window(widget: *mut c_void) -> *mut c_void;
    }

    #[link(name = "gdk-3")]
    unsafe extern "C" {
        fn gdk_window_ensure_native(window: *mut c_void) -> c_int;
        fn gdk_x11_window_get_xid(window: *mut c_void) -> c_ulong;
        fn gdk_x11_window_get_type() -> usize;
    }

    #[link(name = "gobject-2.0")]
    unsafe extern "C" {
        fn g_type_check_instance_is_a(instance: *mut c_void, type_: usize) -> c_int;
    }

    pub fn window_id(widget: *mut c_void) -> Option<i64> {
        if widget.is_null() {
            return None;
        }
        // SAFETY: GTK calls on the video area's own widget, on the main thread, after the frame is shown.
        unsafe {
            gtk_widget_realize(widget);
            let window = gtk_widget_get_window(widget);
            // A Wayland window has no X11 id; mpv would then open a window of its own, so it gets none.
            if window.is_null() || g_type_check_instance_is_a(window, gdk_x11_window_get_type()) == 0 {
                return None;
            }
            // Its own X11 window, rather than one shared with the rest of TimePlayer's window.
            if gdk_window_ensure_native(window) == 0 {
                return None;
            }
            let xid = gdk_x11_window_get_xid(window);
            (xid != 0).then_some(xid as i64)
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
