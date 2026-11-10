//! Work that waits on the network or a program runs on its own thread; what it found comes back as a
//! closure that runs on the window's thread with the app, after the thread wakes the window by posting it a
//! menu command (as Tin Pan 2's network thread does). Playback events from the core come back the same way.

use std::sync::Mutex;
use std::sync::mpsc::{Receiver, Sender, channel};

use wxdragon::ffi;
use wxdragon::prelude::*;

use crate::App;

/// The menu command that only says "there's something to read".
pub const ID_WAKE: i32 = 10_999;

pub type Reply = Box<dyn FnOnce(&mut App) + Send>;

/// Whether the main window is still there to be woken; the window's close turns it off, under the lock, so no
/// thread posts to a window that's gone (mpv and background tasks keep reporting while TimePlayer closes).
static WINDOW_OPEN: Mutex<bool> = Mutex::new(true);

/// Called when the main window closes.
pub fn window_closing() {
    *WINDOW_OPEN.lock().unwrap_or_else(|e| e.into_inner()) = false;
}

/// Wakes the window. Posting a command is safe from any thread.
#[derive(Clone, Copy)]
struct Waker(usize);

impl Waker {
    fn wake(self) {
        let open = WINDOW_OPEN.lock().unwrap_or_else(|e| e.into_inner());
        if *open {
            // SAFETY: the window is open (checked under the lock its close takes); wxWidgets queues the posted
            // event for its own thread.
            unsafe { ffi::wxd_Window_PostMenuCommand(self.0 as *mut ffi::wxd_Window_t, ID_WAKE) };
        }
    }
}

/// Sends replies to the window's thread from anywhere.
#[derive(Clone)]
pub struct ReplySender {
    tx: Sender<Reply>,
    waker: Waker,
}

impl ReplySender {
    pub fn send(&self, reply: impl FnOnce(&mut App) + Send + 'static) {
        if self.tx.send(Box::new(reply)).is_ok() {
            self.waker.wake();
        }
    }
}

pub struct Jobs {
    sender: ReplySender,
    rx: Receiver<Reply>,
}

impl Jobs {
    pub fn new(frame: &Frame) -> Self {
        let (tx, rx) = channel();
        Self { sender: ReplySender { tx, waker: Waker(frame.handle_ptr() as usize) }, rx }
    }

    pub fn sender(&self) -> ReplySender {
        self.sender.clone()
    }

    /// Runs `work` on a thread of its own; what it returns runs with the app on the window's thread.
    pub fn run<R: FnOnce(&mut App) + Send + 'static>(&self, name: &str, work: impl FnOnce() -> R + Send + 'static) {
        let sender = self.sender.clone();
        let spawned = std::thread::Builder::new().name(name.to_owned()).spawn(move || {
            match std::panic::catch_unwind(std::panic::AssertUnwindSafe(work)) {
                Ok(reply) => sender.send(reply),
                Err(_) => sender.send(|_: &mut App| crate::speech::say_error(&timeplayer_core::i18n::t("Something went wrong. Details are in the log."))),
            }
        });
        if let Err(e) = spawned {
            log::error!("Couldn't start a background task: {e}");
        }
    }

    /// The replies waiting, oldest first.
    pub fn take(&self) -> Vec<Reply> {
        self.rx.try_iter().collect()
    }
}
