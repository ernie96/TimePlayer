//! What TimePlayer says (1.x's `WpfAccessibilityService`). While TimePlayer is the window in front, a live
//! region carries it, so the screen reader reads it in its own voice and order; when it isn't, it goes straight
//! to the screen reader through prism, since a live region in a background window goes unheard (as Tin Pan 2).

use std::cell::Cell;

use live_region::Priority;
use wxdragon::prelude::*;

thread_local! {
    static LABEL: Cell<Option<StaticText>> = const { Cell::new(None) };
}

/// Makes the hidden live-region label. Call once, on the window's thread, after the window is built.
pub fn init(parent: &impl WxWidget) {
    let label = StaticText::builder(parent).with_label("").with_size(Size::new(0, 0)).build();
    label.show(false);
    live_region::set_live_region(&label);
    LABEL.set(Some(label));
    #[cfg(all(any(windows, target_os = "macos", target_os = "linux"), feature = "background-speech"))]
    background::init();
}

/// Something the user just asked for (1.x's High priority): it cuts off whatever is being said.
pub fn say(text: &str) {
    speak(text, Priority::High);
}

/// An error; always said, cutting off what is being said.
pub fn say_error(text: &str) {
    speak(text, Priority::High);
}

/// Something that happened by itself (1.x's Medium and Low): said after what is being said.
pub fn say_quietly(text: &str) {
    speak(text, Priority::Medium);
}

/// Captions and sung lyrics read aloud, after what is being said.
/// They go straight to the screen reader (NVDA, JAWS, Narrator, VoiceOver or Orca, through prism) even while
/// TimePlayer is in front: a live region that changes as fast as captions do loses some of them.
pub fn say_caption(text: &str) {
    if text.trim().is_empty() {
        return;
    }
    #[cfg(all(any(windows, target_os = "macos", target_os = "linux"), feature = "background-speech"))]
    if background::speak(text, false) {
        return;
    }
    speak(text, Priority::Medium);
}

fn speak(text: &str, priority: Priority) {
    if text.trim().is_empty() {
        return;
    }
    #[cfg(all(any(windows, target_os = "macos", target_os = "linux"), feature = "background-speech"))]
    if !background::in_front() && background::speak(text, priority == Priority::High) {
        return;
    }
    if let Some(label) = LABEL.get() {
        live_region::announce_with_priority(label, text, priority);
    }
}

/// Whether a TimePlayer window is the one in front (true when that can't be told), so an update offer
/// doesn't pop up behind the app the user is working in.
pub fn app_in_front() -> bool {
    #[cfg(all(any(windows, target_os = "macos", target_os = "linux"), feature = "background-speech"))]
    return background::in_front();
    #[allow(unreachable_code)]
    true
}

#[cfg(all(any(windows, target_os = "macos", target_os = "linux"), feature = "background-speech"))]
mod background {
    use prismer::{Backend, Prism};

    thread_local! {
        // Prism is only used from the window's thread, and a backend isn't Send. A backend borrows its
        // context; leaking the context once lets the backend be kept rather than rebuilt for every line.
        static BACKEND: Option<Backend<'static>> = Prism::new().ok().and_then(|prism| {
            let prism: &'static Prism = Box::leak(Box::new(prism));
            prism.create_best().ok()
        });
    }

    pub fn init() {
        BACKEND.with(|_| {});
    }

    /// False when there's no screen reader or speech to send it to.
    pub fn speak(text: &str, interrupt: bool) -> bool {
        BACKEND.with(|backend| backend.as_ref().is_some_and(|backend| backend.speak(text, interrupt).is_ok()))
    }

    /// Whether one of TimePlayer's windows is the active one; Orca reads its announcements then.
    #[cfg(target_os = "linux")]
    pub fn in_front() -> bool {
        use std::ffi::{c_int, c_void};
        #[repr(C)]
        struct GList {
            data: *mut c_void,
            next: *mut GList,
            prev: *mut GList,
        }
        #[link(name = "gtk-3")]
        unsafe extern "C" {
            fn gtk_window_list_toplevels() -> *mut GList;
            fn gtk_window_is_active(window: *mut c_void) -> c_int;
        }
        #[link(name = "glib-2.0")]
        unsafe extern "C" {
            fn g_list_free(list: *mut GList);
        }
        // SAFETY: GTK's own list of this process's top-level windows, read and freed on the main thread.
        unsafe {
            let list = gtk_window_list_toplevels();
            let mut item = list;
            let mut active = false;
            while !item.is_null() {
                active |= gtk_window_is_active((*item).data) != 0;
                item = (*item).next;
            }
            g_list_free(list);
            active
        }
    }

    /// Whether TimePlayer is the app in front; VoiceOver reads its announcements then.
    #[cfg(target_os = "macos")]
    pub fn in_front() -> bool {
        use objc::runtime::{BOOL, NO, Object};
        use objc::{class, msg_send, sel, sel_impl};
        // SAFETY: NSApplication's own getters, on the main thread, where speech always runs.
        unsafe {
            let app: *mut Object = msg_send![class!(NSApplication), sharedApplication];
            let active: BOOL = msg_send![app, isActive];
            active != NO
        }
    }

    /// Whether the window in front belongs to TimePlayer (the main window or one of its dialogs).
    #[cfg(windows)]
    pub fn in_front() -> bool {
        use windows::Win32::System::Threading::GetCurrentProcessId;
        use windows::Win32::UI::WindowsAndMessaging::{GetForegroundWindow, GetWindowThreadProcessId};
        let mut process = 0;
        // SAFETY: plain Win32 queries; process is a valid out pointer.
        unsafe {
            let front = GetForegroundWindow();
            if front.is_invalid() {
                return false;
            }
            GetWindowThreadProcessId(front, Some(&raw mut process));
            process == GetCurrentProcessId()
        }
    }
}
