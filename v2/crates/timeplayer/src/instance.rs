//! One TimePlayer at a time (1.x's `SingleInstance`): a second start sends its link to the open TimePlayer, or
//! asks it to come forward, and ends. The open one listens on a named pipe on Windows (per user, as 1.x) and a
//! socket in the data folder elsewhere.

#[cfg(not(windows))]
use std::io::Read;
use std::io::Write;

use wxdragon::prelude::*;

use crate::App;
use crate::jobs::ReplySender;

const MAX_MESSAGE_BYTES: usize = 8192;
/// What a second start sends when it has no link.
const SHOW: &str = "show";

pub enum Start {
    /// This is the only TimePlayer; keep the guard while it runs.
    First(Option<SingleInstanceChecker>),
    AnotherIsOpen,
}

/// Checks for an open TimePlayer; if there is one, hands it the link (or asks it to come forward).
pub fn start(link: Option<&str>) -> Start {
    let Some(checker) = SingleInstanceChecker::new("TimePlayerRunning", None) else {
        log::warn!("The single-instance check couldn't be made; starting anyway");
        return Start::First(None);
    };
    if !checker.is_another_running() {
        return Start::First(Some(checker));
    }
    if !send_to_open_copy(link.unwrap_or(SHOW)) {
        log::warn!("Another TimePlayer is running but didn't answer");
    }
    Start::AnotherIsOpen
}

#[cfg(windows)]
fn pipe_name() -> String {
    let user = std::env::var("USERNAME").unwrap_or_default();
    format!(r"\\.\pipe\TimePlayer2-Links-{user}")
}

#[cfg(not(windows))]
fn socket_path() -> std::path::PathBuf {
    timeplayer_core::paths::data_folder().join("timeplayer-links.sock")
}

/// Sends a link (or "show") to the TimePlayer that's open; false when none answered.
pub fn send_to_open_copy(message: &str) -> bool {
    #[cfg(windows)]
    // SAFETY: a plain Win32 call; ASFW_ANY lets the open TimePlayer take the foreground.
    unsafe {
        let _ = windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(u32::MAX);
    }
    let bytes = message.as_bytes();
    let bytes = &bytes[..bytes.len().min(MAX_MESSAGE_BYTES)];
    for _ in 0..30 {
        #[cfg(windows)]
        let connection = std::fs::OpenOptions::new().write(true).open(pipe_name());
        #[cfg(not(windows))]
        let connection = std::os::unix::net::UnixStream::connect(socket_path());
        match connection {
            Ok(mut c) => return c.write_all(bytes).and_then(|()| c.flush()).is_ok(),
            Err(_) => std::thread::sleep(std::time::Duration::from_millis(100)),
        }
    }
    false
}

/// Listens for links from later starts and from the browser extension; each comes to the app.
pub fn listen(sender: ReplySender) {
    let spawned = std::thread::Builder::new().name("links".into()).spawn(move || listen_loop(&sender));
    if let Err(e) = spawned {
        log::warn!("Links from other apps: couldn't listen: {e}");
    }
}

fn deliver(sender: &ReplySender, message: String) {
    let message = message.trim().to_owned();
    if message.is_empty() {
        return;
    }
    sender.send(move |app: &mut App| {
        if message == SHOW {
            crate::platform::bring_to_front(&app.ui.frame);
            return;
        }
        match timeplayer_core::links::normalize_incoming(&message) {
            Some(link) => app.open_link_from_other_app(&link),
            None => log::info!("Ignored a link from another app that isn't a YouTube video or playlist."),
        }
    });
}

#[cfg(not(windows))]
fn listen_loop(sender: &ReplySender) {
    let path = socket_path();
    let _ = std::fs::remove_file(&path);
    let listener = match std::os::unix::net::UnixListener::bind(&path) {
        Ok(l) => l,
        Err(e) => {
            log::warn!("Links from other apps: couldn't listen on {}: {e}", path.display());
            return;
        }
    };
    for stream in listener.incoming().flatten() {
        let mut text = String::new();
        let _ = stream.take(MAX_MESSAGE_BYTES as u64).read_to_string(&mut text);
        deliver(sender, text);
    }
}

#[cfg(windows)]
fn listen_loop(sender: &ReplySender) {
    use windows::Win32::Foundation::{CloseHandle, ERROR_PIPE_CONNECTED, GetLastError, INVALID_HANDLE_VALUE};
    use windows::Win32::Storage::FileSystem::{PIPE_ACCESS_INBOUND, ReadFile};
    use windows::Win32::System::Pipes::{ConnectNamedPipe, CreateNamedPipeW, DisconnectNamedPipe, PIPE_READMODE_BYTE, PIPE_REJECT_REMOTE_CLIENTS, PIPE_TYPE_BYTE, PIPE_WAIT};
    use windows::core::HSTRING;
    let name = HSTRING::from(pipe_name());
    loop {
        // SAFETY: Win32 pipe calls on a handle this loop owns and closes.
        unsafe {
            let pipe = CreateNamedPipeW(&name, PIPE_ACCESS_INBOUND, PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 0, MAX_MESSAGE_BYTES as u32, 0, None);
            if pipe == INVALID_HANDLE_VALUE {
                log::warn!("Links from other apps: the pipe couldn't be made; trying again.");
                std::thread::sleep(std::time::Duration::from_millis(500));
                continue;
            }
            let connected = ConnectNamedPipe(pipe, None).is_ok() || GetLastError() == ERROR_PIPE_CONNECTED;
            if connected {
                let mut buffer = vec![0u8; MAX_MESSAGE_BYTES];
                let mut total = 0usize;
                loop {
                    let mut read = 0u32;
                    if ReadFile(pipe, Some(&mut buffer[total..]), Some(&mut read), None).is_err() || read == 0 {
                        break;
                    }
                    total += read as usize;
                    if total >= MAX_MESSAGE_BYTES {
                        break;
                    }
                }
                deliver(sender, String::from_utf8_lossy(&buffer[..total]).into_owned());
            }
            let _ = DisconnectNamedPipe(pipe);
            let _ = CloseHandle(pipe);
        }
    }
}
