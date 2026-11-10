//! Starting TimePlayer, in 1.x's order (App.OnStartup, then MainWindow_Loaded): one TimePlayer at a time,
//! the language, the NVDA add-on, yt-dlp's update, links from other apps, the window, then "Press F1 for
//! help", the run count, the braille question, the subscriptions check and the update check.

use timeplayer_core::config::Settings;
use timeplayer_core::i18n::{t, use_language};
use timeplayer_core::{braille, community, updates};
use wxdragon::prelude::*;

use crate::window::Ui;
use crate::{App, APP, dialogs, help, instance, link_registration, nvda, speech, with_app, with_app_value};

/// The paused position blinks on the braille line at this pace.
const BLINK_MS: i32 = 500;
/// The subscriptions and update checks wait until the startup speech is over.
const STARTUP_CHECKS_MS: i32 = 4000;

pub fn start(link: Option<String>) {
    let settings = Settings::load();
    use_language(&settings.with(|c| c.language.clone()));
    // One TimePlayer at a time: a second start sends its link to the open one (or brings it forward) and ends.
    let single = match instance::start(link.as_deref()) {
        instance::Start::First(guard) => guard,
        instance::Start::AnotherIsOpen => return,
    };
    std::mem::forget(single);
    log::info!(
        "TimePlayer {} starting up ({}).",
        timeplayer_core::paths::version(),
        if timeplayer_core::paths::is_portable() { "portable" } else { "installed" }
    );
    timeplayer_core::account::remove_leftover_cookie_files();
    signin_forget_browser();
    let use_addon = settings.with(|c| c.accessibility.use_braille_addon);
    if use_addon == Some(true) || (use_addon.is_none() && braille::addon_installed()) {
        let _ = braille::install_addon();
    }
    if let Err(e) = link_registration::apply(settings.with(|c| c.open_links_from_other_apps)) {
        log::warn!("Could not register timeplayer:// links: {e}");
    }

    let frame = Frame::builder().with_title("TimePlayer").with_size(Size::new(1100, 760)).build();
    wxdragon::app::set_top_window(&frame);
    let ui = Ui::build(&frame);
    speech::init(&ui.root);
    let mut app = App::new(&frame, ui, settings);

    // yt-dlp is checked and updated in the background, as 1.x did at every start.
    let ytdlp = app.sources.ytdlp.clone();
    let update_ytdlp = app.settings.with(|c| c.backend.yt_dlp_auto_update_on_startup);
    app.jobs.run("yt-dlp", move || {
        log::info!("Initiating startup check and update for yt-dlp...");
        if let Err(e) = ytdlp.ensure_ready() {
            log::warn!("Background yt-dlp check and update encountered an error: {}", e.message);
        } else if update_ytdlp {
            ytdlp.update();
        }
        |_: &mut App| {}
    });

    bind_frame(&frame);
    app.browse.tabs = app.section_tabs();
    app.ui.set_section_tabs(&app.browse.tabs);
    app.apply_sound();
    app.coordinator.engine().set_video_enabled(!app.music_mode);
    let (device, boost) = app.settings.with(|c| (c.playback.audio_device.clone(), c.playback.volume_boost));
    app.coordinator.engine().set_audio_device(if device.trim().is_empty() { "auto" } else { &device });
    app.coordinator.engine().set_max_volume(if boost { 200.0 } else { 100.0 });
    app.update_title();

    frame.show(true);
    // mpv draws into the video area; it starts in the background so the first video starts sooner.
    let video_handle = app.ui.video.get_handle() as i64;
    let ytdlp_path = app.sources.ytdlp.executable();
    app.coordinator.engine().attach(video_handle, Some(ytdlp_path));
    let engine = app.coordinator.engine().clone();
    let volume = app.playing.volume;
    app.jobs.run("mpv", move || {
        let started = engine.start();
        if started.is_ok() {
            let _ = engine.set_volume(volume);
        }
        move |app: &mut App| match started {
            Ok(()) => {
                app.playing.engine_ready = true;
                if let Some(link) = app.waiting_link.take() {
                    app.open_link_from_other_app(&link);
                }
            }
            Err(e) => {
                log::error!("Failed to initialize playback engine: {}", e.message);
                speech::say_error(&t("Failed to initialize video engine. Please check dependencies."));
                app.show_problem_text(&timeplayer_core::i18n::tf("Player initialization failed: {0}", &[&e.message]), false);
            }
        }
    });
    nvda::set_sender(app.jobs.sender());
    nvda::attach(&frame);
    instance::listen(app.jobs.sender());

    app.ui.focus_search();
    app.load_start_section_quietly();
    speech::say_quietly(&help::startup());
    if let Some(code) = updates::take_install_failure() {
        speech::say_error(&timeplayer_core::i18n::tf(
            "The update didn't install (installer code {0}). Close every TimePlayer window, open TimePlayer again and check for updates.",
            &[&code],
        ));
    }
    if let Some(link) = link {
        app.waiting_link = Some(link);
    }
    APP.with(|cell| *cell.borrow_mut() = Some(app));

    // After the window is up: the braille question and the run count, then the checks.
    let later = Timer::new(&frame);
    later.on_tick(|_| with_app(App::after_startup));
    later.start(300, true);
    std::mem::forget(later);
    let checks = Timer::new(&frame);
    checks.on_tick(|_| with_app(App::startup_checks));
    checks.start(STARTUP_CHECKS_MS, true);
    std::mem::forget(checks);
    crate::selfcheck::start(&frame);
    let blink = Timer::new(&frame);
    blink.on_tick(|_| with_app(App::braille_blink));
    blink.start(BLINK_MS, false);
    std::mem::forget(blink);
}

fn signin_forget_browser() {
    std::thread::spawn(crate::signin::forget_browser);
}

fn bind_frame(frame: &Frame) {
    frame.on_char_hook(|event| {
        let WindowEventData::Keyboard(ref key) = event else { return };
        let handled = with_app_value(|app| app.key(key)).unwrap_or(false);
        event.skip(!handled);
    });
    frame.on_menu_selected(|event| {
        let id = event.get_id();
        if with_app_value(|app| app.menu(id)).is_none() {
            dialogs::remember_pick(id);
        }
    });
    frame.on_close(|event| {
        with_app(App::shutting_down);
        event.skip(true);
    });
}

impl App {
    fn after_startup(&mut self) {
        if self.settings.with(|c| c.accessibility.use_braille_addon.is_none()) && !braille::addon_installed() && braille::nvda_config_folder().is_some() {
            let install = dialogs::ask(
                &self.ui.frame,
                &t("Braille display support"),
                &(t("Do you use a braille display with NVDA?\n\n")
                    + &t("TimePlayer can add a small NVDA add-on that shows the playback position on your braille display ")
                    + &t("and lets the routing keys jump to any point in the video. NVDA needs to be restarted once after installing.\n\n")
                    + &t("Choose Yes to install it, or No if you do not use braille. You will not be asked again; this can be changed in Settings.")),
            );
            self.apply_braille_addon_choice(install);
            self.ui.focus_search();
        }
        self.count_this_run();
        if let Some(link) = self.waiting_link.take() {
            if self.playing.engine_ready {
                self.open_link_from_other_app(&link);
            } else {
                self.waiting_link = Some(link);
            }
        }
        self.read_replies();
    }

    fn startup_checks(&mut self) {
        self.check_feed_in_background();
        if self.settings.with(|c| c.update.check_at_startup) {
            self.check_for_app_update(false);
        }
    }

    fn count_this_run(&mut self) {
        let mine = community::record_run(&self.settings);
        if self.settings.with(|c| c.runs.share.is_none()) {
            let share = dialogs::ask(
                &self.ui.frame,
                &t("Count of runs"),
                &(t("Add your runs to the total for everyone?\n\n")
                    + &t("Each time TimePlayer starts, it can add 1 to a public count of how many times everyone has run it, and tell you that total. ")
                    + &t("Nothing else is sent: not your name, your computer or what you watch.\n\n")
                    + &t("You will not be asked again; this can be changed in Settings, Updates and help.")),
            );
            self.settings.update(|c| c.runs.share = Some(share));
            self.ui.focus_search();
        }
        let http = self.http.clone();
        let settings = self.settings.clone();
        self.jobs.run("run count", move || {
            let everyone = community::send_run(&http, &settings);
            move |app: &mut App| {
                if app.settings.with(|c| c.runs.announce) {
                    speech::say_quietly(&community::runs_spoken(mine, everyone));
                }
            }
        });
    }

    /// Installs or removes the NVDA braille add-on, as the startup question or Settings chose.
    pub(crate) fn apply_braille_addon_choice(&mut self, install: bool) {
        if install {
            let result = braille::install_addon();
            speech::say_quietly(&match result {
                braille::AddonResult::Failed => t("The braille add-on could not be installed."),
                braille::AddonResult::NvdaNotFound => t("NVDA was not found, so the braille add-on was not installed."),
                braille::AddonResult::AlreadyCurrent => t("The braille add-on is installed."),
                braille::AddonResult::InstalledRestartRequired => t("Braille add-on installed. Restart NVDA once to use it."),
            });
            self.settings.update(|c| {
                c.accessibility.use_braille_addon = Some(true);
                c.accessibility.enable_braille_progress = true;
            });
        } else {
            let was_installed = braille::addon_installed();
            braille::uninstall_addon();
            self.settings.update(|c| {
                c.accessibility.use_braille_addon = Some(false);
                c.accessibility.enable_braille_progress = false;
            });
            if was_installed {
                speech::say_quietly(&t("Braille add-on removed. It stops after NVDA restarts."));
            }
        }
        self.update_braille();
    }

    /// A link from a browser or another app: TimePlayer comes forward and opens it.
    pub(crate) fn open_link_from_other_app(&mut self, link: &str) {
        crate::platform::bring_to_front(&self.ui.frame);
        if !self.playing.engine_ready {
            self.waiting_link = Some(link.to_owned());
            return;
        }
        if !self.open_link(link) {
            log::info!("Ignored a link from another app that isn't a YouTube video or playlist.");
        }
    }

    /// The window is closing: progress saved, mpv stopped, the add-on's files taken away.
    fn shutting_down(&mut self) {
        log::info!("TimePlayer application shutting down.");
        crate::jobs::window_closing();
        if self.playing.session_open {
            let position = self.playing.position;
            if let Some(current) = &self.playing.current
                && self.settings.with(|c| c.library.record_history)
            {
                self.library.record_progress(current, position, self.playing.duration, true, self.music_mode);
            }
        }
        let volume = self.playing.volume;
        if self.settings.with(|c| (c.playback.default_volume_percent - volume).abs() >= 0.5) {
            self.settings.update(|c| c.playback.default_volume_percent = volume);
        }
        self.library.flush();
        self.coordinator.engine().terminate();
        braille::clear_state();
        braille::clear_window();
        self.download_state.cancel.cancel();
    }
}
