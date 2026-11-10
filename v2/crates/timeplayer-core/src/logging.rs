//! The log, as 1.x writes it: logs/timeplayer-YYYYMMDD.log, one file per day, the last seven kept, lines
//! like "13:21:47.778 Warning     YtDlp: ...". Information and above; written on a background thread.

use std::io::Write;
use std::sync::mpsc::{Sender, channel};
use std::sync::{Mutex, OnceLock};

static SENDER: OnceLock<Mutex<Sender<String>>> = OnceLock::new();

struct FileLogger;

fn level_name(level: log::Level) -> &'static str {
    match level {
        log::Level::Error => "Error",
        log::Level::Warn => "Warning",
        log::Level::Info => "Information",
        log::Level::Debug => "Debug",
        log::Level::Trace => "Trace",
    }
}

impl log::Log for FileLogger {
    fn enabled(&self, metadata: &log::Metadata) -> bool {
        metadata.level() <= log::Level::Info
    }

    fn log(&self, record: &log::Record) {
        if !self.enabled(record.metadata()) {
            return;
        }
        // "timeplayer_core::ytdlp" → "ytdlp"
        let target = record.target().rsplit("::").next().unwrap_or(record.target());
        let line = format!("{} {:<11} {target}: {}\n", chrono::Local::now().format("%H:%M:%S%.3f"), level_name(record.level()), record.args());
        if let Some(tx) = SENDER.get() {
            let _ = tx.lock().unwrap().send(line);
        }
    }

    fn flush(&self) {}
}

/// Starts logging to the logs folder. Later calls do nothing.
pub fn init() {
    let (tx, rx) = channel::<String>();
    if SENDER.set(Mutex::new(tx)).is_err() {
        return;
    }
    let folder = crate::paths::logs_folder();
    std::thread::Builder::new()
        .name("log writer".into())
        .spawn(move || {
            let _ = std::fs::create_dir_all(&folder);
            delete_old(&folder);
            for line in rx {
                let file = folder.join(format!("timeplayer-{}.log", chrono::Local::now().format("%Y%m%d")));
                if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(file) {
                    let _ = f.write_all(line.as_bytes());
                }
            }
        })
        .ok();
    let _ = log::set_logger(&FileLogger);
    log::set_max_level(log::LevelFilter::Info);
    std::panic::set_hook(Box::new(|info| {
        log::error!("TimePlayer stopped unexpectedly: {info}");
    }));
}

fn delete_old(folder: &std::path::Path) {
    let Ok(entries) = std::fs::read_dir(folder) else { return };
    let mut logs: Vec<std::path::PathBuf> = entries
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.file_name().is_some_and(|n| n.to_string_lossy().starts_with("timeplayer-") && n.to_string_lossy().ends_with(".log")))
        .collect();
    logs.sort();
    logs.reverse();
    for old in logs.into_iter().skip(7) {
        let _ = std::fs::remove_file(old);
    }
}
