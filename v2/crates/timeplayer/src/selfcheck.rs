//! A self-check: with TIMEPLAYER_SELF_CHECK=1, TimePlayer starts in a data folder of its own,
//! goes through the main window's keys one step at a time, logs "Self-check passed" or "Self-check FAILED" for
//! each, and closes. Keys a control handles itself (the list's arrows) are posted to it as real WM_KEYDOWN
//! messages; keys TimePlayer handles in its key hook go to that handler directly, since posted keys never reach
//! a Windows keyboard hook. It only runs when TIMEPLAYER_SELF_CHECK is set.

use wxdragon::prelude::*;

use crate::keys::Focus;
use crate::sections::Section;
use crate::{App, with_app};

const VARIABLE: &str = "TIMEPLAYER_SELF_CHECK";

fn enabled() -> bool {
    // Any build, so the macOS build machine can run the video check on the release build it packages.
    std::env::var(VARIABLE).is_ok_and(|v| v == "1" || v == "video")
}

/// TIMEPLAYER_SELF_CHECK=video: plays mpv's test pattern in the player for a while, so a script can look at the
/// window tree and see where mpv's video window is, then closes.
fn video_mode() -> bool {
    std::env::var(VARIABLE).is_ok_and(|v| v == "video")
}

fn start_video_check(frame: &Frame) {
    log::info!("Self-check (video) starting");
    let timer = Timer::new(frame);
    let mut ticks = 0u32;
    let mut loaded = false;
    let closing = *frame;
    timer.on_tick(move |_| {
        ticks += 1;
        if !loaded {
            let ready = crate::with_app_value(|app| app.playing.engine_ready).unwrap_or(false);
            if ready {
                with_app(App::load_test_video);
                loaded = true;
                ticks = 0;
                log::info!("Self-check (video): test pattern loading");
            } else if ticks > 60 {
                log::error!("Self-check FAILED: mpv didn't start");
                closing.close(true);
            }
            return;
        }
        if ticks == 30 {
            let state = crate::with_app_value(|app| format!("{:?}", app.playing.state)).unwrap_or_default();
            log::info!("Self-check (video): playback state {state}");
            #[cfg(target_os = "macos")]
            log::info!("Self-check (video): frames drawn {}", crate::macvideo::FRAMES.load(std::sync::atomic::Ordering::Relaxed));
            closing.close(true);
        }
    });
    timer.start(500, false);
    std::mem::forget(timer);
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
    let settings = r#"{"Accessibility":{"UseBrailleAddon":false,"EnableBrailleProgress":false},"Runs":{"Share":false,"Announce":false},"Update":{"CheckAtStartup":false},"Library":{"CheckFeedOnStartup":false},"ImportOffered":true}"#;
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
        ("Featured's tab has focus", |app| {
            press(app, '1', true, false, false);
            app.ui.focus_section_tabs();
        }, |app| log_stop(app, 0) && app.focus() == Focus::SectionTabs),
        // Tab from the tabs goes through TimePlayer's key hook (a posted Tab wouldn't reach it); later stops are
        // wxWidgets' own Tab order, posted.
        ("Tab after the tabs reaches the Category box, not an empty panel", |app| press_code(app, WXK_TAB, false, false, false), |app| log_stop(app, 1) && app.ui.category.has_focus()),
        ("Tab stop 2 is a control", |app| post(app, WXK_TAB), |app| log_stop(app, 2) && !focused_is_empty_panel()),
        ("Tab stop 3 is the list", |app| post(app, WXK_TAB), |app| {
            // Posting a key is Windows-only; elsewhere this step is left unchecked, and says so.
            if !cfg!(windows) {
                log::info!("Self-check: Tab order past the Category box isn't checked on this system.");
                return true;
            }
            log_stop(app, 3) && app.focus() == Focus::List
        }),
        ("Shift+Tab from the Category box goes back to the tabs", |app| {
            app.ui.category.set_focus();
            press_code(app, WXK_TAB, false, true, false);
        }, |app| log_stop(app, 4) && app.focus() == Focus::SectionTabs),
        ("Slash goes to the search box", |app| press(app, '/', false, false, false), |app| app.focus() == Focus::SearchBox),
        ("F6 moves to the sections", |app| press_code(app, WXK_F6, false, false, false), |app| app.focus() == Focus::SectionTabs),
        ("Shift+F6 moves back to the search box", |app| press_code(app, WXK_F6, false, true, false), |app| app.focus() == Focus::SearchBox),
        ("Control+M switches to Music", |app| press(app, 'M', true, false, false), |app| app.music_mode && app.browse.tabs.len() == 4),
        ("Control+M switches back to Video", |app| press(app, 'M', true, false, false), |app| !app.music_mode && app.browse.tabs.len() >= 8),
        ("F2 opens Settings", |app| press_code(app, WXK_F2, false, false, false), |app| app.settings_window.is_some() && !app.ui.frame.is_enabled()),
        ("closing Settings returns to the main window", App::close_settings, |app| app.settings_window.is_none() && app.ui.frame.is_enabled()),
        ("Control+5 then Import and export opens Settings at Import", |app| {
            press(app, '5', true, false, false);
            app.open_import_settings();
        }, |app| app.settings_window.as_ref().and_then(|w| w.page()) == Some(7) && app.ui.import_export.is_shown()),
        ("closing Settings again returns to the main window", App::close_settings, |app| app.settings_window.is_none() && app.ui.frame.is_enabled()),
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
        use windows::Win32::UI::Input::KeyboardAndMouse::{GetFocus, VK_DOWN, VK_HOME, VK_TAB};
        use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_KEYDOWN};
        let vk = match code {
            WXK_DOWN => VK_DOWN.0,
            WXK_HOME => VK_HOME.0,
            WXK_TAB => VK_TAB.0,
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

/// Logs where focus is, for following the Tab order; always true.
fn log_stop(app: &App, stop: usize) -> bool {
    log::info!("Self-check: tab stop {stop}: {} ({:?})", describe_focus(), app.focus());
    true
}

#[cfg(windows)]
fn focused_window() -> usize {
    // SAFETY: a plain Win32 query on this thread.
    unsafe { windows::Win32::UI::Input::KeyboardAndMouse::GetFocus().0 as usize }
}

#[cfg(windows)]
fn window_text(hwnd: windows::Win32::Foundation::HWND) -> (String, String) {
    use windows::Win32::UI::WindowsAndMessaging::{GetClassNameW, GetWindowTextW};
    let mut class = [0u16; 128];
    let mut text = [0u16; 256];
    // SAFETY: valid buffers for plain Win32 queries.
    let (c, t) = unsafe { (GetClassNameW(hwnd, &mut class), GetWindowTextW(hwnd, &mut text)) };
    (String::from_utf16_lossy(&class[..c.max(0) as usize]), String::from_utf16_lossy(&text[..t.max(0) as usize]))
}

/// The focused window's class and text, and its parent's, for the log.
#[cfg(windows)]
fn describe_focus() -> String {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::UI::WindowsAndMessaging::GetParent;
    let focused = HWND(focused_window() as *mut std::ffi::c_void);
    if focused.is_invalid() {
        return "nothing".into();
    }
    let (class, text) = window_text(focused);
    // SAFETY: a plain Win32 query.
    let parent = unsafe { GetParent(focused) }.ok().map(window_text).map(|(c, t)| format!("{c} \"{t}\"")).unwrap_or_default();
    format!("{class} \"{text}\" in {parent}")
}

#[cfg(not(windows))]
fn describe_focus() -> String {
    String::new()
}

/// Whether focus sits on a plain wx panel with no children (what screen readers call "panel").
#[cfg(windows)]
fn focused_is_empty_panel() -> bool {
    use windows::Win32::Foundation::HWND;
    use windows::Win32::UI::WindowsAndMessaging::{GW_CHILD, GetWindow};
    let focused = HWND(focused_window() as *mut std::ffi::c_void);
    let (class, _) = window_text(focused);
    // SAFETY: a plain Win32 query.
    let has_child = unsafe { GetWindow(focused, GW_CHILD) }.is_ok_and(|c| !c.is_invalid());
    class.starts_with("wxWindow") && !has_child
}

#[cfg(not(windows))]
fn focused_is_empty_panel() -> bool {
    false
}

pub fn start(frame: &Frame) {
    if !enabled() {
        return;
    }
    if video_mode() {
        start_video_check(frame);
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
