//! The NVDA add-on's window messages (1.x's MainWindow WndProc): a routing key on the braille line
//! (WM_USER+777, wParam the cell, lParam the display's cell count) seeks, and Control+Shift+D caught by the
//! add-on (WM_USER+778) turns the braille track on or off. The add-on finds the window through a file in the
//! temp folder. Windows only.

use wxdragon::prelude::*;

#[cfg(windows)]
pub fn attach(frame: &Frame) {
    use timeplayer_core::braille::{self, WM_TIMEPLAYER_BRAILLE_TOUCH, WM_TIMEPLAYER_TOGGLE_BRAILLE};
    use windows::Win32::Foundation::{HWND, LPARAM, LRESULT, WPARAM};
    use windows::Win32::UI::Shell::{DefSubclassProc, SetWindowSubclass};
    use windows::Win32::UI::WindowsAndMessaging::{ChangeWindowMessageFilterEx, MSGFLT_ALLOW};

    unsafe extern "system" fn proc(hwnd: HWND, msg: u32, wparam: WPARAM, lparam: LPARAM, _id: usize, _data: usize) -> LRESULT {
        if msg == WM_TIMEPLAYER_BRAILLE_TOUCH {
            let (cell, cells) = (wparam.0 as isize, lparam.0);
            if cell >= 0 {
                // The message may come while a dialog of TimePlayer's is open; it waits for the app's next turn.
                crate::nvda::queue(move |app| app.braille_touch(cell as usize, usize::try_from(cells).unwrap_or(0)));
                return LRESULT(0);
            }
        } else if msg == WM_TIMEPLAYER_TOGGLE_BRAILLE {
            crate::nvda::queue(crate::App::toggle_braille_progress);
            return LRESULT(0);
        }
        // SAFETY: passes every other message on to wxWidgets' own window procedure.
        unsafe { DefSubclassProc(hwnd, msg, wparam, lparam) }
    }

    let handle = frame.get_handle();
    if handle.is_null() {
        return;
    }
    let hwnd = HWND(handle);
    // Nothing is playing yet: overwrite state a previous run left (after a crash, say) before the add-on
    // learns this window exists.
    braille::publish_state("stopped");
    // SAFETY: subclassing our own top-level window on its thread; the procedure outlives it.
    unsafe {
        let _ = SetWindowSubclass(hwnd, Some(proc), 777, 0);
        let _ = ChangeWindowMessageFilterEx(hwnd, WM_TIMEPLAYER_BRAILLE_TOUCH, MSGFLT_ALLOW, None);
        let _ = ChangeWindowMessageFilterEx(hwnd, WM_TIMEPLAYER_TOGGLE_BRAILLE, MSGFLT_ALLOW, None);
    }
    braille::publish_window(handle as i64);
}

#[cfg(not(windows))]
pub fn attach(_frame: &Frame) {}

#[cfg(windows)]
thread_local! {
    static SENDER: std::cell::RefCell<Option<crate::jobs::ReplySender>> = const { std::cell::RefCell::new(None) };
}

/// Where the add-on's messages go: the app's reply queue.
#[cfg(windows)]
pub fn set_sender(sender: crate::jobs::ReplySender) {
    SENDER.with_borrow_mut(|s| *s = Some(sender));
}

#[cfg(not(windows))]
pub fn set_sender(_sender: crate::jobs::ReplySender) {}

#[cfg(windows)]
fn queue(f: impl FnOnce(&mut crate::App) + Send + 'static) {
    SENDER.with_borrow(|s| {
        if let Some(sender) = s {
            sender.send(f);
        }
    });
}
