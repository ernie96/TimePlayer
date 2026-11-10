//! TimePlayer 2: TimePlayer rebuilt on wxWidgets (through wxDragon), so the same app runs on Windows, macOS and
//! Linux with each system's own controls. The core (`timeplayer-core`) has the YouTube sources, playback,
//! library and settings; this crate has the windows, keys and speech.
#![windows_subsystem = "windows"]

mod actions;
mod contact;
mod dialogs;
mod help;
mod import_progress;
mod instance;
mod jobs;
mod keys;
mod link_registration;
mod nvda;
mod platform;
mod player;
mod report;
mod sections;
mod settings_window;
mod signin;
mod selfcheck;
mod speech;
mod startup;
mod update_flow;
mod window;

use std::cell::RefCell;
use std::sync::Arc;

use timeplayer_core::account::Account;
use timeplayer_core::config::Settings;
use timeplayer_core::discovery::{Discovery, Playlists, Recommendations, WatchPage};
use timeplayer_core::downloads::Downloads;
use timeplayer_core::http::Http;
use timeplayer_core::i18n::t;
use timeplayer_core::innertube::InnerTube;
use timeplayer_core::invidious::Invidious;
use timeplayer_core::library::Library;
use timeplayer_core::playback::{Coordinator, Sources};
use timeplayer_core::process::SystemRunner;
use timeplayer_core::tuberepair::TubeRepair;
use timeplayer_core::ytdlp::YtDlp;
use wxdragon::prelude::*;

use crate::jobs::{ID_WAKE, Jobs};
use crate::sections::Browse;
use crate::window::Ui;

/// Runs later with the app.
pub type Waiter = Box<dyn FnOnce(&mut App)>;
/// Runs once the next page of the list has loaded, with whether it brought anything.
pub type AfterMore = Box<dyn FnOnce(&mut App, bool)>;

/// What Retry does again.
#[derive(Clone)]
pub enum LastAction {
    Search(String),
    Play(String),
}

/// Everything the window keeps. Lives on the window's thread only.
pub struct App {
    pub ui: Ui,
    pub jobs: Jobs,
    pub settings: Settings,
    pub http: Http,
    pub library: Library,
    pub account: Account,
    pub sources: Sources,
    pub coordinator: Arc<Coordinator>,
    pub discovery: Discovery,
    pub watch_page: WatchPage,
    pub playlists: Playlists,
    pub recommendations: Recommendations,
    pub downloads: Downloads,
    pub browse: Browse,
    pub playing: player::Playing,
    pub download_state: actions::Downloads,
    pub music_mode: bool,
    pub player_visible: bool,
    /// Counts searches; an older one's results are dropped.
    pub searching: u64,
    pub last_action: Option<LastAction>,
    /// Waiting for the subscriptions check that's under way.
    pub feed_waiters: Vec<Waiter>,
    /// Runs once the next page of the list has loaded (with whether it brought anything).
    pub after_more_loaded: Option<AfterMore>,
    /// The row Back selects once its list is there.
    pub reselect_after_load: Option<usize>,
    pub importing: bool,
    pub import_window: Option<import_progress::ImportProgress>,
    /// A link that came before the player was ready.
    pub waiting_link: Option<String>,
    pub updating: bool,
    pub settings_window: Option<settings_window::SettingsWindow>,
    pub sign_in_window: Option<signin::SignInWindow>,
    pub report_window: Option<report::ReportWindow>,
    /// The browser Settings reads a sign-in from.
    pub sign_in_browser: String,
}

thread_local! {
    static APP: RefCell<Option<App>> = const { RefCell::new(None) };
}

/// Runs `f` with the app, unless it's already busy (an event raised from inside another handler).
pub fn with_app(f: impl FnOnce(&mut App)) {
    with_app_value(f);
}

/// Runs `f` on the app and gives back what it returns; None when the app is busy (a re-entrant event) or `f`
/// failed. A failure is logged and said, and TimePlayer carries on, as 1.x did with an unexpected error.
pub fn with_app_value<R>(f: impl FnOnce(&mut App) -> R) -> Option<R> {
    APP.with(|app| {
        let Ok(mut app) = app.try_borrow_mut() else { return None };
        let app = app.as_mut()?;
        match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| f(app))) {
            Ok(value) => Some(value),
            Err(_) => {
                speech::say_error(&t("Something went wrong. Details are in the log."));
                None
            }
        }
    })
}

fn main() {
    // The browser extension starts TimePlayer as its native messaging host: answer and end, no window.
    let args: Vec<String> = std::env::args().skip(1).collect();
    if link_registration::is_native_host_start(&args) {
        link_registration::run_native_host(&args);
        return;
    }
    // The self-check (debug builds) works in a data folder of its own, so it never touches the user's library.
    selfcheck::prepare();
    SystemOptions::set_option_by_int("msw.no-manifest-check", 1);
    timeplayer_core::logging::init();
    // What went wrong, and where, goes in the log; with_app says so and carries on.
    std::panic::set_hook(Box::new(|info| log::error!("Unexpected error: {info}")));
    let link = args.iter().find_map(|a| timeplayer_core::links::normalize_incoming(a));
    let _ = wxdragon::main(move |_| {
        if let Err(e) = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| startup::start(link))) {
            log::error!("TimePlayer couldn't start: {e:?}");
            let frame = Frame::builder().with_title("TimePlayer").with_size(Size::new(1, 1)).build();
            dialogs::warning(&frame, &t("TimePlayer couldn't start. Details are in the log."), "TimePlayer");
            frame.close(true);
        }
    });
    // Everything was saved when the window closed. The app is let go without being dropped: at exit Windows runs
    // this thread's destructors after the other threads have ended, and dropping the HTTP client then would wait
    // for its worker thread, which is already gone, and abort.
    APP.with(|app| std::mem::forget(app.borrow_mut().take()));
}

impl App {
    /// Builds the services the way 1.x's container did.
    pub fn new(frame: &Frame, ui: Ui, settings: Settings) -> App {
        let jobs = Jobs::new(frame);
        let http = Http::new();
        let library = Library::open();
        let account = Account::open(settings.clone());
        let runner: Arc<dyn timeplayer_core::process::Runner> = Arc::new(SystemRunner);
        let ytdlp = YtDlp::new(http.clone(), settings.clone(), runner, Some(account.clone()));
        let invidious = Invidious::new(http.clone(), settings.clone());
        let inner_tube = InnerTube::new(http.clone(), settings.clone(), Some(account.clone()));
        let sources = Sources {
            tube_repair: TubeRepair::new(http.clone(), settings.clone()),
            invidious: invidious.clone(),
            inner_tube: inner_tube.clone(),
            ytdlp: ytdlp.clone(),
            account: Some(account.clone()),
        };
        let sender = jobs.sender();
        let coordinator = Coordinator::new(
            sources.clone(),
            settings.clone(),
            http.clone(),
            Arc::new(move |event| sender.send(move |app: &mut App| app.on_playback_event(event))),
        );
        let discovery = Discovery::new(http.clone(), inner_tube.clone(), invidious.clone());
        let watch_page = WatchPage::new(inner_tube.clone(), invidious.clone());
        let playlists = Playlists::new(http.clone(), inner_tube.clone(), invidious.clone());
        let recommendations = Recommendations::new(library.clone(), watch_page.clone(), discovery.clone());
        let downloads = Downloads::new(ytdlp, settings.clone(), Some(account.clone()));
        let volume = settings.with(|c| c.playback.default_volume_percent);
        let music_mode = settings.with(|c| c.audio.music_mode);
        let mut browse = Browse::default();
        browse.feed = library.cached_feed();
        App {
            ui,
            jobs,
            settings,
            http,
            library,
            account,
            sources,
            coordinator,
            discovery,
            watch_page,
            playlists,
            recommendations,
            downloads,
            browse,
            playing: player::Playing::new(volume),
            download_state: actions::Downloads::default(),
            music_mode,
            player_visible: false,
            searching: 0,
            last_action: None,
            feed_waiters: Vec::new(),
            after_more_loaded: None,
            reselect_after_load: None,
            importing: false,
            import_window: None,
            waiting_link: None,
            updating: false,
            settings_window: None,
            sign_in_window: None,
            report_window: None,
            sign_in_browser: "firefox".into(),
        }
    }

    /// A menu command: the wake-up from a background task, or a pop-up menu's choice.
    pub fn menu(&mut self, id: i32) {
        if id != ID_WAKE {
            return;
        }
        self.read_replies();
    }

    /// Runs what background tasks found.
    pub fn read_replies(&mut self) {
        for reply in self.jobs.take() {
            reply(self);
        }
        self.apply_reselect();
    }

    /// After a button: the same as its key, then what came back while a dialog was open.
    pub fn after_button(&mut self, f: fn(&mut App)) {
        f(self);
        self.read_replies();
    }

    // ----- The problem panel -----

    /// Says a problem and shows it with Retry and Dismiss (1.x's ShowProblem).
    pub fn show_problem(&mut self, what: &str, reason: Option<&str>, can_retry: bool) {
        let why = reason
            .map(str::trim)
            .filter(|r| !r.is_empty())
            .map(str::to_owned)
            .unwrap_or_else(|| t("No reason was given. The log (Settings, Updates and help, Open log folder) may say more."));
        let message = timeplayer_core::i18n::tf("{0}: {1}", &[&what, &why]);
        self.show_problem_text(&message, can_retry);
    }

    /// Shows a problem already worded, with Retry when it can be tried again.
    pub fn show_problem_text(&mut self, message: &str, can_retry: bool) {
        log::warn!("Shown to the user: {message}");
        self.ui.error_text.set_value(message);
        self.ui.retry.show(can_retry);
        self.ui.error_panel.show(true);
        // The problem panel lives in the list's view; in the player the message is said and stays there.
        self.ui.library.layout();
        speech::say(&timeplayer_core::i18n::tf("{0} F6 reaches Retry and Dismiss.", &[&message]));
    }

    pub fn clear_error(&mut self) {
        if self.ui.error_panel.is_shown() {
            self.ui.error_panel.show(false);
            self.ui.library.layout();
        }
    }

    /// Dismiss.
    pub fn dismiss_error(&mut self) {
        self.clear_error();
        if self.player_visible {
            self.ui.play.set_focus();
        } else {
            self.ui.focus_list();
        }
    }

    /// Retry: the last play or search again.
    pub fn retry_last_action(&mut self) {
        self.clear_error();
        match self.last_action.clone() {
            Some(LastAction::Play(target)) => {
                speech::say_quietly(&t("Retrying playback..."));
                self.play_direct_url(&target);
            }
            Some(LastAction::Search(query)) => {
                speech::say_quietly(&timeplayer_core::i18n::tf("Retrying search for '{0}'...", &[&query]));
                self.ui.search_box.set_value(&query);
                self.search();
            }
            None => {}
        }
    }
}
