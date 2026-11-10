//! A self-check for debug builds: with TIMEPLAYER_SELF_CHECK=1, TimePlayer starts in a data folder of its own,
//! goes through the main window's keys one step at a time, logs "Self-check passed" or "Self-check FAILED" for
//! each, and closes. Keys a control handles itself (the list's arrows) are posted to it as real WM_KEYDOWN
//! messages; keys TimePlayer handles in its key hook go to that handler directly, since posted keys never reach
//! a Windows keyboard hook. Nothing here runs in release builds.

use wxdragon::prelude::*;

use crate::keys::Focus;
use crate::sections::Section;
use crate::{App, with_app};

const VARIABLE: &str = "TIMEPLAYER_SELF_CHECK";

fn enabled() -> bool {
    cfg!(debug_assertions) && std::env::var(VARIABLE).is_ok_and(|v| v == "1")
}

/// Points the data folder at a fresh temporary one, before anything reads it.
pub fn prepare() {
    if !enabled() {
        return;
    }
    // One folder for every run, so mpv and yt-dlp are downloaded once; the check only reads the library.
    let folder = std::env::temp_dir().join("timeplayer-self-check");
    let _ = std::fs::create_dir_all(&folder);
    // The startup questions are answered and the network checks are off, so no dialog stops the check.
    let settings = r#"{"Accessibility":{"UseBrailleAddon":false,"EnableBrailleProgress":false},"Runs":{"Share":false,"Announce":false},"Update":{"CheckAtStartup":false},"Library":{"CheckFeedOnStartup":false}}"#;
    let _ = std::fs::write(folder.join("settings.json"), settings);
    timeplayer_core::paths::set_data_folder(folder);
}

type Step = (&'static str, fn(&mut App), fn(&App) -> bool);

fn steps() -> Vec<Step> {
    vec![
        ("starts in the search box", |_| {}, |app| app.focus() == Focus::SearchBox),
        ("Control+5 shows Playlists with Liked videos", |app| press(app, '5', true, false, false), |app| app.browse.current() == Section::Playlists && !app.browse.items.is_empty()),
        ("Control+5 moves to the list", |_| {}, |app| app.focus() == Focus::List),
        ("Down in the list keeps a row selected", |app| post(app, WXK_DOWN), |app| app.ui.list.selected_index().is_some()),
        ("Home selects the first row", |app| post(app, WXK_HOME), |app| app.ui.list.selected_index() == Some(0)),
        ("Enter opens Liked videos", |app| press_code(app, WXK_RETURN, false, false, false), |app| app.browse.current() == Section::Playlist),
        ("Alt+Left goes back to Playlists", |app| press_code(app, WXK_LEFT, false, false, true), |app| app.browse.current() == Section::Playlists),
        ("Control+8 shows the Queue", |app| press(app, '8', true, false, false), |app| app.browse.current() == Section::Queue),
        ("Control+Tab goes to Featured", |app| press_code(app, WXK_TAB, true, false, false), |app| app.browse.current() == Section::Featured),
        ("Control+Shift+Tab goes back to the Queue", |app| press_code(app, WXK_TAB, true, true, false), |app| app.browse.current() == Section::Queue),
        ("Control+7 shows History", |app| press(app, '7', true, false, false), |app| app.browse.current() == Section::History),
        ("Slash goes to the search box", |app| press(app, '/', false, false, false), |app| app.focus() == Focus::SearchBox),
        ("F6 moves to the sections", |app| press_code(app, WXK_F6, false, false, false), |app| app.focus() == Focus::SectionTabs),
        ("Shift+F6 moves back to the search box", |app| press_code(app, WXK_F6, false, true, false), |app| app.focus() == Focus::SearchBox),
        ("Control+M switches to Music", |app| press(app, 'M', true, false, false), |app| app.music_mode && app.browse.tabs.len() == 4),
        ("Control+M switches back to Video", |app| press(app, 'M', true, false, false), |app| !app.music_mode && app.browse.tabs.len() >= 8),
        ("F2 opens Settings", |app| press_code(app, WXK_F2, false, false, false), |app| app.settings_window.is_some() && !app.ui.frame.is_enabled()),
        ("closing Settings returns to the main window", App::close_settings, |app| app.settings_window.is_none() && app.ui.frame.is_enabled()),
        ("Control+Shift+B opens Report a problem", |app| press(app, 'B', true, true, false), |app| app.report_window.is_some()),
        ("Escape closes Report a problem", App::close_report_window, |app| app.report_window.is_none()),
        ("Escape in the search box moves to the list or sections", |app| press_code(app, WXK_ESCAPE, false, false, false), |app| matches!(app.focus(), Focus::List | Focus::SectionTabs)),
    ]
}

fn press(app: &mut App, key: char, ctrl: bool, shift: bool, alt: bool) {
    press_code(app, key as i32, ctrl, shift, alt);
}

fn press_code(app: &mut App, code: i32, ctrl: bool, shift: bool, alt: bool) {
    if !app.key_code(code, ctrl, shift, alt) {
        log::warn!("Self-check: TimePlayer didn't take key {code}");
    }
}

/// Posts a real key press to the window that has focus.
fn post(app: &mut App, code: i32) {
    #[cfg(windows)]
    {
        use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
        use windows::Win32::UI::Input::KeyboardAndMouse::{GetFocus, VK_DOWN, VK_HOME};
        use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_KEYDOWN};
        let vk = match code {
            WXK_DOWN => VK_DOWN.0,
            WXK_HOME => VK_HOME.0,
            _ => return,
        };
        // SAFETY: posts a key message to TimePlayer's own focused window.
        unsafe {
            let focused: HWND = GetFocus();
            let _ = PostMessageW(Some(focused), WM_KEYDOWN, WPARAM(vk as usize), LPARAM(1));
        }
    }
    let _ = (app, code);
}

pub fn start(frame: &Frame) {
    if !enabled() {
        return;
    }
    log::info!("Self-check starting");
    let timer = Timer::new(frame);
    let mut index = 0usize;
    let mut acted = false;
    let mut failures = 0usize;
    let all = steps();
    let closing = *frame;
    timer.on_tick(move |_| {
        if index >= all.len() {
            log::info!("Self-check finished: {} of {} passed", all.len() - failures, all.len());
            closing.close(true);
            return;
        }
        let (name, act, check) = all[index];
        if !acted {
            with_app(act);
            acted = true;
            return;
        }
        let passed = crate::with_app_value(|app| check(app)).unwrap_or(false);
        if passed {
            log::info!("Self-check passed: {name}");
        } else {
            failures += 1;
            log::error!("Self-check FAILED: {name}");
        }
        index += 1;
        acted = false;
    });
    timer.start(700, false);
    std::mem::forget(timer);
}

impl App {
    /// What the report window's Escape does.
    fn close_report_window(&mut self) {
        self.close_report();
    }
}
