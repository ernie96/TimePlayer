//! Braille progress through TimePlayer's NVDA add-on (1.x's `BrailleProgressTrack`, `NvdaBrailleBridge`
//! and `NvdaAddonInstaller`). The add-on reads two files in the temp folder, the main window's handle and the
//! playback state ("playing 0.4231 0.12,0.35"), and sends the window WM_USER+777 when a routing key is
//! pressed (seek) and WM_USER+778 to switch braille progress. Windows only.

use std::path::PathBuf;
use std::sync::Mutex;

pub const LOWER_G: char = '\u{2836}';
/// Dots 7 and 8: where a chapter starts.
pub const CHAPTER_MARK: char = '\u{28C0}';
pub const BRAILLE_BLANK: char = '\u{2800}';

pub const WM_TIMEPLAYER_BRAILLE_TOUCH: u32 = 0x0400 + 777;
pub const WM_TIMEPLAYER_TOGGLE_BRAILLE: u32 = 0x0400 + 778;
pub const ADDON_NAME: &str = "timePlayerBraille";

const MANIFEST: &str = include_str!("../nvda-addon/manifest.ini");
const PLUGIN: &str = include_str!("../nvda-addon/globalPlugins/timePlayerBraille.py");

pub struct Track {
    pub cells: usize,
}

impl Track {
    pub fn new(cells: usize) -> Self {
        Self { cells: cells.max(2) }
    }

    pub fn cell_index(&self, position: f64, duration: f64) -> usize {
        if duration <= 0.0 || position <= 0.0 {
            return 0;
        }
        if position >= duration {
            return self.cells - 1;
        }
        (((position / duration) * (self.cells - 1) as f64).round() as usize).min(self.cells - 1)
    }

    /// Where a routing key on `cell` goes.
    pub fn seek_time(cell: usize, cells: usize, duration: f64) -> f64 {
        if duration <= 0.0 || cells < 2 {
            return 0.0;
        }
        let fraction = cell.min(cells - 1) as f64 / (cells - 1) as f64;
        (fraction * duration * 1000.0).round() / 1000.0
    }

    /// The line shown in TimePlayer itself: chapter marks, and the g where playback is (blinking while paused).
    pub fn line(&self, position: f64, duration: f64, playing: bool, paused: bool, blink_visible: bool, chapter_starts: &[f64]) -> String {
        let mut cells = vec![BRAILLE_BLANK; self.cells];
        if (!playing && !paused) || (paused && !blink_visible) {
            return cells.into_iter().collect();
        }
        for start in chapter_starts.iter().filter(|s| **s > 0.0) {
            cells[self.cell_index(*start, duration)] = CHAPTER_MARK;
        }
        let active = self.cell_index(position, duration);
        cells[active] = if cells[active] == CHAPTER_MARK { char::from_u32(LOWER_G as u32 | CHAPTER_MARK as u32).unwrap_or(LOWER_G) } else { LOWER_G };
        cells.into_iter().collect()
    }
}

fn temp_file(name: &str) -> PathBuf {
    std::env::temp_dir().join(name)
}

static LAST_STATE: Mutex<Option<String>> = Mutex::new(None);

/// "playing 0.4231 0.1200,0.3500", "paused ...", "stopped" or "off" for the add-on.
pub fn state_text(playing: bool, paused: bool, position: f64, duration: f64, chapter_starts: &[f64]) -> String {
    if !playing && !paused {
        return "stopped".into();
    }
    let fraction = if duration > 0.0 { (position / duration).clamp(0.0, 1.0) } else { 0.0 };
    let chapters: Vec<String> =
        if duration > 0.0 { chapter_starts.iter().filter(|s| **s > 0.0).map(|s| format!("{:.4}", (s / duration).clamp(0.0, 1.0))).collect() } else { Vec::new() };
    format!("{} {fraction:.4} {}", if playing { "playing" } else { "paused" }, chapters.join(",")).trim_end().to_owned()
}

/// Writes the state the add-on reads, when it changed.
pub fn publish_state(state: &str) {
    let mut last = LAST_STATE.lock().unwrap();
    if last.as_deref() == Some(state) {
        return;
    }
    if crate::paths::write_atomic(&temp_file("timeplayer_braille_state.txt"), state.as_bytes()).is_ok() {
        *last = Some(state.to_owned());
    }
}

pub fn clear_state() {
    *LAST_STATE.lock().unwrap() = None;
    let _ = std::fs::remove_file(temp_file("timeplayer_braille_state.txt"));
}

/// The main window's handle, so the add-on knows where to send routing keys.
pub fn publish_window(hwnd: i64) {
    // Only the number: the add-on reads it with int().
    let _ = std::fs::write(temp_file("timeplayer_hwnd.txt"), hwnd.to_string());
}

pub fn clear_window() {
    let _ = std::fs::remove_file(temp_file("timeplayer_hwnd.txt"));
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AddonResult {
    NvdaNotFound,
    AlreadyCurrent,
    InstalledRestartRequired,
    Failed,
}

/// NVDA's settings folder (%APPDATA%\nvda), if NVDA has been used here.
pub fn nvda_config_folder() -> Option<PathBuf> {
    if !cfg!(windows) {
        return None;
    }
    let folder = dirs::config_dir()?.join("nvda");
    folder.is_dir().then_some(folder)
}

pub fn addon_installed() -> bool {
    nvda_config_folder().is_some_and(|d| d.join("addons").join(ADDON_NAME).join("manifest.ini").is_file())
}

/// Installs or updates the add-on in NVDA's add-ons folder (NVDA ignores globalPlugins and appModules).
pub fn install_addon() -> AddonResult {
    let Some(nvda) = nvda_config_folder() else { return AddonResult::NvdaNotFound };
    let addon = nvda.join("addons").join(ADDON_NAME);
    // What earlier test builds left in places NVDA no longer loads.
    for old in [["globalPlugins", "timeplayer_bridge.py"], ["appModules", "timeplayer.py"], ["appModules", "timeplayer_wpf.py"]] {
        let _ = std::fs::remove_file(nvda.join(old[0]).join(old[1]));
    }
    let mut changed = false;
    for (relative, content) in [(PathBuf::from("manifest.ini"), MANIFEST), (PathBuf::from("globalPlugins").join("timePlayerBraille.py"), PLUGIN)] {
        let target = addon.join(&relative);
        if std::fs::read_to_string(&target).is_ok_and(|c| c == content) {
            continue;
        }
        if crate::paths::write_atomic(&target, content.as_bytes()).is_err() {
            return AddonResult::Failed;
        }
        changed = true;
    }
    if changed {
        let _ = std::fs::remove_dir_all(addon.join("globalPlugins").join("__pycache__"));
        AddonResult::InstalledRestartRequired
    } else {
        AddonResult::AlreadyCurrent
    }
}

pub fn uninstall_addon() -> bool {
    match nvda_config_folder() {
        Some(nvda) => {
            let addon = nvda.join("addons").join(ADDON_NAME);
            !addon.exists() || std::fs::remove_dir_all(addon).is_ok()
        }
        None => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_g_moves_and_chapters_are_marked() {
        let track = Track::new(10);
        assert_eq!(track.cell_index(50.0, 100.0), 5);
        let line: Vec<char> = track.line(0.0, 100.0, true, false, true, &[50.0]).chars().collect();
        assert_eq!(line[0], LOWER_G);
        assert_eq!(line[5], CHAPTER_MARK);
        assert_eq!(Track::seek_time(9, 10, 100.0), 100.0);
        assert_eq!(state_text(true, false, 25.0, 100.0, &[12.0, 35.0]), "playing 0.2500 0.1200,0.3500");
        assert_eq!(state_text(false, false, 0.0, 0.0, &[]), "stopped");
    }
}
