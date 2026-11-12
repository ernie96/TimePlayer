//! Settings (F2), 1.x's SettingsWindow: eight pages, every change applied and saved at once. The window keeps
//! the main window disabled while it's open, as 1.x's dialog did, but isn't run as a modal loop, so what
//! background tasks find (a sign-in, diagnostics) still reaches it.

use timeplayer_core::audio::{self, BAND_FREQUENCIES, MAX_BAND_GAIN};
use timeplayer_core::community::SegmentCategory;
use timeplayer_core::config::{AccountUse, QualityPreference, SegmentAction};
use timeplayer_core::i18n::{LANGUAGES, plural, t, tf};
use wxdragon::prelude::*;

use crate::window::wx_label;
use crate::{App, dialogs, help, speech, with_app};

/// The Import page's place among the pages.
const IMPORT_PAGE: usize = 7;
const SMALL_SEEKS: [f64; 5] = [2.0, 5.0, 10.0, 15.0, 30.0];
const LARGE_SEEKS: [f64; 5] = [15.0, 30.0, 60.0, 120.0, 300.0];
const VOLUME_STEPS: [f64; 4] = [1.0, 2.0, 5.0, 10.0];
const QUALITIES: [QualityPreference; 6] = [QualityPreference::Auto, QualityPreference::P1080, QualityPreference::P720, QualityPreference::P480, QualityPreference::P360, QualityPreference::AudioOnly];
const CELL_COUNTS: [u32; 12] = [12, 14, 16, 18, 20, 24, 32, 40, 60, 70, 80, 84];
const SEGMENT_ACTIONS: [SegmentAction; 3] = [SegmentAction::Skip, SegmentAction::Announce, SegmentAction::Off];
const ACCOUNT_USES: [AccountUse; 3] = [AccountUse::Off, AccountUse::RestrictedOnly, AccountUse::Full];
const SIGN_IN_BROWSERS: [(&str, &str); 6] = [("firefox", "Firefox"), ("chrome", "Chrome"), ("edge", "Edge"), ("brave", "Brave"), ("opera", "Opera"), ("vivaldi", "Vivaldi")];
const SHARE_KINDS: [&str; 3] = ["ask", "youtube", "timeplayer"];
pub const BROWSER_BOOKMARK: &str = "javascript:location.href='timeplayer://open?url='+encodeURIComponent(location.href)";

/// The regions YouTube has charts for, with their English names (1.x listed .NET's regions).
const REGIONS: &[(&str, &str)] = &[
    ("DZ", "Algeria"), ("AR", "Argentina"), ("AU", "Australia"), ("AT", "Austria"), ("AZ", "Azerbaijan"), ("BH", "Bahrain"), ("BD", "Bangladesh"),
    ("BY", "Belarus"), ("BE", "Belgium"), ("BO", "Bolivia"), ("BA", "Bosnia and Herzegovina"), ("BR", "Brazil"), ("BG", "Bulgaria"), ("KH", "Cambodia"),
    ("CA", "Canada"), ("CL", "Chile"), ("CO", "Colombia"), ("CR", "Costa Rica"), ("HR", "Croatia"), ("CY", "Cyprus"), ("CZ", "Czechia"), ("DK", "Denmark"),
    ("DO", "Dominican Republic"), ("EC", "Ecuador"), ("EG", "Egypt"), ("SV", "El Salvador"), ("EE", "Estonia"), ("FI", "Finland"), ("FR", "France"),
    ("GE", "Georgia"), ("DE", "Germany"), ("GH", "Ghana"), ("GR", "Greece"), ("GT", "Guatemala"), ("HN", "Honduras"), ("HK", "Hong Kong SAR"),
    ("HU", "Hungary"), ("IS", "Iceland"), ("IN", "India"), ("ID", "Indonesia"), ("IQ", "Iraq"), ("IE", "Ireland"), ("IL", "Israel"), ("IT", "Italy"),
    ("JM", "Jamaica"), ("JP", "Japan"), ("JO", "Jordan"), ("KZ", "Kazakhstan"), ("KE", "Kenya"), ("KR", "Korea"), ("KW", "Kuwait"), ("LA", "Laos"),
    ("LV", "Latvia"), ("LB", "Lebanon"), ("LY", "Libya"), ("LI", "Liechtenstein"), ("LT", "Lithuania"), ("LU", "Luxembourg"), ("MY", "Malaysia"),
    ("MT", "Malta"), ("MX", "Mexico"), ("MD", "Moldova"), ("ME", "Montenegro"), ("MA", "Morocco"), ("NP", "Nepal"), ("NL", "Netherlands"),
    ("NZ", "New Zealand"), ("NI", "Nicaragua"), ("NG", "Nigeria"), ("MK", "North Macedonia"), ("NO", "Norway"), ("OM", "Oman"), ("PK", "Pakistan"),
    ("PA", "Panama"), ("PG", "Papua New Guinea"), ("PY", "Paraguay"), ("PE", "Peru"), ("PH", "Philippines"), ("PL", "Poland"), ("PT", "Portugal"),
    ("PR", "Puerto Rico"), ("QA", "Qatar"), ("RO", "Romania"), ("RU", "Russia"), ("SA", "Saudi Arabia"), ("SN", "Senegal"), ("RS", "Serbia"),
    ("SG", "Singapore"), ("SK", "Slovakia"), ("SI", "Slovenia"), ("ZA", "South Africa"), ("ES", "Spain"), ("LK", "Sri Lanka"), ("SE", "Sweden"),
    ("CH", "Switzerland"), ("TW", "Taiwan"), ("TZ", "Tanzania"), ("TH", "Thailand"), ("TN", "Tunisia"), ("TR", "Türkiye"), ("UG", "Uganda"),
    ("UA", "Ukraine"), ("AE", "United Arab Emirates"), ("GB", "United Kingdom"), ("US", "United States"), ("UY", "Uruguay"), ("VE", "Venezuela"),
    ("VN", "Vietnam"), ("YE", "Yemen"), ("ZW", "Zimbabwe"),
];

const SEGMENT_CATEGORIES: [(SegmentCategory, &str); 8] = [
    (SegmentCategory::Sponsor, "_Sponsor (paid promotion)"),
    (SegmentCategory::SelfPromotion, "Unpaid or _self-promotion"),
    (SegmentCategory::Interaction, "Reminders to _like or subscribe"),
    (SegmentCategory::Intro, "_Intro animation or opening"),
    (SegmentCategory::Outro, "_Outro and end cards"),
    (SegmentCategory::Preview, "_Preview or recap"),
    (SegmentCategory::Filler, "_Filler and tangents"),
    (SegmentCategory::NonMusic, "_Non-music parts of music videos"),
];

fn seconds_label(value: f64) -> String {
    if value >= 60.0 && value % 60.0 == 0.0 { plural((value / 60.0) as i64, "{0} minute", "{0} minutes") } else { plural(value as i64, "{0} second", "{0} seconds") }
}

fn nearest(choices: &[f64], value: f64) -> usize {
    choices.iter().enumerate().min_by(|a, b| (a.1 - value).abs().total_cmp(&(b.1 - value).abs())).map(|(i, _)| i).unwrap_or(0)
}

fn quality_label(q: QualityPreference) -> String {
    match q {
        QualityPreference::Auto => t("Automatic"),
        QualityPreference::P1080 => t("1080p, full HD"),
        QualityPreference::P720 => t("720p, HD"),
        QualityPreference::P480 => "480p".into(),
        QualityPreference::P360 => t("360p, saves data"),
        QualityPreference::AudioOnly => t("Audio only"),
    }
}

fn language_label(code: &str, name: &str, english: &str) -> String {
    let here = t(english);
    if code.is_empty() {
        here
    } else if here.to_lowercase() == name.to_lowercase() {
        name.to_owned()
    } else {
        format!("{name} ({here})")
    }
}

fn preset_description(name: &str) -> String {
    if name == audio::CUSTOM { t("Your own settings, below.") } else { audio::find_preset(name).map(|p| t(p.description)).unwrap_or_default() }
}

fn segment_action(settings: &timeplayer_core::config::SponsorBlockConfig, category: SegmentCategory) -> SegmentAction {
    timeplayer_core::community::action_for(settings, category)
}

fn set_segment_action(settings: &mut timeplayer_core::config::SponsorBlockConfig, category: SegmentCategory, action: SegmentAction) {
    match category {
        SegmentCategory::Sponsor => settings.sponsor = action,
        SegmentCategory::SelfPromotion => settings.self_promotion = action,
        SegmentCategory::Interaction => settings.interaction = action,
        SegmentCategory::Intro => settings.intro = action,
        SegmentCategory::Outro => settings.outro = action,
        SegmentCategory::Preview => settings.preview = action,
        SegmentCategory::Filler => settings.filler = action,
        SegmentCategory::NonMusic => settings.non_music = action,
        _ => {}
    }
}

/// The browser extension ships next to TimePlayer.
fn extension_folder() -> std::path::PathBuf {
    timeplayer_core::paths::program_folder().join("browser-extension")
}

pub struct SettingsWindow {
    dialog: Dialog,
    pages: Notebook,
    page_names: Vec<String>,
    music_mode: CheckBox,
    video_preset: Choice,
    music_preset: Choice,
    video_description: StaticText,
    music_description: StaticText,
    bands: Vec<Slider>,
    bass: Slider,
    clarity: Slider,
    width: Slider,
    room: Choice,
    crossfeed: CheckBox,
    level_volume: CheckBox,
    compressor: CheckBox,
    voice_focus: CheckBox,
    use_addon: CheckBox,
    braille_progress: CheckBox,
    account_status: StaticText,
    dependency_status: StaticText,
    diagnostics: TextCtrl,
    help_line: StaticText,
    audio_device: Choice,
    devices: Vec<(String, String)>,
    loading_custom: std::cell::Cell<bool>,
}

/// Lays out one page: labels before their controls, in Tab order.
struct Page {
    panel: ScrolledWindow,
    sizer: BoxSizer,
}

impl Page {
    fn new(pages: &Notebook) -> Self {
        let panel = ScrolledWindow::builder(pages).with_style(ScrolledWindowStyle::VScroll).build();
        panel.set_scroll_rate(0, 20);
        Self { panel, sizer: BoxSizer::builder(Orientation::Vertical).build() }
    }

    fn text(&self, text: &str) -> StaticText {
        let label = StaticText::builder(&self.panel).with_label(&text.replace('&', "&&")).build();
        label.wrap(620);
        self.sizer.add(&label, 0, SizerFlag::Expand | SizerFlag::All, 6);
        label
    }

    fn check(&self, label: &str, value: bool, changed: impl Fn(&mut App, bool) + 'static) -> CheckBox {
        let check = CheckBox::builder(&self.panel).with_label(&t(label).replace('&', "&&")).build();
        check.set_value(value);
        check.on_toggled(move |e| {
            let on = e.is_checked();
            with_app(|app| changed(app, on));
        });
        self.sizer.add(&check, 0, SizerFlag::All, 6);
        check
    }

    fn choice(&self, label: &str, name: &str, items: Vec<String>, selected: usize, changed: impl Fn(&mut App, usize) + 'static) -> Choice {
        let row = BoxSizer::builder(Orientation::Horizontal).build();
        let text = StaticText::builder(&self.panel).with_label(&wx_label(label)).build();
        let choice = Choice::builder(&self.panel).with_choices(items).build();
        choice.set_name(&t(name));
        choice.set_selection(selected as u32);
        choice.on_selection_changed(move |e| {
            if let Some(index) = e.get_selection() {
                with_app(|app| changed(app, index as usize));
            }
        });
        row.add(&text, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 8);
        row.add(&choice, 0, SizerFlag::All, 0);
        self.sizer.add_sizer(&row, 0, SizerFlag::All, 6);
        choice
    }

    fn slider(&self, label: &str, name: &str, min: i32, max: i32, value: i32, changed: impl Fn(&mut App, i32) + 'static) -> Slider {
        let row = BoxSizer::builder(Orientation::Horizontal).build();
        let text = StaticText::builder(&self.panel).with_label(&label.replace('&', "&&")).build();
        let slider = Slider::builder(&self.panel).with_value(value).with_min_value(min).with_max_value(max).with_size(Size::new(260, -1)).build();
        slider.set_name(name);
        slider.on_slider(move |e| {
            let value = e.get_value();
            with_app(|app| changed(app, value));
        });
        row.add(&text, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 8);
        row.add(&slider, 0, SizerFlag::All, 0);
        self.sizer.add_sizer(&row, 0, SizerFlag::All, 6);
        slider
    }

    fn button(&self, label: &str, clicked: fn(&mut App)) -> Button {
        let button = Button::builder(&self.panel).with_label(&wx_label(label)).build();
        button.on_click(move |_| with_app(|app| app.after_button(clicked)));
        self.sizer.add(&button, 0, SizerFlag::All, 6);
        button
    }

    fn document(&self, label: &str, name: &str, text: &str, height: i32) -> TextCtrl {
        let caption = StaticText::builder(&self.panel).with_label(&wx_label(label)).build();
        let document = TextCtrl::builder(&self.panel).with_value(text).with_style(TextCtrlStyle::MultiLine | TextCtrlStyle::ReadOnly).with_size(Size::new(-1, height)).build();
        document.set_name(&t(name));
        self.sizer.add(&caption, 0, SizerFlag::Left | SizerFlag::Right | SizerFlag::Top, 6);
        self.sizer.add(&document, 0, SizerFlag::Expand | SizerFlag::All, 6);
        document
    }

    fn finish(self, pages: &Notebook, name: &str) {
        self.panel.set_sizer(self.sizer, true);
        pages.add_page(&self.panel, &t(name), false, None);
    }
}

impl App {
    /// F2, or Settings.
    pub(crate) fn open_settings(&mut self) {
        if let Some(open) = &self.settings_window {
            open.dialog.raise();
            return;
        }
        let window = SettingsWindow::open(self);
        self.ui.frame.enable(false);
        self.settings_window = Some(window);
    }

    pub(crate) fn close_settings(&mut self) {
        if let Some(window) = self.settings_window.take() {
            window.dialog.destroy();
        }
        self.ui.frame.enable(true);
        self.ui.frame.raise();
        if self.player_visible {
            self.ui.play.set_focus();
        } else {
            self.ui.focus_search();
        }
        self.refresh_section_tabs();
    }

    fn settings_help(&self) {
        if let Some(window) = &self.settings_window {
            let page = window.pages.selection();
            let name = usize::try_from(page).ok().and_then(|i| window.page_names.get(i)).cloned().unwrap_or_else(|| t("Settings"));
            let text = help::quick::settings(&name);
            window.help_line.set_label(&text);
            speech::say(&text);
        }
    }

    fn settings_full_help(&self) {
        if let Some(window) = &self.settings_window {
            dialogs::message(
                &window.dialog,
                &(t("Settings has nine pages: Playback, Sound, Speech and braille, Library, SponsorBlock and dislikes, Links and browser, YouTube account, Import, and Updates and help. ")
                    + &t("Control+Tab and Control+Shift+Tab move between pages; on the page names, Left and Right do too.\n")
                    + &t("Tab and Shift+Tab move between settings. Space toggles a check box; Up and Down change a choice. ")
                    + &t("Alt plus the underlined letter jumps to a setting on the current page.\n")
                    + &t("Escape closes and saves.")),
                &t("Help: Settings"),
            );
        }
    }

    // ----- Sound -----

    fn active_preset_changed(&mut self, music: bool, index: usize) {
        let Some(name) = audio::preset_names().get(index).map(|n| n.to_string()) else { return };
        self.settings.update(|c| if music { c.audio.music_preset = name.clone() } else { c.audio.preset = name.clone() });
        self.apply_sound();
        if let Some(window) = &self.settings_window {
            let label = if music { &window.music_description } else { &window.video_description };
            label.set_label(&preset_description(&name));
            window.dialog.layout();
        }
    }

    /// A Custom control changed: Custom becomes the sound playing, as 1.x did.
    fn custom_changed(&mut self, change: impl FnOnce(&mut audio::AudioEffectSettings)) {
        if self.settings_window.as_ref().is_some_and(|w| w.loading_custom.get()) {
            return;
        }
        let music = self.music_mode;
        self.settings.update(|c| {
            change(&mut c.audio.custom);
            if music { c.audio.music_preset = audio::CUSTOM.into() } else { c.audio.preset = audio::CUSTOM.into() }
        });
        self.apply_sound();
        if let Some(window) = &self.settings_window {
            let index = audio::preset_names().iter().position(|n| *n == audio::CUSTOM).unwrap_or(0) as u32;
            let (choice, label) = if music { (&window.music_preset, &window.music_description) } else { (&window.video_preset, &window.video_description) };
            choice.set_selection(index);
            label.set_label(&preset_description(audio::CUSTOM));
        }
    }

    fn show_custom(&self) {
        let Some(window) = &self.settings_window else { return };
        let custom = self.settings.with(|c| c.audio.custom.clone());
        window.loading_custom.set(true);
        for (i, band) in window.bands.iter().enumerate() {
            band.set_value(custom.gain_at(i).round() as i32);
        }
        window.bass.set_value(custom.bass.round() as i32);
        window.clarity.set_value(custom.clarity.round() as i32);
        window.width.set_value(custom.width.round() as i32);
        window.room.set_selection(custom.room.clamp(0, 3) as u32);
        window.crossfeed.set_value(custom.crossfeed);
        window.level_volume.set_value(custom.level_volume);
        window.compressor.set_value(custom.compressor);
        window.voice_focus.set_value(custom.voice_focus);
        window.loading_custom.set(false);
    }

    /// Start Custom from the current preset.
    fn copy_preset_to_custom(&mut self) {
        let name = self.active_sound_preset();
        let source = audio::find_preset(&name).map(|p| p.settings).unwrap_or_else(|| self.settings.with(|c| c.audio.custom.clone()));
        self.custom_changed(move |custom| *custom = source);
        self.show_custom();
        speech::say_quietly(&tf("Custom now starts from {0}.", &[&t(&name)]));
    }

    /// Reset Custom to flat.
    fn reset_custom_sound(&mut self) {
        self.settings.update(|c| c.audio.custom = audio::AudioEffectSettings::default());
        self.apply_sound();
        self.show_custom();
        speech::say_quietly(&t("Custom sound reset to flat."));
    }

    /// The Music mode check box: the same as Control+M.
    fn settings_music_mode(&mut self, on: bool) {
        if on != self.music_mode {
            self.toggle_music_mode();
        }
    }

    // ----- Links and browser -----

    fn open_links_changed(&mut self, on: bool) {
        if let Err(e) = crate::link_registration::apply(on) {
            log::warn!("Could not change the timeplayer:// link registration: {e}");
        }
        self.settings.update(|c| c.open_links_from_other_apps = on);
    }

    fn register_links_again(&mut self) {
        match crate::link_registration::apply(true) {
            Ok(()) => {
                self.settings.update(|c| c.open_links_from_other_apps = true);
                speech::say(&t("Done. Browsers and other apps can open YouTube links in TimePlayer again."));
            }
            Err(e) => speech::say(&tf("Windows did not allow TimePlayer to register for links: {0}", &[&e])),
        }
    }

    fn open_extension_folder(&mut self) {
        let folder = extension_folder();
        if !folder.is_dir() {
            speech::say(&t("The browser extension isn't included in this copy of TimePlayer. It's on the TimePlayer releases page."));
            return;
        }
        crate::platform::open_folder(&folder);
    }

    fn copy_extension_folder(&mut self) {
        let folder = extension_folder();
        if !folder.is_dir() {
            speech::say(&t("The browser extension isn't included in this copy of TimePlayer. It's on the TimePlayer releases page."));
            return;
        }
        let text = folder.display().to_string();
        if self.copy_to_clipboard(&text) {
            speech::say(&tf("Folder location copied: {0}", &[&text]));
        }
    }

    fn copy_browser_bookmark(&mut self) {
        if Clipboard::get().set_text(BROWSER_BOOKMARK) {
            speech::say(&t("Bookmark copied. In your browser, add a new bookmark named Play in TimePlayer and paste this as its address. On a YouTube page, choose that bookmark to play the page in TimePlayer."));
        } else {
            speech::say(&t("Could not copy to the clipboard. Try again."));
        }
    }

    // ----- YouTube account -----

    fn account_use_changed(&mut self, index: usize) {
        let use_ = ACCOUNT_USES[index.min(2)];
        self.settings.update(|c| c.account.use_ = use_);
        self.show_account_status();
    }

    fn show_account_status(&self) {
        if let Some(window) = &self.settings_window {
            window.account_status.set_label(&self.account_status());
            window.dialog.layout();
        }
    }

    fn import_sign_in_file(&mut self) {
        let parent = self.settings_window.as_ref().map(|w| w.dialog);
        let Some(parent) = parent else { return };
        let filter = t("Sign-in files (*.txt)|*.txt|All files|*.*");
        let Some(path) = dialogs::open_file(&parent, &t("Import a sign-in file"), &filter) else { return };
        let result = self.account.import_file(&path);
        if result.is_ok() && path.file_name().is_some_and(|n| n.to_string_lossy().to_lowercase().starts_with("timeplayer-youtube-signin")) {
            let _ = std::fs::remove_file(&path);
        }
        self.after_sign_in(result.map(|_| ()).map_err(|e| e.message));
        self.show_account_status();
    }

    fn sign_in_from_browser(&mut self) {
        let browser = self.sign_in_browser.clone();
        speech::say_quietly(&t("Reading the sign-in from the browser..."));
        let ytdlp = self.sources.ytdlp.clone();
        let account = self.account.clone();
        self.jobs.run("browser sign-in", move || {
            let result = ytdlp.read_browser_sign_in(&browser).and_then(|text| account.import_cookies(timeplayer_core::account::parse(&text)).map(|_| ()));
            move |app: &mut App| {
                app.after_sign_in(result.map_err(|e| e.message));
                app.show_account_status();
            }
        });
    }

    fn sign_out(&mut self) {
        self.account.sign_out();
        self.refresh_section_tabs();
        self.show_account_status();
        speech::say(&t("Signed out. Your sign-in is erased from this computer."));
    }

    // ----- Updates and help -----

    /// Settings at its Import page (the Import and export button, and the first-start offer).
    pub(crate) fn open_import_settings(&mut self) {
        self.open_settings();
        if let Some(window) = &self.settings_window {
            window.pages.set_selection(IMPORT_PAGE);
            window.pages.set_focus();
        }
    }

    fn settings_sign_in(&mut self) {
        self.close_settings();
        self.sign_in_only = true;
        self.import_by_signing_in();
    }

    fn settings_import_by_signing_in(&mut self) {
        self.close_settings();
        self.import_by_signing_in();
    }

    fn settings_import_takeout(&mut self) {
        self.close_settings();
        self.import_takeout_from_file();
    }

    fn settings_takeout_steps(&mut self) {
        self.close_settings();
        self.show_youtube_data_steps();
    }

    fn settings_import_subscriptions(&mut self) {
        self.close_settings();
        self.import_subscriptions_from_file();
    }

    fn settings_export_subscriptions(&mut self) {
        self.close_settings();
        self.export_subscriptions_to_file();
    }

    fn settings_check_updates(&mut self) {
        self.close_settings();
        self.check_for_app_update(true);
    }

    fn settings_report_problem(&mut self) {
        self.close_settings();
        self.show_report(false);
    }

    fn settings_suggest_feature(&mut self) {
        self.close_settings();
        self.show_report(true);
    }

    fn settings_test_pattern(&mut self) {
        self.close_settings();
        self.load_test_video();
    }

    /// Update the YouTube downloader.
    fn update_downloader(&mut self) {
        let status = t("Checking for updates to yt-dlp and mpv...");
        self.show_dependency_status(&status);
        speech::say_quietly(&status);
        let ytdlp = self.sources.ytdlp.clone();
        self.jobs.run("update yt-dlp", move || {
            let result = ytdlp.ensure_ready().map(|_| ytdlp.update()).and_then(|_| ytdlp.version());
            move |app: &mut App| {
                let status = match result {
                    Ok(version) => tf("yt-dlp is verified and up to date (version {0}). mpv engine is intact.", &[&version]),
                    Err(e) => tf("Update check: {0}", &[&e.message]),
                };
                app.show_dependency_status(&status);
                speech::say(&status);
            }
        });
    }

    fn show_dependency_status(&self, text: &str) {
        if let Some(window) = &self.settings_window {
            window.dependency_status.set_label(text);
            window.dialog.layout();
        }
    }

    /// Run diagnostics: each source, yt-dlp and mpv.
    fn run_diagnostics(&mut self) {
        speech::say_quietly(&t("Running diagnostics..."));
        let http = self.http.clone();
        let settings = self.settings.clone();
        let invidious = self.sources.invidious.clone();
        let ytdlp = self.sources.ytdlp.clone();
        let engine = self.coordinator.engine().clone();
        let state = self.playing.state;
        let engine_ready = self.playing.engine_ready;
        self.jobs.run("diagnostics", move || {
            let mut report = String::from("=== TimePlayer Diagnostics Report ===\n");
            report += &format!("Timestamp: {}\n\n[1] TubeRepair Primary Backend:\n", chrono::Local::now().format("%Y-%m-%d %H:%M:%S"));
            let base = settings.with(|c| c.backend.tube_repair_base_url.clone());
            report += &format!("  - Server URL: {base}\n");
            let ping = timeplayer_core::http::Request::get(format!("{}/feeds/api/videos?q=test&max-results=1", base.trim_end_matches('/'))).timeout(std::time::Duration::from_secs(5));
            report += &match http.send(ping) {
                Ok(r) if r.ok() => format!("  - Health status: Reachable (HTTP {})\n", r.status),
                Ok(r) => format!("  - Health status: HTTP {}\n", r.status),
                Err(e) => format!("  - TubeRepair check error: {}\n", e.message),
            };
            report += "\n[2] Invidious Backend Status:\n";
            report += &format!("  - Discovered instances: {}\n", invidious.instances().len());
            report += &match invidious.select() {
                Some(best) => format!("  - Active / Best instance: {}\n", best.base_uri),
                None => "  - Active instance: None reachable (will fallback)\n".into(),
            };
            report += "\n[3] InnerTube Tertiary Backend:\n";
            report += &format!("  - Base URL: {}\n  - Protocol: Anonymous YouTube InnerTube API v1\n", settings.with(|c| c.backend.inner_tube_base_url.clone()));
            report += "\n[4] Local yt-dlp Executable (Final Fallback):\n";
            report += &format!("  - Path: {}\n  - Installed: {}\n", ytdlp.executable().display(), ytdlp.is_installed());
            if ytdlp.is_installed() {
                report += &format!("  - Version: {}\n", ytdlp.version().unwrap_or_else(|_| "Unknown".into()));
            }
            report += "\n[5] Embedded mpv Engine:\n";
            report += &format!("  - Initialized: {engine_ready}\n  - Engine alive: {}\n  - Playback state: {state:?}\n", engine.is_alive());
            report += "\nDiagnostics completed.";
            move |app: &mut App| {
                if let Some(window) = &app.settings_window {
                    window.diagnostics.set_value(&report);
                }
                speech::say(&t("Diagnostics complete."));
            }
        });
    }

    /// Play test pattern: mpv's own colour bars, to check the video works without YouTube.
    fn load_test_video(&mut self) {
        if !self.playing.engine_ready {
            speech::say(&t("Video engine is not ready."));
            return;
        }
        self.playing.title = t("Built-in Test Pattern (1080p 30fps Color Bars)");
        self.playing.channel = "TimePlayer Built-in Diagnostics".into();
        self.playing.session_open = true;
        if !self.music_mode {
            self.set_player_visible(true);
        }
        self.update_now_playing();
        speech::say(&t("Loading test pattern..."));
        if self.player_visible {
            self.ui.play.set_focus();
        }
        if let Err(e) = self.coordinator.engine().load("av://lavfi:testsrc=duration=600:size=1280x720:rate=30", None, None) {
            self.show_problem_text(&tf("Failed to load test video: {0}", &[&e.message]), false);
        }
    }

    fn open_log_folder(&mut self) {
        crate::platform::open_folder(&timeplayer_core::paths::logs_folder());
    }

    fn sound_output_changed(&mut self, index: usize) {
        let Some((device, label)) = self.settings_window.as_ref().and_then(|w| w.devices.get(index).cloned()) else { return };
        self.choose_audio_device(&device, &label);
    }
}

impl SettingsWindow {
    fn open(app: &App) -> Self {
        let c = app.settings.get();
        let dialog = Dialog::builder(&app.ui.frame, &t("Settings")).with_style(DialogStyle::DefaultDialogStyle | DialogStyle::ResizeBorder).with_size(720, 640).build();
        let top = BoxSizer::builder(Orientation::Vertical).build();
        let version = tf("TimePlayer {0}", &[&timeplayer_core::paths::version()]) + &if timeplayer_core::paths::is_portable() { t(", portable") } else { String::new() };
        let intro = StaticText::builder(&dialog).with_label(&(version + &t(". F1 for help. Changes apply right away and are saved when you close this window."))).build();
        let help_line = StaticText::builder(&dialog).with_label("").build();
        let pages = Notebook::builder(&dialog).build();
        pages.set_name(&t("Settings pages"));
        let mut page_names = Vec::new();

        // Playback
        let p = Page::new(&pages);
        p.check("Continue videos where I left off", c.library.resume_playback, |app, on| app.settings.update(|c| c.library.resume_playback = on));
        p.check("Play audio description when a video has it (D in the player)", c.playback.audio_description, |app, on| app.settings.update(|c| c.playback.audio_description = on));
        p.check("Autoplay a related video when the queue ends (A in the player)", c.library.autoplay, |app, on| app.settings.update(|c| c.library.autoplay = on));
        p.check("Use hardware video decoding (takes effect after restart)", c.playback.hardware_decoding, |app, on| app.settings.update(|c| c.playback.hardware_decoding = on));
        p.choice(
            "Preferred video _quality:",
            "Preferred video quality",
            QUALITIES.iter().map(|q| quality_label(*q)).collect(),
            QUALITIES.iter().position(|q| *q == c.playback.preferred_quality).unwrap_or(0),
            |app, i| app.settings.update(|c| c.playback.preferred_quality = QUALITIES[i]),
        );
        p.choice("_Left and Right arrows move:", "Left and Right arrows move", SMALL_SEEKS.iter().map(|v| seconds_label(*v)).collect(), nearest(&SMALL_SEEKS, c.playback.small_seek_seconds), |app, i| {
            app.settings.update(|c| c.playback.small_seek_seconds = SMALL_SEEKS[i])
        });
        p.choice("Control+Left and Right m_ove:", "Control+Left and Right move", LARGE_SEEKS.iter().map(|v| seconds_label(*v)).collect(), nearest(&LARGE_SEEKS, c.playback.large_seek_seconds), |app, i| {
            app.settings.update(|c| c.playback.large_seek_seconds = LARGE_SEEKS[i])
        });
        p.choice(
            "Up and Down change the _volume by:",
            "Up and Down change the volume by",
            VOLUME_STEPS.iter().map(|v| tf("{0} percent", &[v])).collect(),
            nearest(&VOLUME_STEPS, c.playback.volume_step_percent),
            |app, i| app.settings.update(|c| c.playback.volume_step_percent = VOLUME_STEPS[i]),
        );
        page_names.push(t("Playback"));
        p.finish(&pages, "Playback");

        // Sound
        let p = Page::new(&pages);
        p.text(&t("These effects change TimePlayer's sound only; other apps and Windows sound as before. In the player, E and Shift+E switch presets."));
        let devices = app.audio_device_choices();
        let device_index = devices.iter().position(|(name, _)| *name == c.playback.audio_device).unwrap_or(0);
        let audio_device = p.choice("Play sound _through:", "Play sound through", devices.iter().map(|(_, label)| label.clone()).collect(), device_index, |app, i| app.sound_output_changed(i));
        p.check("Allow volume above 100 percent, up to 200, for quiet videos", c.playback.volume_boost, |app, on| {
            app.settings.update(|c| c.playback.volume_boost = on);
            app.coordinator.engine().set_max_volume(if on { 200.0 } else { 100.0 });
            if !on && app.playing.volume > 100.0 && app.coordinator.engine().set_volume(100.0).is_ok() {
                app.playing.volume = 100.0;
            }
        });
        let music_mode = p.check("Music mode: no video, and the music sound below (Control+M)", app.music_mode, App::settings_music_mode);
        let names = audio::preset_names();
        let preset_labels: Vec<String> = names.iter().map(|n| t(n)).collect();
        let video_preset = p.choice("Sound for _videos:", "Sound for videos", preset_labels.clone(), names.iter().position(|n| *n == c.audio.preset).unwrap_or(0), |app, i| app.active_preset_changed(false, i));
        let video_description = p.text(&preset_description(&c.audio.preset));
        let music_preset = p.choice("Sound in _music mode:", "Sound in music mode", preset_labels, names.iter().position(|n| *n == c.audio.music_preset).unwrap_or(0), |app, i| app.active_preset_changed(true, i));
        let music_description = p.text(&preset_description(&c.audio.music_preset));
        p.text(&t("Custom sound"));
        p.text(&t("Used wherever Custom is chosen above. Changing anything here switches the sound you hear now to Custom. Equalizer sliders go from minus 12 to plus 12 decibels; Left and Right change them by 1, Page Up and Page Down by 3."));
        p.button("Start Custom from the current _preset", App::copy_preset_to_custom);
        p.button("_Reset Custom to flat", App::reset_custom_sound);
        let custom = &c.audio.custom;
        let max = MAX_BAND_GAIN as i32;
        let bands: Vec<Slider> = BAND_FREQUENCIES
            .iter()
            .enumerate()
            .map(|(i, f)| {
                let label = if *f >= 1000 { tf("{0} kilohertz", &[&(f / 1000)]) } else { tf("{0} hertz", &[f]) };
                p.slider(&label, &tf("{0}, decibels", &[&label]), -max, max, custom.gain_at(i).round() as i32, move |app, v| {
                    app.custom_changed(move |s| {
                        if s.equalizer_gains.len() < BAND_FREQUENCIES.len() {
                            s.equalizer_gains.resize(BAND_FREQUENCIES.len(), 0.0);
                        }
                        s.equalizer_gains[i] = v as f64;
                    })
                })
            })
            .collect();
        let bass = p.slider(&t("Bass boost"), &t("Bass boost, decibels"), 0, 12, custom.bass.round() as i32, |app, v| app.custom_changed(move |s| s.bass = v as f64));
        let clarity = p.slider(&t("Clarity"), &t("Clarity, 0 to 10"), 0, 10, custom.clarity.round() as i32, |app, v| app.custom_changed(move |s| s.clarity = v as f64));
        let width = p.slider(&t("Stereo width"), &t("Stereo width, 0 to 10"), 0, 10, custom.width.round() as i32, |app, v| app.custom_changed(move |s| s.width = v as f64));
        let room = p.choice("R_oom:", "Room", vec![t("Off"), t("Small room"), t("Large room"), t("Hall")], custom.room.clamp(0, 3) as usize, |app, i| app.custom_changed(move |s| s.room = i as i32));
        let crossfeed = p.check("Headphone crossfeed (less hard left and right on headphones)", custom.crossfeed, |app, on| app.custom_changed(move |s| s.crossfeed = on));
        let level_volume = p.check("Even out the volume", custom.level_volume, |app, on| app.custom_changed(move |s| s.level_volume = on));
        let compressor = p.check("Compressor: quiet sounds louder, loud sounds softer", custom.compressor, |app, on| app.custom_changed(move |s| s.compressor = on));
        let voice_focus = p.check("Voice focus: cut low rumble", custom.voice_focus, |app, on| app.custom_changed(move |s| s.voice_focus = on));
        page_names.push(t("Sound"));
        p.finish(&pages, "Sound");

        // Speech and braille
        let p = Page::new(&pages);
        let language_index = LANGUAGES.iter().position(|(code, _, _)| *code == c.language).unwrap_or(0);
        p.choice("_Language (after a restart):", "Language (after a restart)", LANGUAGES.iter().map(|(code, name, english)| language_label(code, name, english)).collect(), language_index, |app, i| {
            let (code, name, english) = LANGUAGES[i];
            app.settings.update(|c| c.language = code.to_owned());
            speech::say_quietly(&tf("TimePlayer will use {0} after you restart it.", &[&language_label(code, name, english)]));
        });
        p.check("Read captions aloud as they appear (C in the player)", c.accessibility.speak_captions, |app, on| {
            if app.settings.with(|c| c.accessibility.speak_captions) != on {
                app.toggle_speak_captions();
            }
        });
        p.check("Say each chapter's name when it starts", c.accessibility.announce_chapters, |app, on| app.settings.update(|c| c.accessibility.announce_chapters = on));
        p.check("Read timed lyrics aloud as they're sung (L in the player)", c.accessibility.speak_lyrics, |app, on| app.settings.update(|c| c.accessibility.speak_lyrics = on));
        let use_addon = p.check(
            "Use the NVDA braille add-on (for braille displays; restart NVDA after changing)",
            c.accessibility.use_braille_addon.unwrap_or_else(timeplayer_core::braille::addon_installed),
            |app, on| {
                app.apply_braille_addon_choice(on);
                if let Some(w) = &app.settings_window {
                    w.braille_progress.set_value(on);
                }
            },
        );
        let braille_progress = p.check("Show playback progress on the braille display (Control+Shift+D)", c.accessibility.enable_braille_progress, |app, on| {
            app.settings.update(|c| c.accessibility.enable_braille_progress = on);
            app.update_braille();
        });
        let mut cells: Vec<u32> = CELL_COUNTS.to_vec();
        if !cells.contains(&c.accessibility.braille_display_cell_count) {
            cells.push(c.accessibility.braille_display_cell_count);
        }
        let cell_index = cells.iter().position(|n| *n == c.accessibility.braille_display_cell_count).unwrap_or(7);
        let cell_values = cells.clone();
        p.choice("Braille display _cells, when the add-on isn't running:", "Braille display cells, when the add-on isn't running", cells.iter().map(u32::to_string).collect(), cell_index, move |app, i| {
            let count = cell_values[i];
            app.settings.update(|c| c.accessibility.braille_display_cell_count = count);
            app.update_braille();
        });
        page_names.push(t("Speech and braille"));
        p.finish(&pages, "Speech and braille");

        // Library
        let p = Page::new(&pages);
        p.check("Keep a watch history on this computer (needed for resume and For You)", c.library.record_history, |app, on| app.settings.update(|c| c.library.record_history = on));
        p.check("Check my channels for new videos at startup", c.library.check_feed_on_startup, |app, on| app.settings.update(|c| c.library.check_feed_on_startup = on));
        p.check("Hide Shorts in What's New, Featured and For You", c.library.hide_shorts_in_feed, |app, on| {
            app.settings.update(|c| c.library.hide_shorts_in_feed = on);
            if matches!(app.browse.current(), crate::sections::Section::WhatsNew | crate::sections::Section::Featured) {
                app.show_section(app.browse.current(), false, false);
            }
        });
        let mut regions: Vec<(&str, &str)> = REGIONS.to_vec();
        regions.sort_by_key(|(_, name)| name.to_lowercase());
        let region_index = regions.iter().position(|(code, _)| code.eq_ignore_ascii_case(&c.library.featured_region)).or_else(|| regions.iter().position(|(code, _)| *code == "US")).unwrap_or(0);
        let region_codes: Vec<String> = regions.iter().map(|(code, _)| code.to_string()).collect();
        p.choice("_Region for Featured and the music charts:", "Region for Featured and the music charts", regions.iter().map(|(code, name)| tf("{0} ({1})", &[name, code])).collect(), region_index, move |app, i| {
            let code = region_codes[i].clone();
            if app.settings.with(|c| c.library.featured_region != code) {
                app.browse.clear_featured_cache();
            }
            app.settings.update(|c| c.library.featured_region = code);
        });
        page_names.push(t("Library"));
        p.finish(&pages, "Library");

        // SponsorBlock and dislikes
        let p = Page::new(&pages);
        p.text(&t("SponsorBlock (sponsor.ajay.app) skips or announces parts of videos that its users marked, such as sponsor reads. Only the first characters of a fingerprint of the video's id are sent, so the server can't tell which video you watch. In the player, S skips the segment playing now and H jumps to the highlight."));
        p.check("Use SponsorBlock", c.sponsor_block.enabled, |app, on| app.settings.update(|c| c.sponsor_block.enabled = on));
        let action_labels = vec![t("Skip automatically"), t("Tell me (S skips it)"), t("Do nothing")];
        for (category, label) in SEGMENT_CATEGORIES {
            let current = segment_action(&c.sponsor_block, category);
            let spoken = t(label).replace('_', "");
            p.choice(label, &spoken, action_labels.clone(), SEGMENT_ACTIONS.iter().position(|a| *a == current).unwrap_or(2), move |app, i| {
                app.settings.update(|c| set_segment_action(&mut c.sponsor_block, category, SEGMENT_ACTIONS[i]))
            });
        }
        p.check("Say what was skipped", c.sponsor_block.announce_skips, |app, on| app.settings.update(|c| c.sponsor_block.announce_skips = on));
        p.check("Tell me when a video has a highlight", c.sponsor_block.announce_highlight, |app, on| app.settings.update(|c| c.sponsor_block.announce_highlight = on));
        p.check("Tell me when a whole video is labeled, such as sponsored", c.sponsor_block.announce_whole_video_labels, |app, on| app.settings.update(|c| c.sponsor_block.announce_whole_video_labels = on));
        p.text(&t("Dislikes"));
        p.text(&t("YouTube hides dislike counts. Return YouTube Dislike (returnyoutubedislike.com) estimates them; they appear in the description and when you press I in the player. Only the video's id is sent."));
        p.check("Show likes and estimated dislikes", c.dislikes.enabled, |app, on| {
            app.settings.update(|c| c.dislikes.enabled = on);
            if !on {
                app.playing.votes = None;
            }
        });
        page_names.push(t("SponsorBlock and dislikes"));
        p.finish(&pages, "SponsorBlock and dislikes");

        // Links and browser
        let p = Page::new(&pages);
        p.check("Let browsers and other apps open YouTube links in TimePlayer", c.open_links_from_other_apps, App::open_links_changed);
        p.button("_Register TimePlayer for links again", App::register_links_again);
        p.button("Open the browser e_xtension folder", App::open_extension_folder);
        p.button("Copy the extension folder's _location", App::copy_extension_folder);
        p.button("Copy the _browser bookmark", App::copy_browser_bookmark);
        let steps = t("1. Choose \"Copy the extension folder's location\" above.")
            + "\n"
            + &t("2. In Chrome, Edge or Brave, type chrome://extensions in the address bar (edge://extensions in Edge, brave://extensions in Brave) and press Enter.")
            + "\n"
            + &t("3. Turn on Developer mode. It's a switch on that page.")
            + "\n"
            + &t("4. Choose \"Load unpacked\". In the folder window, paste the location into the folder box, press Enter, then choose Select Folder.")
            + "\n"
            + &t("5. That's it. On other websites, \"Play in TimePlayer\" now follows each YouTube link. On YouTube itself, press Alt+Shift+P to play the page.")
            + "\n"
            + &t("6. Links now go straight to TimePlayer. If you added an earlier version of the extension, remove that one in the browser's extensions page.")
            + "\n\n"
            + &t("Without the extension: choose \"Copy the browser bookmark\", add a bookmark in your browser and paste it as the address. On a YouTube page, choose that bookmark.")
            + "\n\n"
            + &t("If links stop opening in TimePlayer (for example after moving a portable copy), choose \"Register TimePlayer for links again\".");
        p.document("_How to add the Play in TimePlayer extension:", "How to add the Play in TimePlayer extension", &steps, 200);
        let share_index = SHARE_KINDS.iter().position(|k| *k == c.share_links).unwrap_or(0);
        p.choice("Control+C _shares:", "Control+C shares", vec![t("Ask each time"), t("YouTube link"), t("TimePlayer link")], share_index, |app, i| {
            app.settings.update(|c| c.share_links = SHARE_KINDS[i].to_owned())
        });
        page_names.push(t("Links and browser"));
        p.finish(&pages, "Links and browser");

        // YouTube account
        let p = Page::new(&pages);
        let account_status = p.text(&app.account_status());
        p.choice(
            "Use my YouTube _account:",
            "Use my YouTube account",
            vec![t("Don't use my account"), t("Only for videos that need signing in"), t("Also my YouTube home, subscriptions, history and likes")],
            ACCOUNT_USES.iter().position(|u| *u == c.account.use_).unwrap_or(0),
            App::account_use_changed,
        );
        p.button("Sign _in to YouTube...", App::settings_sign_in);
        p.button("Import a sign-in _file...", App::import_sign_in_file);
        p.button("Sign _out", App::sign_out);
        let browser_index = SIGN_IN_BROWSERS.iter().position(|(code, _)| *code == app.sign_in_browser).unwrap_or(0);
        p.choice("_Browser:", "Browser", SIGN_IN_BROWSERS.iter().map(|(_, name)| (*name).to_owned()).collect(), browser_index, |app, i| app.sign_in_browser = SIGN_IN_BROWSERS[i].0.to_owned());
        p.button("Read the sign-in from the bro_wser", App::sign_in_from_browser);
        p.text(&t("Keeping in step with your account works when it's used for your home, subscriptions, history and likes (above). Likes and subscriptions are always sent to YouTube then; these choose what else is."));
        p.check("Put videos I play in my YouTube watch history", c.account.send_history, |app, on| app.settings.update(|c| c.account.send_history = on));
        p.check("Add and remove Watch Later videos on YouTube too", c.account.send_watch_later, |app, on| app.settings.update(|c| c.account.send_watch_later = on));
        p.check(
            "At startup, bring in what's new from my YouTube account: subscriptions, history, likes, Watch Later and playlists",
            c.account.sync_at_startup,
            |app, on| app.settings.update(|c| c.account.sync_at_startup = on),
        );
        let about = t("TimePlayer works without an account. Signing in is only for what YouTube keeps behind its sign-in, and for keeping TimePlayer and your account in step.")
            + "\n\n"
            + &t("There are three ways to sign in:")
            + "\n"
            + &t("1. Sign in to YouTube: Google's own sign-in page opens in a TimePlayer window. Once you're signed in, the window closes by itself. This is the easiest.")
            + "\n"
            + &t("2. Import a sign-in file: in the Play in TimePlayer browser extension's options, choose \"Export my YouTube sign-in\" while signed in to YouTube. It saves a file to Downloads; choose \"Import a sign-in file\" here and pick it. TimePlayer deletes that file after reading it.")
            + "\n"
            + &t("3. Read the sign-in from the browser: works with Firefox. Chrome-based browsers lock their cookies, so use one of the other ways there.")
            + "\n\n"
            + &t("To bring in your subscriptions, history, likes and playlists, use the Import page.")
            + "\n\n"
            + &t("The sign-in is stored encrypted for your Windows user, only sent to youtube.com, and never included in logs or problem reports. Sign out erases it. YouTube sometimes flags accounts used outside the browser, so a spare account is safest.");
        p.document("About si_gning in:", "About signing in", &about, 200);
        page_names.push(t("YouTube account"));
        p.finish(&pages, "YouTube account");

        // Import
        let p = Page::new(&pages);
        p.text(&t("Bring your YouTube subscriptions, watch history, likes, Watch Later and playlists into TimePlayer. What's already in TimePlayer stays, and nothing is added twice."));
        p.button("Sign in to _YouTube and import...", App::settings_import_by_signing_in);
        p.text(&t("Google's own sign-in page opens in a TimePlayer window. TimePlayer reads your lists, closes the page, and asks whether to stay signed in."));
        p.button("Import from Google _Takeout...", App::settings_import_takeout);
        p.button("_How to get a Google Takeout file...", App::settings_takeout_steps);
        p.text(&t("Google Takeout makes a zip of your YouTube data, sent to you by email. TimePlayer reads the zip as it is."));
        p.button("Import _subscriptions from a file...", App::settings_import_subscriptions);
        p.text(&t("Reads NewPipe, LibreTube and Tubular exports, FreeTube profiles, OPML files, and Takeout's subscriptions.csv."));
        p.button("E_xport subscriptions...", App::settings_export_subscriptions);
        p.text(&t("Saves your subscriptions for NewPipe, LibreTube, Tubular, FreeTube or an RSS reader."));
        page_names.push(t("Import"));
        p.finish(&pages, "Import");

        // Updates and help
        let p = Page::new(&pages);
        p.check("Check for TimePlayer updates at startup", c.update.check_at_startup, |app, on| app.settings.update(|c| c.update.check_at_startup = on));
        p.check("Tell me at startup how many times TimePlayer has run", c.runs.announce, |app, on| app.settings.update(|c| c.runs.announce = on));
        p.check("Add my runs to the total for everyone (only adds 1 per start, nothing about you)", c.runs.share == Some(true), |app, on| app.settings.update(|c| c.runs.share = Some(on)));
        p.button("Check for TimePlayer _updates", App::settings_check_updates);
        p.button("Report a _problem... (Control+Shift+B)", App::settings_report_problem);
        p.button("Suggest a _feature... (Control+Shift+F)", App::settings_suggest_feature);
        p.button("Update the YouTube _downloader", App::update_downloader);
        p.button("Run dia_gnostics", App::run_diagnostics);
        p.button("Play _test pattern", App::settings_test_pattern);
        p.button("Open _log folder", App::open_log_folder);
        p.text(&t("TimePlayer is free. If it's useful to you, you can support its development with a donation."));
        p.button("D_onate with PayPal", |_| crate::platform::open_url(crate::contact::PAYPAL));
        p.button("Donate on _Ko-fi", |_| crate::platform::open_url(crate::contact::KOFI));
        p.button("TimePlayer's _website", |_| crate::platform::open_url(crate::contact::WEBSITE));
        p.button("Telegram _channel for announcements", |_| crate::platform::open_url(crate::contact::TELEGRAM));
        p.text(&tf("Contact: {0}, or on Mastodon at {1}.", &[&crate::contact::EMAIL, &crate::contact::MASTODON]));
        let dependency_status = p.text(&t("All media components verified."));
        let diagnostics = p.document("Diagnostics _report:", "Diagnostics report", &t("Press 'Run Diagnostics' to test backend instances and playback components."), 220);
        page_names.push(t("Updates and help"));
        p.finish(&pages, "Updates and help");

        let close = Button::builder(&dialog).with_id(ID_CANCEL).with_label(&t("Close")).build();
        close.on_click(|_| with_app(App::close_settings));
        top.add(&intro, 0, SizerFlag::Expand | SizerFlag::All, 8);
        top.add(&help_line, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right, 8);
        top.add(&pages, 1, SizerFlag::Expand | SizerFlag::All, 8);
        top.add(&close, 0, SizerFlag::AlignRight | SizerFlag::All, 8);
        dialog.set_sizer(top, true);
        dialog.on_char_hook(|event| {
            let WindowEventData::Keyboard(ref key) = event else { return };
            let (ctrl, shift, alt) = (key.control_down(), key.shift_down(), key.alt_down());
            match key.get_key_code() {
                Some(WXK_F1) if !ctrl && !shift && !alt => {
                    with_app(|app| app.settings_help());
                    event.skip(false);
                }
                Some(WXK_F1) if ctrl && !shift && !alt => {
                    with_app(|app| app.settings_full_help());
                    event.skip(false);
                }
                Some(WXK_ESCAPE) => {
                    with_app(App::close_settings);
                    event.skip(false);
                }
                _ => event.skip(true),
            }
        });
        dialog.on_close(|event| {
            with_app(App::close_settings);
            event.skip(false);
        });
        dialog.show(true);
        pages.set_focus();
        SettingsWindow {
            dialog,
            pages,
            page_names,
            music_mode,
            video_preset,
            music_preset,
            video_description,
            music_description,
            bands,
            bass,
            clarity,
            width,
            room,
            crossfeed,
            level_volume,
            compressor,
            voice_focus,
            use_addon,
            braille_progress,
            account_status,
            dependency_status,
            diagnostics,
            help_line,
            audio_device,
            devices,
            loading_custom: std::cell::Cell::new(false),
        }
    }

    /// The page showing, for the self-check.
    #[allow(dead_code)]
    pub fn page(&self) -> Option<usize> {
        usize::try_from(self.pages.selection()).ok()
    }

    /// Control+M while Settings is open, so the Music mode box agrees.
    #[allow(dead_code)]
    pub fn show_music_mode(&self, on: bool) {
        self.music_mode.set_value(on);
        let _ = (&self.use_addon, &self.audio_device);
    }
}
