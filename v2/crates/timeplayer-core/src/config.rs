//! Settings, in the same `settings.json` as TimePlayer 1 (PascalCase names, enums as text), so updating
//! keeps every choice. Fields 2.0 doesn't know are kept and written back unchanged.

use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::sync::{Arc, RwLock};

use crate::audio::AudioEffectSettings;
use crate::paths;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum QualityPreference {
    #[default]
    Auto,
    P1080,
    P720,
    P480,
    P360,
    AudioOnly,
}

impl QualityPreference {
    pub fn max_height(self) -> Option<u32> {
        match self {
            Self::P1080 => Some(1080),
            Self::P720 => Some(720),
            Self::P480 => Some(480),
            Self::P360 => Some(360),
            _ => None,
        }
    }
}

/// How TimePlayer uses a YouTube sign-in, if there is one.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum AccountUse {
    /// Never: TimePlayer works without an account (the default).
    #[default]
    Off,
    /// Only for videos YouTube won't play without signing in.
    RestrictedOnly,
    /// Also your YouTube home, subscriptions, history and liked videos, and likes and subscriptions sent to YouTube.
    Full,
}

/// What to do when a SponsorBlock segment of a category starts.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
pub enum SegmentAction {
    #[default]
    Off,
    Announce,
    Skip,
}

macro_rules! section {
    ($(#[$meta:meta])* $name:ident { $($(#[$fmeta:meta])* $field:ident : $ty:ty = $default:expr),* $(,)? }) => {
        $(#[$meta])*
        #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
        #[serde(rename_all = "PascalCase", default)]
        pub struct $name {
            $($(#[$fmeta])* pub $field: $ty,)*
            #[serde(flatten)]
            pub unknown: Map<String, Value>,
        }
        impl Default for $name {
            fn default() -> Self {
                Self { $($field: $default,)* unknown: Map::new() }
            }
        }
    };
}

section!(PlaybackConfig {
    /// mpv's name for the sound output, or "auto" for the system's default device.
    audio_device: String = "auto".into(),
    /// Let Up go past 100 percent, to 200.
    volume_boost: bool = false,
    /// Play a video's audio-described track when it has one.
    audio_description: bool = true,
    default_volume_percent: f64 = 80.0,
    volume_step_percent: f64 = 5.0,
    small_seek_seconds: f64 = 5.0,
    medium_seek_seconds: f64 = 10.0,
    large_seek_seconds: f64 = 30.0,
    min_speed: f64 = 0.25,
    max_speed: f64 = 3.0,
    speed_step: f64 = 0.25,
    preferred_quality: QualityPreference = QualityPreference::Auto,
    hardware_decoding: bool = true,
    mpv_custom_path: Option<String> = None,
});

section!(BackendConfig {
    tube_repair_base_url: String = "https://tuberepair.uptimetrackers.com/".into(),
    tube_repair_timeout_seconds: u64 = 10,
    inner_tube_base_url: String = "https://www.youtube.com/youtubei/v1/".into(),
    inner_tube_timeout_seconds: u64 = 10,
    invidious_instances_directory_url: String = "https://api.invidious.io/instances.json?sort_by=health".into(),
    invidious_seed_instances: Vec<String> = vec![
        "https://invidious.f5.si".into(),
        "https://inv.nadeko.net".into(),
        "https://invidious.nerdvpn.de".into(),
    ],
    request_timeout_seconds: u64 = 15,
    max_retries_per_provider: u32 = 2,
    instance_cache_ttl_hours: u64 = 24,
    yt_dlp_custom_path: Option<String> = None,
    yt_dlp_auto_update_on_startup: bool = true,
    yt_dlp_timeout_seconds: u64 = 45,
    yt_dlp_download_directory: Option<String> = None,
    yt_dlp_browser_cookies: Option<String> = None,
    yt_dlp_max_search_results: u32 = 20,
});

section!(AccessibilityConfig {
    /// Say a chapter's name when it starts playing.
    announce_chapters: bool = false,
    /// Read timed lyrics aloud as they're sung.
    speak_lyrics: bool = false,
    braille_display_cell_count: u32 = 40,
    enable_braille_progress: bool = true,
    /// Read captions aloud through the screen reader as they appear.
    speak_captions: bool = false,
    /// Whether the user wants the NVDA braille add-on: unset until asked (only when NVDA is installed).
    use_braille_addon: Option<bool> = None,
});

section!(LibraryConfig {
    resume_playback: bool = true,
    record_history: bool = true,
    hide_shorts_in_feed: bool = false,
    /// Two-letter region code for Featured.
    featured_region: String = "US".into(),
    check_feed_on_startup: bool = true,
    autoplay: bool = true,
});

/// Where official releases are published.
pub const OFFICIAL_REPOSITORY: &str = "ernie96/TimePlayer";

section!(UpdateConfig {
    check_at_startup: bool = true,
    repository: String = OFFICIAL_REPOSITORY.into(),
    skipped_version: Option<String> = None,
});

section!(SponsorBlockConfig {
    enabled: bool = true,
    server_url: String = "https://sponsor.ajay.app".into(),
    announce_skips: bool = true,
    sponsor: SegmentAction = SegmentAction::Skip,
    self_promotion: SegmentAction = SegmentAction::Announce,
    interaction: SegmentAction = SegmentAction::Announce,
    intro: SegmentAction = SegmentAction::Off,
    outro: SegmentAction = SegmentAction::Off,
    preview: SegmentAction = SegmentAction::Off,
    filler: SegmentAction = SegmentAction::Off,
    non_music: SegmentAction = SegmentAction::Off,
    announce_highlight: bool = true,
    announce_whole_video_labels: bool = true,
});

section!(DislikeConfig {
    enabled: bool = true,
    api_url: String = "https://returnyoutubedislikeapi.com".into(),
});

section!(AudioConfig {
    /// A preset name, or "Custom".
    preset: String = crate::audio::FLAT.into(),
    /// No video, and its own effects.
    music_mode: bool = false,
    music_preset: String = "Music".into(),
    custom: AudioEffectSettings = AudioEffectSettings::default(),
});

section!(RunsConfig {
    count: i64 = 0,
    announce: bool = true,
    /// Add each start to the shared count; unset until asked once.
    share: Option<bool> = None,
});

section!(AccountConfig {
    #[serde(rename = "Use")]
    use_: AccountUse = AccountUse::Off,
});

section!(AppConfig {
    /// "" follows the system; otherwise "en", "ro", "uk" or "es".
    language: String = String::new(),
    playback: PlaybackConfig = PlaybackConfig::default(),
    backend: BackendConfig = BackendConfig::default(),
    accessibility: AccessibilityConfig = AccessibilityConfig::default(),
    library: LibraryConfig = LibraryConfig::default(),
    update: UpdateConfig = UpdateConfig::default(),
    sponsor_block: SponsorBlockConfig = SponsorBlockConfig::default(),
    dislikes: DislikeConfig = DislikeConfig::default(),
    audio: AudioConfig = AudioConfig::default(),
    runs: RunsConfig = RunsConfig::default(),
    account: AccountConfig = AccountConfig::default(),
    /// Let browsers and other apps open YouTube links in TimePlayer.
    open_links_from_other_apps: bool = true,
    /// What Control+C shares: "ask", "youtube" or "timeplayer".
    share_links: String = "ask".into(),
});

impl AppConfig {
    /// Values that would break playback are replaced by the defaults (1.x refused to save them).
    pub fn sanitize(&mut self) {
        let d = PlaybackConfig::default();
        let p = &mut self.playback;
        for (value, default) in [
            (&mut p.small_seek_seconds, d.small_seek_seconds),
            (&mut p.medium_seek_seconds, d.medium_seek_seconds),
            (&mut p.large_seek_seconds, d.large_seek_seconds),
            (&mut p.min_speed, d.min_speed),
        ] {
            if !(*value > 0.0) {
                *value = default;
            }
        }
        if p.max_speed < p.min_speed {
            p.max_speed = d.max_speed.max(p.min_speed);
        }
        if !(0.0..=100.0).contains(&p.default_volume_percent) {
            p.default_volume_percent = d.default_volume_percent;
        }
        // Early test builds saved a placeholder repository; always use the official one then (1.2.2).
        if !self.update.repository.contains('/') || self.update.repository.contains("OWNER") {
            self.update.repository = OFFICIAL_REPOSITORY.into();
        }
    }
}

/// The settings in use, shared across threads, saved on every change.
#[derive(Clone)]
pub struct Settings {
    inner: Arc<RwLock<AppConfig>>,
    path: Option<std::path::PathBuf>,
}

impl Settings {
    /// Loads `settings.json` from the data folder (or the defaults).
    pub fn load() -> Self {
        Self::load_from(paths::settings_file())
    }

    pub fn load_from(path: std::path::PathBuf) -> Self {
        let mut config = std::fs::read_to_string(&path)
            .ok()
            .and_then(|text| serde_json::from_str::<AppConfig>(text.trim_start_matches('\u{feff}')).ok())
            .unwrap_or_default();
        config.sanitize();
        Self { inner: Arc::new(RwLock::new(config)), path: Some(path) }
    }

    /// Settings that are never saved (tests).
    pub fn in_memory(config: AppConfig) -> Self {
        Self { inner: Arc::new(RwLock::new(config)), path: None }
    }

    pub fn get(&self) -> AppConfig {
        self.inner.read().unwrap().clone()
    }

    /// Reads one value without copying everything.
    pub fn with<R>(&self, f: impl FnOnce(&AppConfig) -> R) -> R {
        f(&self.inner.read().unwrap())
    }

    /// Changes the settings and saves them.
    pub fn update(&self, change: impl FnOnce(&mut AppConfig)) {
        let snapshot = {
            let mut config = self.inner.write().unwrap();
            change(&mut config);
            config.sanitize();
            config.clone()
        };
        self.save(&snapshot);
    }

    fn save(&self, config: &AppConfig) {
        let Some(path) = &self.path else { return };
        match serde_json::to_vec_pretty(config) {
            Ok(json) => {
                if let Err(e) = paths::write_atomic(path, &json) {
                    log::warn!("Saving settings failed: {e}");
                }
                // A portable copy keeps its settings next to the program too, as 1.x did.
                if paths::is_portable() {
                    let beside = paths::program_folder().join("settings.json");
                    if &beside != path {
                        let _ = paths::write_atomic(&beside, &json);
                    }
                }
            }
            Err(e) => log::warn!("Settings could not be written: {e}"),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_1x_settings_and_keeps_unknown_fields() {
        let json = r#"{
          "Language": "ro",
          "Playback": { "SmallSeekSeconds": 7, "PreferredQuality": "P720", "Future": 1 },
          "Account": { "Use": "Full" },
          "Audio": { "Preset": "Music", "Custom": { "EqualizerGains": [1,2,3,0,0,0,0,0,0,0], "Crossfeed": true } },
          "SomethingNew": { "A": true }
        }"#;
        let config: AppConfig = serde_json::from_str(json).unwrap();
        assert_eq!(config.language, "ro");
        assert_eq!(config.playback.small_seek_seconds, 7.0);
        assert_eq!(config.playback.preferred_quality, QualityPreference::P720);
        assert_eq!(config.account.use_, AccountUse::Full);
        assert!(config.audio.custom.crossfeed);
        assert_eq!(config.backend.inner_tube_timeout_seconds, 10);

        let back = serde_json::to_value(&config).unwrap();
        assert_eq!(back["Playback"]["Future"], 1);
        assert_eq!(back["SomethingNew"]["A"], true);
        assert_eq!(back["Account"]["Use"], "Full");
        assert_eq!(back["Playback"]["SmallSeekSeconds"], 7.0);
    }

    #[test]
    fn broken_values_fall_back() {
        let mut config = AppConfig::default();
        config.playback.small_seek_seconds = 0.0;
        config.update.repository = "OWNER/REPO".into();
        config.sanitize();
        assert_eq!(config.playback.small_seek_seconds, 5.0);
        assert_eq!(config.update.repository, OFFICIAL_REPOSITORY);
    }
}
