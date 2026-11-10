//! Where TimePlayer keeps its data: the same folder as 1.x, so updating to 2.0 keeps everything.
//! Windows: `%LOCALAPPDATA%\TimePlayer`; Linux: `~/.local/share/TimePlayer`; macOS:
//! `~/Library/Application Support/TimePlayer`. In portable mode (a `portable.txt` or `settings.json` next
//! to the program) everything stays in the program folder.

use std::path::PathBuf;
use std::sync::OnceLock;

pub const APP_NAME: &str = "TimePlayer";

/// The folder TimePlayer's program file is in.
pub fn program_folder() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|p| p.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."))
}

pub fn is_portable() -> bool {
    let folder = program_folder();
    folder.join("portable.txt").exists() || folder.join("settings.json").exists()
}

static OVERRIDE: OnceLock<PathBuf> = OnceLock::new();

/// Tests and the self-check use their own folder.
pub fn set_data_folder(folder: PathBuf) {
    let _ = OVERRIDE.set(folder);
}

pub fn data_folder() -> PathBuf {
    if let Some(folder) = OVERRIDE.get() {
        return folder.clone();
    }
    if is_portable() {
        return program_folder();
    }
    #[cfg(windows)]
    let base = dirs::data_local_dir();
    #[cfg(not(windows))]
    let base = dirs::data_dir();
    base.unwrap_or_else(|| PathBuf::from(".")).join(APP_NAME)
}

pub fn settings_file() -> PathBuf {
    data_folder().join("settings.json")
}

pub fn library_folder() -> PathBuf {
    data_folder().join("library")
}

pub fn logs_folder() -> PathBuf {
    data_folder().join("logs")
}

/// Where yt-dlp, mpv and QuickJS go when TimePlayer downloads them.
pub fn tools_folder() -> PathBuf {
    let beside = program_folder().join("bin");
    if beside.exists() && writable(&beside) {
        return beside;
    }
    data_folder().join("bin")
}

fn writable(folder: &std::path::Path) -> bool {
    let probe = folder.join(format!(".write-test-{}", std::process::id()));
    let ok = std::fs::write(&probe, b"").is_ok();
    let _ = std::fs::remove_file(&probe);
    ok
}

/// "2.0.0".
pub fn version() -> &'static str {
    env!("CARGO_PKG_VERSION")
}

/// Writes a file so that a crash halfway leaves the old one in place.
pub fn write_atomic(path: &std::path::Path, contents: &[u8]) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let temp = path.with_extension(format!("tmp-{}", std::process::id()));
    std::fs::write(&temp, contents)?;
    std::fs::rename(&temp, path).inspect_err(|_| {
        let _ = std::fs::remove_file(&temp);
    })
}
