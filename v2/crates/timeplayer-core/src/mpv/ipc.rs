//! mpv's JSON IPC (1.x's `MpvIpcClient`): one JSON object per line. Commands carry a request ID and get a
//! reply with it; events and property changes arrive in between. On Windows the connection is a named
//! pipe opened for overlapped I/O, so the reading thread never blocks writes; elsewhere a Unix socket.

use serde_json::{Value, json};
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Read, Write};
use std::sync::atomic::{AtomicBool, AtomicI64, Ordering};
use std::sync::mpsc::{Sender, channel};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::error::{AppError, Result};

/// Something mpv said without being asked.
#[derive(Debug, Clone)]
pub enum Message {
    Event { name: String, reason: Option<String>, error: Option<String>, entry_id: Option<i64> },
    Property { id: i64, name: String, value: Value },
    Disconnected,
}

pub type Handler = Arc<dyn Fn(Message) + Send + Sync>;

type Pending = Arc<Mutex<HashMap<i64, Sender<Value>>>>;

pub struct Client {
    writer: Mutex<Box<dyn Write + Send>>,
    pending: Pending,
    next_id: AtomicI64,
    connected: Arc<AtomicBool>,
}

impl Client {
    /// Connects to `endpoint` (a pipe name on Windows, a socket path elsewhere), retrying until mpv is
    /// listening or `timeout` passes.
    pub fn connect(endpoint: &str, timeout: Duration, handler: Handler) -> Result<Self> {
        let started = Instant::now();
        let (reader, writer) = loop {
            match transport::open(endpoint) {
                Ok(pair) => break pair,
                Err(e) if started.elapsed() < timeout => {
                    log::debug!("mpv isn't listening yet: {e}");
                    std::thread::sleep(Duration::from_millis(100));
                }
                Err(e) => return Err(AppError::playback(format!("Communication with the media player timed out. ({e})"))),
            }
        };
        let pending: Pending = Arc::default();
        let connected = Arc::new(AtomicBool::new(true));
        {
            let pending = pending.clone();
            let connected = connected.clone();
            std::thread::Builder::new()
                .name("mpv-ipc".into())
                .spawn(move || {
                    for line in BufReader::new(reader).lines() {
                        let Ok(line) = line else { break };
                        let Ok(v) = serde_json::from_str::<Value>(&line) else { continue };
                        if let Some(id) = v.get("request_id").and_then(Value::as_i64) {
                            if let Some(tx) = pending.lock().unwrap().remove(&id) {
                                let _ = tx.send(v);
                            }
                            continue;
                        }
                        let Some(name) = v.get("event").and_then(Value::as_str) else { continue };
                        if name == "property-change" {
                            handler(Message::Property {
                                id: v.get("id").and_then(Value::as_i64).unwrap_or(0),
                                name: v.get("name").and_then(Value::as_str).unwrap_or("").to_owned(),
                                value: v.get("data").cloned().unwrap_or(Value::Null),
                            });
                        } else {
                            handler(Message::Event {
                                name: name.to_owned(),
                                reason: v.get("reason").and_then(Value::as_str).map(str::to_owned),
                                error: v.get("file_error").or_else(|| v.get("error")).and_then(Value::as_str).map(str::to_owned),
                                entry_id: v.get("playlist_entry_id").and_then(Value::as_i64),
                            });
                        }
                    }
                    connected.store(false, Ordering::SeqCst);
                    pending.lock().unwrap().clear();
                    handler(Message::Disconnected);
                })
                .map_err(|e| AppError::playback(e.to_string()))?;
        }
        Ok(Self { writer: Mutex::new(writer), pending, next_id: AtomicI64::new(1), connected })
    }

    pub fn is_connected(&self) -> bool {
        self.connected.load(Ordering::SeqCst)
    }

    /// Sends a command and waits up to five seconds for mpv's reply ({"error": "success", "data": ...}).
    pub fn command(&self, args: Value) -> Result<Value> {
        if !self.is_connected() {
            return Err(AppError::playback("Playback engine is not running or connected."));
        }
        let id = self.next_id.fetch_add(1, Ordering::SeqCst);
        let (tx, rx) = channel();
        self.pending.lock().unwrap().insert(id, tx);
        let line = format!("{}\n", json!({ "command": args, "request_id": id }));
        {
            let mut writer = self.writer.lock().unwrap();
            if let Err(e) = writer.write_all(line.as_bytes()).and_then(|_| writer.flush()) {
                self.pending.lock().unwrap().remove(&id);
                return Err(AppError::playback(format!("mpv connection was lost: {e}")));
            }
        }
        rx.recv_timeout(Duration::from_secs(5)).map_err(|_| {
            self.pending.lock().unwrap().remove(&id);
            AppError::playback("Communication with the media player timed out.")
        })
    }

    /// True when mpv answered "success".
    pub fn ok(&self, args: Value) -> bool {
        self.command(args).is_ok_and(|r| r.get("error").and_then(Value::as_str) == Some("success"))
    }

    pub fn set(&self, name: &str, value: Value) -> bool {
        self.ok(json!(["set_property", name, value]))
    }

    pub fn get(&self, name: &str) -> Option<Value> {
        let reply = self.command(json!(["get_property", name])).ok()?;
        (reply.get("error").and_then(Value::as_str) == Some("success")).then(|| reply.get("data").cloned().unwrap_or(Value::Null))
    }

    pub fn observe(&self, id: i64, name: &str) -> bool {
        self.ok(json!(["observe_property", id, name]))
    }
}

#[cfg(windows)]
mod transport {
    use super::*;
    use std::os::windows::io::{FromRawHandle, OwnedHandle, AsRawHandle};
    use windows_sys::Win32::Foundation::{CloseHandle, ERROR_IO_PENDING, GENERIC_READ, GENERIC_WRITE, GetLastError, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_FLAG_OVERLAPPED, OPEN_EXISTING, ReadFile, WriteFile};
    use windows_sys::Win32::System::IO::{GetOverlappedResult, OVERLAPPED};
    use windows_sys::Win32::System::Threading::CreateEventW;

    /// One handle, opened for overlapped I/O, shared by a reader and a writer.
    struct Pipe {
        handle: Arc<OwnedHandle>,
    }

    // SAFETY: overlapped I/O on one handle from two threads is allowed; each call has its own OVERLAPPED.
    unsafe impl Send for Pipe {}

    impl Pipe {
        fn io(&self, buf: *mut u8, len: usize, write: bool) -> std::io::Result<usize> {
            // SAFETY: a fresh manual-reset event and OVERLAPPED per call; we wait for completion before the
            // buffer and the OVERLAPPED go out of scope.
            unsafe {
                let event = CreateEventW(std::ptr::null(), 1, 0, std::ptr::null());
                if event.is_null() {
                    return Err(std::io::Error::last_os_error());
                }
                let mut overlapped: OVERLAPPED = std::mem::zeroed();
                overlapped.hEvent = event;
                let handle = self.handle.as_raw_handle() as HANDLE;
                let mut done = 0u32;
                let ok = if write {
                    WriteFile(handle, buf, len as u32, &mut done, &mut overlapped)
                } else {
                    ReadFile(handle, buf, len as u32, &mut done, &mut overlapped)
                };
                let result = if ok != 0 || GetLastError() == ERROR_IO_PENDING {
                    if GetOverlappedResult(handle, &overlapped, &mut done, 1) != 0 {
                        Ok(done as usize)
                    } else {
                        Err(std::io::Error::last_os_error())
                    }
                } else {
                    Err(std::io::Error::last_os_error())
                };
                CloseHandle(event);
                result
            }
        }
    }

    impl Read for Pipe {
        fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
            match self.io(buf.as_mut_ptr(), buf.len(), false) {
                // A closed pipe reads as the end.
                Err(e) if e.raw_os_error() == Some(109) => Ok(0),
                other => other,
            }
        }
    }

    impl Write for Pipe {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            self.io(buf.as_ptr().cast_mut(), buf.len(), true)
        }

        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    pub fn open(name: &str) -> std::io::Result<(Box<dyn Read + Send>, Box<dyn Write + Send>)> {
        let path: Vec<u16> = format!(r"\\.\pipe\{name}").encode_utf16().chain(std::iter::once(0)).collect();
        // SAFETY: a null-terminated path; the handle is owned and closed by OwnedHandle.
        let handle = unsafe {
            CreateFileW(path.as_ptr(), GENERIC_READ | GENERIC_WRITE, 0, std::ptr::null(), OPEN_EXISTING, FILE_FLAG_OVERLAPPED, std::ptr::null_mut())
        };
        if handle == INVALID_HANDLE_VALUE {
            return Err(std::io::Error::last_os_error());
        }
        // SAFETY: CreateFileW returned a valid handle we now own.
        let owned = Arc::new(unsafe { OwnedHandle::from_raw_handle(handle as _) });
        Ok((Box::new(Pipe { handle: owned.clone() }), Box::new(Pipe { handle: owned })))
    }
}

#[cfg(unix)]
mod transport {
    use super::*;
    use std::os::unix::net::UnixStream;

    pub fn open(path: &str) -> std::io::Result<(Box<dyn Read + Send>, Box<dyn Write + Send>)> {
        let stream = UnixStream::connect(path)?;
        Ok((Box::new(stream.try_clone()?), Box::new(stream)))
    }
}

/// Where mpv should listen: a pipe name on Windows, a socket path elsewhere.
pub fn new_endpoint() -> String {
    let unique = uuid::Uuid::new_v4().simple().to_string();
    if cfg!(windows) {
        format!("timeplayer-mpv-{unique}")
    } else {
        std::env::temp_dir().join(format!("timeplayer-mpv-{unique}.sock")).display().to_string()
    }
}

/// The --input-ipc-server value for an endpoint.
pub fn server_argument(endpoint: &str) -> String {
    if cfg!(windows) { format!(r"\\.\pipe\{endpoint}") } else { endpoint.to_owned() }
}
