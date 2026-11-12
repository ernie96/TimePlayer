//! The player, mpv, run as its own program and controlled over JSON IPC (1.x's `MpvManager` and
//! `MpvPlaybackEngine`). It draws video into TimePlayer's window (`--wid`) and keeps the user's sound
//! effects, music mode, output device, loop and volume limit across videos and restarts.

pub mod ipc;

use serde_json::{Value, json};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::config::Settings;
use crate::error::{AppError, Result, redact_url};
use crate::http::Http;
use crate::paths;
use crate::process::{hide_window, find_on_path};

/// shinchiro's mpv build for Windows, pinned, with GitHub's published SHA-256 (as 1.x).
const WINDOWS_MPV_URL: &str = "https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20261017/mpv-x86_64-20261017-git-dde0671072.7z";
const WINDOWS_MPV_SHA256: &str = "2821d9eef879215e99975fae9616e1777f5e3747168edd3a571c41e21b8abbbc";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PlaybackState {
    #[default]
    Stopped,
    Opening,
    Buffering,
    Playing,
    Paused,
    Ended,
    Error,
}

/// What the engine tells the app.
#[derive(Debug, Clone)]
pub enum EngineEvent {
    State(PlaybackState),
    /// Position and duration in seconds, at most four times a second (at once after a seek).
    Position { position: f64, duration: f64 },
    Error(String),
    SubtitleText(String),
}

/// A sound output: mpv's name for it and the name the system shows.
#[derive(Debug, Clone, PartialEq)]
pub struct AudioDevice {
    pub name: String,
    pub description: String,
}

/// Finds mpv, or (on Windows) downloads and checks it.
pub fn find_mpv(settings: &Settings) -> Option<PathBuf> {
    if let Some(custom) = settings.with(|c| c.playback.mpv_custom_path.clone()).filter(|p| Path::new(p).is_file()) {
        return Some(PathBuf::from(custom));
    }
    let exe = if cfg!(windows) { "mpv.exe" } else { "mpv" };
    let program = paths::program_folder();
    if cfg!(target_os = "linux") {
        // The system's mpv first: it matches the system's graphics drivers. The AppImage's own copy
        // (usr/bin/mpv-bundled, beside TimePlayer) is for systems without one.
        return find_on_path(exe).or_else(|| Some(program.join("mpv-bundled")).filter(|p| p.is_file()));
    }
    let mut candidates = vec![program.join("bin").join("mpv").join(exe), program.join(exe), paths::data_folder().join("bin").join("mpv").join(exe)];
    if cfg!(windows) {
        candidates.push(paths::data_folder().join("bin").join("mpv").join("mpv.com"));
    }
    if cfg!(target_os = "macos") {
        // The copy inside TimePlayer.app (Contents/Resources/mpv), then one installed separately.
        candidates.insert(0, program.join("..").join("Resources").join("mpv").join("mpv"));
        candidates.push(PathBuf::from("/opt/homebrew/bin/mpv"));
        candidates.push(PathBuf::from("/usr/local/bin/mpv"));
        candidates.push(PathBuf::from("/Applications/mpv.app/Contents/MacOS/mpv"));
    }
    candidates.into_iter().find(|p| p.is_file()).or_else(|| find_on_path(exe))
}

pub fn ensure_mpv(http: &Http, settings: &Settings) -> Result<PathBuf> {
    if let Some(found) = find_mpv(settings) {
        return Ok(found);
    }
    if !cfg!(windows) {
        return Err(AppError::dependency_not_found(
            "mpv",
            if cfg!(target_os = "macos") { "TimePlayer.app should contain it; download TimePlayer again, or install it with Homebrew: brew install mpv" } else { "Install it with your package manager, for example: sudo apt install mpv" },
        ));
    }
    let folder = paths::data_folder().join("bin").join("mpv");
    std::fs::create_dir_all(&folder)?;
    log::info!("mpv isn't installed; downloading {WINDOWS_MPV_URL}.");
    let bytes = http.get_ok(crate::http::Request::get(WINDOWS_MPV_URL).timeout(Duration::from_secs(600)))?.body;
    if crate::ytdlp::sha256_hex(&bytes) != WINDOWS_MPV_SHA256 {
        return Err(AppError::new("MPV_INTEGRITY", "The downloaded mpv didn't match its published checksum, so it was not installed."));
    }
    let archive = folder.join(format!("mpv-{}.7z", uuid::Uuid::new_v4().simple()));
    std::fs::write(&archive, &bytes)?;
    let extracted = extract(&archive, &folder);
    let _ = std::fs::remove_file(&archive);
    if !extracted {
        return Err(AppError::new("MPV_EXTRACTION_FAILED", "Failed to extract the mpv archive using available system archivers (7-Zip or tar)."));
    }
    find_mpv(settings).ok_or_else(|| AppError::new("MPV_MISSING_AFTER_EXTRACTION", "Extraction succeeded but mpv.exe was not found."))
}

fn extract(archive: &Path, to: &Path) -> bool {
    let mut tools: Vec<(PathBuf, Vec<String>)> = Vec::new();
    if let Some(pf) = std::env::var_os("ProgramFiles") {
        tools.push((PathBuf::from(pf).join("7-Zip").join("7z.exe"), vec!["x".into(), archive.display().to_string(), format!("-o{}", to.display()), "-y".into()]));
    }
    if let Some(root) = std::env::var_os("SystemRoot") {
        // Windows' own tar (bsdtar) reads 7z archives too.
        tools.push((PathBuf::from(root).join("System32").join("tar.exe"), vec!["-xf".into(), archive.display().to_string(), "-C".into(), to.display().to_string()]));
    }
    for (tool, args) in tools {
        if !tool.is_file() {
            continue;
        }
        let mut command = Command::new(&tool);
        command.args(&args).stdout(Stdio::null()).stderr(Stdio::null());
        hide_window(&mut command);
        if command.status().is_ok_and(|s| s.success()) {
            return true;
        }
    }
    false
}

const PROP_PAUSE: i64 = 1;
const PROP_TIME_POS: i64 = 2;
const PROP_DURATION: i64 = 3;
const PROP_VOLUME: i64 = 4;
const PROP_MUTE: i64 = 5;
const PROP_SPEED: i64 = 6;
const PROP_EOF: i64 = 8;
const PROP_SUB_TEXT: i64 = 10;

#[derive(Default)]
struct Shared {
    state: PlaybackState,
    position: f64,
    duration: f64,
    volume: f64,
    speed: f64,
    muted: bool,
    paused: bool,
    current_entry: Option<i64>,
    last_report: Option<Instant>,
}

/// mpv run inside TimePlayer (libmpv) instead of as its own program: on macOS, where only mpv inside the app
/// can draw into the app's window. It's started with the same options (as "name=value", without "--") and
/// opens the same control channel, so everything else works as with the program.
pub trait InProcessPlayer: Send + Sync {
    /// Creates and starts mpv with these options; false when libmpv isn't there.
    fn start(&self, options: &[String]) -> bool;
    /// Destroys mpv.
    fn stop(&self);
}

/// Choices kept across videos and restarts.
#[derive(Clone)]
struct Sticky {
    audio_filters: String,
    video_enabled: bool,
    audio_device: String,
    max_volume: f64,
    looping: bool,
}

pub struct Engine {
    settings: Settings,
    http: Http,
    window: Mutex<Option<i64>>,
    child: Mutex<Option<Child>>,
    client: Mutex<Option<Arc<ipc::Client>>>,
    shared: Arc<Mutex<Shared>>,
    sticky: Mutex<Sticky>,
    intentional_stop: Arc<AtomicBool>,
    on_event: Arc<dyn Fn(EngineEvent) + Send + Sync>,
    ytdlp_path: Mutex<Option<PathBuf>>,
    in_process: Mutex<Option<Arc<dyn InProcessPlayer>>>,
    /// mpv runs inside TimePlayer now (libmpv), not as a program.
    running_in_process: AtomicBool,
}

impl Engine {
    pub fn new(http: Http, settings: Settings, on_event: Arc<dyn Fn(EngineEvent) + Send + Sync>) -> Arc<Self> {
        let volume = settings.with(|c| c.playback.default_volume_percent);
        let max = if settings.with(|c| c.playback.volume_boost) { 200.0 } else { 100.0 };
        let device = settings.with(|c| c.playback.audio_device.clone());
        Arc::new(Self {
            settings,
            http,
            window: Mutex::new(None),
            child: Mutex::new(None),
            client: Mutex::new(None),
            shared: Arc::new(Mutex::new(Shared { volume, speed: 1.0, ..Default::default() })),
            sticky: Mutex::new(Sticky { audio_filters: String::new(), video_enabled: true, audio_device: device, max_volume: max, looping: false }),
            intentional_stop: Arc::new(AtomicBool::new(false)),
            on_event,
            ytdlp_path: Mutex::new(None),
            in_process: Mutex::new(None),
            running_in_process: AtomicBool::new(false),
        })
    }

    /// mpv inside TimePlayer (macOS); the program stays the fallback when it can't start.
    pub fn set_in_process(&self, player: Arc<dyn InProcessPlayer>) {
        *self.in_process.lock().unwrap() = Some(player);
    }

    /// The window mpv draws into, and the yt-dlp it should use for links it opens itself.
    pub fn attach(self: &Arc<Self>, window_handle: i64, ytdlp: Option<PathBuf>) {
        *self.window.lock().unwrap() = Some(window_handle);
        *self.ytdlp_path.lock().unwrap() = ytdlp;
    }

    /// The yt-dlp mpv uses for links it opens itself, when there's no window to attach (sound only).
    pub fn set_ytdlp(&self, ytdlp: Option<PathBuf>) {
        *self.ytdlp_path.lock().unwrap() = ytdlp;
    }

    pub fn state(&self) -> PlaybackState {
        self.shared.lock().unwrap().state
    }

    pub fn position(&self) -> f64 {
        self.shared.lock().unwrap().position
    }

    pub fn duration(&self) -> f64 {
        self.shared.lock().unwrap().duration
    }

    pub fn volume(&self) -> f64 {
        self.shared.lock().unwrap().volume
    }

    pub fn speed(&self) -> f64 {
        self.shared.lock().unwrap().speed
    }

    pub fn is_muted(&self) -> bool {
        self.shared.lock().unwrap().muted
    }

    pub fn is_alive(&self) -> bool {
        let connected = self.client.lock().unwrap().as_ref().is_some_and(|c| c.is_connected());
        connected
            && (self.running_in_process.load(Ordering::SeqCst)
                || self.child.lock().unwrap().as_mut().is_some_and(|c| c.try_wait().ok().flatten().is_none()))
    }

    fn client(&self) -> Result<Arc<ipc::Client>> {
        if !self.is_alive() {
            self.start()?;
        }
        self.client.lock().unwrap().clone().ok_or_else(|| AppError::playback("Playback engine is not running or connected."))
    }

    fn set_state(&self, state: PlaybackState) {
        set_state(&self.shared, &self.on_event, state);
    }

    /// Starts mpv (downloading it first if needed) and connects.
    pub fn start(&self) -> Result<()> {
        self.terminate();
        let in_process = self.in_process.lock().unwrap().clone();
        let endpoint = ipc::new_endpoint();
        let playback = self.settings.with(|c| c.playback.clone());
        let sticky = self.sticky.lock().unwrap().clone();
        let mut args: Vec<String> = vec![
            format!("--input-ipc-server={}", ipc::server_argument(&endpoint)),
            "--idle=yes".into(),
            // A plain command-line player whatever an mpv.conf says: mpv.app's own config asks for its GUI
            // mode, which opens a window.
            "--player-operation-mode=cplayer".into(),
            // Not in Windows' media pop-up as "mpv"; TimePlayer handles the media keys itself.
            #[cfg(windows)]
            "--media-controls=no".into(),
            "--keep-open=yes".into(),
            "--osc=no".into(),
            "--osd-level=0".into(),
            "--input-default-bindings=no".into(),
            "--input-vo-keyboard=no".into(),
            "--input-cursor=no".into(),
            "--ytdl=yes".into(),
            "--user-agent=Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36".into(),
            "--referrer=https://www.youtube.com/".into(),
            format!("--hwdec={}", if playback.hardware_decoding { "auto-safe" } else { "no" }),
            format!("--volume={}", self.volume()),
            format!("--volume-max={}", sticky.max_volume),
            format!("--speed={}", self.speed()),
        ];
        match self.embedding_window() {
            // Inside TimePlayer, mpv hands its frames to TimePlayer, which draws them in its window.
            _ if in_process.is_some() => args.push("--vo=libmpv".into()),
            Some(wid) => {
                args.push(format!("--wid={wid}"));
                args.push("--force-window=yes".into());
                if cfg!(windows) {
                    args.push("--gpu-context=d3d11".into());
                }
            }
            // Nothing to draw into: sound only, with no video output at all, so mpv can never open a window
            // of its own.
            None => args.extend(["--force-window=no", "--vid=no", "--vo=null"].map(String::from)),
        }
        if sticky.audio_device != "auto" && !sticky.audio_device.is_empty() {
            args.push(format!("--audio-device={}", sticky.audio_device));
        }
        if let Some(ytdlp) = self.ytdlp_path.lock().unwrap().as_ref().filter(|p| p.is_file()) {
            args.push(format!("--script-opts=ytdl_hook-ytdl_path={}", ytdlp.display()));
        }
        let options: Vec<String> = args.iter().map(|a| a.trim_start_matches("--").to_owned()).collect();
        let started_inside = in_process.as_ref().is_some_and(|player| {
            log::info!("Starting mpv inside TimePlayer with IPC at {endpoint}.");
            player.start(&options)
        });
        self.running_in_process.store(started_inside, Ordering::SeqCst);
        if !started_inside {
            if in_process.is_some() {
                log::warn!("mpv couldn't start inside TimePlayer; playing sound only through the mpv program.");
                args.retain(|a| a != "--vo=libmpv");
                args.extend(["--force-window=no", "--vid=no", "--vo=null"].map(String::from));
            }
            let exe = ensure_mpv(&self.http, &self.settings)?;
            log::info!("Starting mpv ({}) with IPC at {endpoint}.", exe.display());
            let mut command = Command::new(&exe);
            command.args(&args).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
            hide_window(&mut command);
            let child = command.spawn().map_err(|e| AppError::playback(format!("mpv could not start: {e}")))?;
            *self.child.lock().unwrap() = Some(child);
        }
        self.intentional_stop.store(false, Ordering::SeqCst);

        let shared = self.shared.clone();
        let on_event = self.on_event.clone();
        let stopping = self.intentional_stop.clone();
        let handler: ipc::Handler = Arc::new(move |m| handle(&shared, &on_event, &stopping, m));
        let client = Arc::new(ipc::Client::connect(&endpoint, Duration::from_secs(10), handler)?);
        for (id, name) in [
            (PROP_PAUSE, "pause"),
            (PROP_TIME_POS, "time-pos"),
            (PROP_DURATION, "duration"),
            (PROP_VOLUME, "volume"),
            (PROP_MUTE, "mute"),
            (PROP_SPEED, "speed"),
            (PROP_EOF, "eof-reached"),
            (PROP_SUB_TEXT, "sub-text"),
        ] {
            client.observe(id, name);
        }
        // A restarted player keeps the user's effects, music mode and loop.
        if !sticky.audio_filters.is_empty() {
            apply_filters(&client, &sticky.audio_filters);
        }
        if !sticky.video_enabled {
            client.set("vid", json!("no"));
        }
        if sticky.looping {
            client.set("loop-file", json!("inf"));
        }
        *self.client.lock().unwrap() = Some(client);
        self.set_state(PlaybackState::Stopped);
        Ok(())
    }

    /// Plays a file or stream, with a separate audio track when the video is DASH.
    pub fn load(&self, url: &str, audio_url: Option<&str>, start_seconds: Option<f64>) -> Result<()> {
        let client = self.client()?;
        log::info!("Loading in mpv: {}", redact_url(url));
        self.intentional_stop.store(false, Ordering::SeqCst);
        self.shared.lock().unwrap().current_entry = None;
        self.set_state(PlaybackState::Opening);
        let options = match start_seconds.filter(|s| *s > 0.0) {
            Some(s) => format!("start={s}"),
            None => "start=none".into(),
        };
        // mpv 0.38 added an index before the options; older mpv (Ubuntu 24.04 has 0.37) answers that with
        // "invalid parameter" and takes the options third.
        let mut reply = client.command(json!(["loadfile", url, "replace", -1, options]))?;
        if reply.get("error").and_then(Value::as_str) == Some("invalid parameter") {
            reply = client.command(json!(["loadfile", url, "replace", options]))?;
        }
        if reply.get("error").and_then(Value::as_str) != Some("success") {
            self.set_state(PlaybackState::Error);
            let message = format!("mpv failed to load media: {}", reply.get("error").and_then(Value::as_str).unwrap_or("unknown"));
            (self.on_event)(EngineEvent::Error(message.clone()));
            return Err(AppError::playback(message));
        }
        // mpv keeps the pause flag across files and pauses itself at the end of one (--keep-open), so a
        // video loaded after another ended would otherwise start silently paused.
        if self.shared.lock().unwrap().paused {
            client.set("pause", json!(false));
        }
        if let Some(audio) = audio_url.filter(|a| !a.trim().is_empty()) {
            client.ok(json!(["audio-add", audio, "select"]));
        }
        Ok(())
    }

    pub fn play(&self) -> Result<()> {
        self.client()?.set("pause", json!(false));
        Ok(())
    }

    pub fn pause(&self) -> Result<()> {
        self.client()?.set("pause", json!(true));
        Ok(())
    }

    pub fn is_paused(&self) -> bool {
        self.shared.lock().unwrap().paused
    }

    pub fn stop(&self) {
        if !self.is_alive() {
            return;
        }
        self.intentional_stop.store(true, Ordering::SeqCst);
        if let Some(c) = self.client.lock().unwrap().as_ref() {
            c.ok(json!(["stop"]));
        }
        self.set_state(PlaybackState::Stopped);
    }

    pub fn seek(&self, seconds: f64) -> Result<()> {
        self.client()?.ok(json!(["seek", seconds.max(0.0), "absolute"]));
        Ok(())
    }

    pub fn seek_relative(&self, delta: f64) -> Result<()> {
        self.client()?.ok(json!(["seek", delta, "relative"]));
        Ok(())
    }

    pub fn set_volume(&self, percent: f64) -> Result<()> {
        let max = self.sticky.lock().unwrap().max_volume;
        let clamped = percent.clamp(0.0, max);
        self.client()?.set("volume", json!(clamped));
        self.shared.lock().unwrap().volume = clamped;
        Ok(())
    }

    pub fn set_speed(&self, speed: f64) -> Result<()> {
        let clamped = speed.clamp(0.25, 4.0);
        self.client()?.set("speed", json!(clamped));
        self.shared.lock().unwrap().speed = clamped;
        Ok(())
    }

    pub fn set_mute(&self, muted: bool) -> Result<()> {
        self.client()?.set("mute", json!(muted));
        self.shared.lock().unwrap().muted = muted;
        Ok(())
    }

    pub fn add_subtitles(&self, url: &str, select: bool) -> Result<()> {
        self.client()?.ok(json!(["sub-add", url, if select { "select" } else { "auto" }]));
        Ok(())
    }

    pub fn set_subtitle_track(&self, id: &str) -> Result<()> {
        self.client()?.set("sub", json!(id));
        Ok(())
    }

    /// Sound effects (an mpv "af" chain; empty for none), kept across videos.
    pub fn set_audio_filters(&self, chain: &str) {
        self.sticky.lock().unwrap().audio_filters = chain.to_owned();
        if let Some(c) = self.live_client() {
            apply_filters(&c, chain);
        }
    }

    /// Music mode: no video decoding.
    pub fn set_video_enabled(&self, enabled: bool) {
        self.sticky.lock().unwrap().video_enabled = enabled;
        // Without TimePlayer's window to draw into, video stays off whatever music mode says.
        let shown = enabled && self.can_show_video();
        if let Some(c) = self.live_client() {
            c.set("vid", json!(if shown { "auto" } else { "no" }));
        }
    }

    /// The window mpv draws into: TimePlayer's video area. On macOS there is none, since a separate mpv
    /// process can't draw into another app's view there; mpv inside TimePlayer draws there instead.
    fn embedding_window(&self) -> Option<i64> {
        if cfg!(target_os = "macos") { None } else { *self.window.lock().unwrap() }
    }

    /// Whether video can show inside TimePlayer: mpv drawing into its window, or mpv inside it.
    fn can_show_video(&self) -> bool {
        self.running_in_process.load(Ordering::SeqCst) || self.embedding_window().is_some()
    }

    pub fn set_audio_device(&self, device: &str) {
        let device = if device.trim().is_empty() { "auto".to_owned() } else { device.to_owned() };
        self.sticky.lock().unwrap().audio_device = device.clone();
        if let Some(c) = self.live_client() {
            c.set("audio-device", json!(device));
        }
    }

    pub fn audio_devices(&self) -> Vec<AudioDevice> {
        let Some(list) = self.live_client().and_then(|c| c.get("audio-device-list")) else { return Vec::new() };
        list.as_array()
            .map(|devices| {
                devices
                    .iter()
                    .filter_map(|d| {
                        let name = d.get("name")?.as_str().filter(|n| !n.trim().is_empty())?.to_owned();
                        let description = d.get("description").and_then(Value::as_str).filter(|s| !s.trim().is_empty()).unwrap_or(&name).to_owned();
                        Some(AudioDevice { name, description })
                    })
                    .collect()
            })
            .unwrap_or_default()
    }

    /// Plays each video again and again (Shorts), or once.
    pub fn set_loop(&self, looping: bool) {
        self.sticky.lock().unwrap().looping = looping;
        if let Some(c) = self.live_client() {
            c.set("loop-file", json!(if looping { "inf" } else { "no" }));
        }
    }

    /// The highest volume allowed: 100, or up to 200 for quiet videos.
    pub fn set_max_volume(&self, percent: f64) {
        let max = percent.clamp(100.0, 200.0);
        self.sticky.lock().unwrap().max_volume = max;
        if let Some(c) = self.live_client() {
            c.set("volume-max", json!(max));
        }
    }

    pub fn max_volume(&self) -> f64 {
        self.sticky.lock().unwrap().max_volume
    }

    fn live_client(&self) -> Option<Arc<ipc::Client>> {
        self.client.lock().unwrap().clone().filter(|c| c.is_connected())
    }

    pub fn restart(&self) -> Result<()> {
        log::info!("Restarting mpv after a failure.");
        self.start()
    }

    /// Quits mpv.
    pub fn terminate(&self) {
        self.intentional_stop.store(true, Ordering::SeqCst);
        if let Some(c) = self.client.lock().unwrap().take() {
            let _ = c.command(json!(["quit"]));
        }
        if self.running_in_process.swap(false, Ordering::SeqCst)
            && let Some(player) = self.in_process.lock().unwrap().clone()
        {
            player.stop();
        }
        if let Some(mut child) = self.child.lock().unwrap().take() {
            let deadline = Instant::now() + Duration::from_secs(1);
            while Instant::now() < deadline && child.try_wait().ok().flatten().is_none() {
                std::thread::sleep(Duration::from_millis(50));
            }
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.terminate();
    }
}

fn apply_filters(client: &ipc::Client, chain: &str) {
    // A filter mpv can't build would silence playback; fall back to no effects instead.
    if !client.ok(json!(["set_property", "af", chain])) {
        log::warn!("mpv rejected the audio effects; playing without them. Chain: {chain}");
        client.ok(json!(["set_property", "af", ""]));
    }
}

fn set_state(shared: &Mutex<Shared>, on_event: &Arc<dyn Fn(EngineEvent) + Send + Sync>, state: PlaybackState) {
    {
        let mut s = shared.lock().unwrap();
        if s.state == state {
            return;
        }
        s.state = state;
    }
    on_event(EngineEvent::State(state));
}

fn handle(shared: &Mutex<Shared>, on_event: &Arc<dyn Fn(EngineEvent) + Send + Sync>, stopping: &AtomicBool, message: ipc::Message) {
    match message {
        ipc::Message::Event { name, reason, error, entry_id } => match name.as_str() {
            "start-file" => {
                shared.lock().unwrap().current_entry = entry_id;
                set_state(shared, on_event, PlaybackState::Buffering);
            }
            "file-loaded" => set_state(shared, on_event, PlaybackState::Playing),
            "end-file" => {
                let (current, state) = {
                    let s = shared.lock().unwrap();
                    (s.current_entry, s.state)
                };
                // "loadfile replace" ends the previous file after the new one was asked for; that end
                // belongs to the old file and must not stop (or fail) the new one.
                if let (Some(ended), Some(current)) = (entry_id, current)
                    && ended != current {
                        return;
                    }
                let reason = reason.unwrap_or_default();
                if current.is_none() && state == PlaybackState::Opening && reason != "error" {
                    return;
                }
                if reason == "error" {
                    set_state(shared, on_event, PlaybackState::Error);
                    let message = match error.filter(|e| !e.trim().is_empty()) {
                        Some(e) => format!("Playback stopped due to error: {e}"),
                        None => "Playback stopped due to media error.".into(),
                    };
                    log::error!("{message}");
                    on_event(EngineEvent::Error(message));
                } else if reason == "eof" {
                    set_state(shared, on_event, PlaybackState::Ended);
                } else {
                    set_state(shared, on_event, PlaybackState::Stopped);
                }
            }
            "playback-restart" => {
                let state = shared.lock().unwrap().state;
                if matches!(state, PlaybackState::Buffering | PlaybackState::Opening) {
                    set_state(shared, on_event, PlaybackState::Playing);
                }
            }
            _ => {}
        },
        ipc::Message::Property { id, value, .. } => match id {
            PROP_PAUSE => {
                let paused = value.as_bool() == Some(true);
                let state = {
                    let mut s = shared.lock().unwrap();
                    s.paused = paused;
                    s.state
                };
                if paused && state == PlaybackState::Playing {
                    set_state(shared, on_event, PlaybackState::Paused);
                } else if !paused && state == PlaybackState::Paused {
                    set_state(shared, on_event, PlaybackState::Playing);
                }
            }
            PROP_TIME_POS => {
                let Some(seconds) = value.as_f64() else { return };
                let report = {
                    let mut s = shared.lock().unwrap();
                    let position = seconds.max(0.0);
                    let jumped = (position - s.position).abs() > 1.5;
                    s.position = position;
                    // mpv reports every frame; four times a second is plenty, and seeks go out at once.
                    let due = s.last_report.is_none_or(|t| t.elapsed() >= Duration::from_millis(250));
                    if jumped || due {
                        s.last_report = Some(Instant::now());
                        Some((s.position, s.duration))
                    } else {
                        None
                    }
                };
                if let Some((position, duration)) = report {
                    on_event(EngineEvent::Position { position, duration });
                }
            }
            PROP_DURATION => {
                if let Some(d) = value.as_f64() {
                    let position = {
                        let mut s = shared.lock().unwrap();
                        s.duration = d.max(0.0);
                        s.position
                    };
                    on_event(EngineEvent::Position { position, duration: d.max(0.0) });
                }
            }
            PROP_VOLUME => {
                if let Some(v) = value.as_f64() {
                    shared.lock().unwrap().volume = v;
                }
            }
            PROP_MUTE => shared.lock().unwrap().muted = value.as_bool() == Some(true),
            PROP_SPEED => {
                if let Some(v) = value.as_f64() {
                    shared.lock().unwrap().speed = v;
                }
            }
            PROP_EOF => {
                // With --keep-open mpv holds the last frame and sets eof-reached instead of ending the
                // file; that is the end-of-video signal the queue moves on from.
                let (state, paused) = {
                    let s = shared.lock().unwrap();
                    (s.state, s.paused)
                };
                if value.as_bool() == Some(true) {
                    if matches!(state, PlaybackState::Playing | PlaybackState::Paused | PlaybackState::Buffering) {
                        set_state(shared, on_event, PlaybackState::Ended);
                    }
                } else if value.as_bool() == Some(false) && state == PlaybackState::Ended {
                    set_state(shared, on_event, if paused { PlaybackState::Paused } else { PlaybackState::Playing });
                }
            }
            PROP_SUB_TEXT => {
                let text = match &value {
                    Value::String(s) => s.clone(),
                    Value::Null => String::new(),
                    other => other.to_string(),
                };
                on_event(EngineEvent::SubtitleText(text));
            }
            _ => {}
        },
        ipc::Message::Disconnected => {
            let state = shared.lock().unwrap().state;
            if !stopping.load(Ordering::SeqCst) && state != PlaybackState::Stopped {
                log::warn!("The mpv connection dropped.");
                set_state(shared, on_event, PlaybackState::Error);
                on_event(EngineEvent::Error("mpv connection was lost unexpectedly.".into()));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::AppConfig;

    /// Starts the mpv TimePlayer 1 installed (no window, nothing loaded), talks to it and quits.
    /// `cargo test -p timeplayer-core -- --ignored mpv`
    #[test]
    #[ignore]
    fn talks_to_a_real_mpv() {
        let exe = dirs::data_local_dir().unwrap().join("Programs").join("TimePlayer").join("bin").join("mpv").join("mpv.exe");
        if !exe.is_file() {
            return;
        }
        let mut config = AppConfig::default();
        config.playback.mpv_custom_path = Some(exe.display().to_string());
        let events = Arc::new(Mutex::new(Vec::new()));
        let seen = events.clone();
        let engine = Engine::new(Http::with_transport(|_| Err(AppError::network("offline"))), Settings::in_memory(config), Arc::new(move |e| seen.lock().unwrap().push(format!("{e:?}"))));
        engine.start().unwrap();
        assert!(engine.is_alive());
        engine.set_volume(42.0).unwrap();
        std::thread::sleep(Duration::from_millis(300));
        assert_eq!(engine.volume(), 42.0);
        engine.set_audio_filters(&crate::audio::filter_chain(&crate::audio::find_preset("Night").unwrap().settings));
        assert!(!engine.audio_devices().is_empty());
        engine.terminate();
        assert!(!engine.is_alive());
    }
}
