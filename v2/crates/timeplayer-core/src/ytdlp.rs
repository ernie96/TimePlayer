//! yt-dlp, the last source and the downloader (1.x's `YtDlpManager`, `YtDlpBackendProvider` and
//! `YtDlpErrorClassifier`). TimePlayer downloads its own copy, checks it against the release's SHA-256
//! list, keeps it updated, and gives it a JavaScript runtime (Deno or Node when installed, otherwise a
//! pinned QuickJS), because YouTube hides some formats behind a JavaScript puzzle.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use crate::account::Account;
use crate::config::{QualityPreference, Settings};
use crate::error::{AppError, Result};
use crate::http::{Cancel, Http};
use crate::models::{CaptionTrack, ChannelInfo, Page, ResolvedStream, SearchResult, SourceKind, StreamCandidate, VideoDetails, is_descriptive_track_name};
use crate::paths;
use crate::process::{Output, RunOptions, Runner, find_on_path};

#[cfg(windows)]
const ASSET: &str = "yt-dlp.exe";
#[cfg(target_os = "macos")]
const ASSET: &str = "yt-dlp_macos";
#[cfg(all(not(windows), not(target_os = "macos")))]
const ASSET: &str = "yt-dlp_linux";

#[cfg(windows)]
const EXE_NAME: &str = "yt-dlp.exe";
#[cfg(not(windows))]
const EXE_NAME: &str = "yt-dlp";

const RELEASES: &str = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
const PLAYER_CLIENTS: &str = "--extractor-args \"youtube:player_client=mweb,default\"";

/// QuickJS-ng v0.17.0 for this platform, with the checksum GitHub publishes for it.
fn quickjs_asset() -> Option<(&'static str, &'static str)> {
    let found = if cfg!(all(windows, target_arch = "x86_64")) {
        ("qjs-windows-x86_64.exe", "2aeabf0092c3262d6b2609824418f7dd7ed1f1df939f73b2b15645230cac0d77")
    } else if cfg!(all(windows, target_arch = "x86")) {
        ("qjs-windows-x86.exe", "dedc4dd8da20234d206c433a706336fede47a14e75c690cb607f81aa9c9c71be")
    } else if cfg!(all(target_os = "linux", target_arch = "x86_64")) {
        ("qjs-linux-x86_64", "0bfc02511a9f549c28b53880d988fc7cd5d361e90c5e8afdfcd7dc6774ceace5")
    } else if cfg!(all(target_os = "linux", target_arch = "aarch64")) {
        ("qjs-linux-aarch64", "3372133484edf50a69f3c67903af41206d22a061e930e3cfb63269272ef56d2e")
    } else if cfg!(all(target_os = "macos", target_arch = "aarch64")) {
        ("qjs-darwin-arm64", "8be3ddfe3397d2e692e4e1e8972ee9d032a0a580505d2f8b4ea528cf1b651c11")
    } else if cfg!(all(target_os = "macos", target_arch = "x86_64")) {
        ("qjs-darwin-x86_64", "9e5e101b4fd13cda3204222ca9f8be35412c41dcdef3745829633b7a67245412")
    } else {
        return None;
    };
    Some(found)
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
struct VersionFile {
    version: Option<String>,
    #[serde(with = "crate::models::dateoffset::option")]
    last_checked_utc: Option<chrono::DateTime<chrono::Utc>>,
    sha256: Option<String>,
}

/// Where yt-dlp is and keeping it current.
#[derive(Clone)]
pub struct YtDlp {
    http: Http,
    settings: Settings,
    runner: Arc<dyn Runner>,
    account: Option<Account>,
}

pub fn sha256_hex(bytes: &[u8]) -> String {
    hex::encode(Sha256::digest(bytes))
}

impl YtDlp {
    pub fn new(http: Http, settings: Settings, runner: Arc<dyn Runner>, account: Option<Account>) -> Self {
        Self { http, settings, runner, account }
    }

    /// A custom path from Settings, a copy next to TimePlayer, or the one TimePlayer downloads.
    pub fn executable(&self) -> PathBuf {
        if let Some(custom) = self.settings.with(|c| c.backend.yt_dlp_custom_path.clone()).filter(|p| Path::new(p).is_file()) {
            return PathBuf::from(custom);
        }
        let beside = paths::program_folder().join("bin").join(EXE_NAME);
        if beside.is_file() {
            return beside;
        }
        let root = paths::program_folder().join(EXE_NAME);
        if root.is_file() {
            return root;
        }
        paths::data_folder().join("bin").join(EXE_NAME)
    }

    pub fn is_installed(&self) -> bool {
        self.executable().is_file()
    }

    fn own_copy(&self) -> bool {
        self.settings.with(|c| c.backend.yt_dlp_custom_path.as_deref().is_some_and(|p| !p.trim().is_empty() && Path::new(p).is_file()))
    }

    fn run(&self, args: Vec<String>, timeout: Duration, on_line: Option<crate::process::LineCallback>, cancel: &Cancel) -> Result<Output> {
        let options = RunOptions { timeout: Some(timeout), ..Default::default() };
        self.runner.run(&self.executable(), &args, &options, on_line, cancel)
    }

    /// Installs yt-dlp if it's missing, updates it if Settings allow, and sets up its config file.
    pub fn ensure_ready(&self) -> Result<String> {
        if !self.is_installed() {
            log::info!("yt-dlp is not installed; downloading a checked copy.");
            self.download_and_verify()?;
        } else if self.settings.with(|c| c.backend.yt_dlp_auto_update_on_startup) {
            self.update();
        }
        // A yt-dlp of the person's own choosing keeps its own settings.
        let own = self.own_copy();
        let runtime = if own { None } else { self.ensure_javascript_runtime() };
        self.write_config(runtime.as_deref(), !own);
        self.version()
    }

    pub fn version(&self) -> Result<String> {
        let out = self.run(vec!["--version".into()], Duration::from_secs(10), None, &Cancel::new())?;
        if out.success() && !out.stdout.trim().is_empty() {
            return Ok(out.stdout.trim().to_owned());
        }
        self.read_version_file().and_then(|v| v.version).ok_or_else(|| AppError::new("YTDLP_VERSION_UNKNOWN", "Could not query yt-dlp version."))
    }

    fn version_file(&self) -> PathBuf {
        self.executable().with_file_name("yt-dlp-version.json")
    }

    fn read_version_file(&self) -> Option<VersionFile> {
        serde_json::from_str(&std::fs::read_to_string(self.version_file()).ok()?).ok()
    }

    fn save_version(&self, version: &str, sha256: Option<String>) {
        let file = VersionFile { version: Some(version.into()), last_checked_utc: Some(chrono::Utc::now()), sha256 };
        if let Ok(json) = serde_json::to_vec_pretty(&file) {
            let _ = paths::write_atomic(&self.version_file(), &json);
        }
    }

    /// "yt-dlp --update", falling back to a fresh checked download if that fails.
    pub fn update(&self) -> bool {
        match self.run(vec!["--update".into()], Duration::from_secs(120), None, &Cancel::new()) {
            Ok(out) => {
                log::info!("yt-dlp update response: {}", out.stdout.trim());
                let updated = out.stdout.contains("Updating to version") || out.stdout.contains("Updated yt-dlp");
                if let Ok(v) = self.version() {
                    self.save_version(&v, None);
                }
                updated
            }
            Err(e) => {
                log::warn!("yt-dlp self-update failed ({e}); downloading it again.");
                self.download_and_verify().is_ok()
            }
        }
    }

    /// Downloads the release for this platform and checks it against SHA2-256SUMS.
    pub fn download_and_verify(&self) -> Result<()> {
        let target = self.executable();
        let bytes = self.http.get_ok(crate::http::Request::get(format!("{RELEASES}{ASSET}")).timeout(Duration::from_secs(300)))?.body;
        let computed = sha256_hex(&bytes);
        match self.http.get_text(&format!("{RELEASES}SHA2-256SUMS"), Duration::from_secs(30)) {
            Ok(sums) => {
                if let Some(expected) = checksum_for(&sums, ASSET)
                    && !expected.eq_ignore_ascii_case(&computed) {
                        log::error!("yt-dlp checksum mismatch: expected {expected}, got {computed}.");
                        return Err(AppError::new("CHECKSUM_MISMATCH", "Downloaded yt-dlp binary failed SHA256 integrity verification."));
                    }
            }
            Err(e) => log::warn!("SHA2-256SUMS could not be read ({e}); continuing without it."),
        }
        paths::write_atomic(&target, &bytes)?;
        make_executable(&target);
        let version = self.version().unwrap_or_else(|_| "latest".into());
        self.save_version(&version, Some(computed));
        log::info!("yt-dlp {version} installed at {}.", target.display());
        Ok(())
    }

    /// The --js-runtimes line for yt-dlp.conf, or None when Deno is installed (found by yt-dlp itself)
    /// or nothing could be set up.
    fn ensure_javascript_runtime(&self) -> Option<String> {
        let exe = |name: &str| if cfg!(windows) { format!("{name}.exe") } else { name.to_owned() };
        if find_on_path(&exe("deno")).is_some() {
            return None;
        }
        if find_on_path(&exe("node")).is_some() {
            return Some("--js-runtimes node".into());
        }
        let (asset, sha) = quickjs_asset()?;
        let qjs = self.executable().with_file_name(exe("qjs"));
        let current = std::fs::read(&qjs).ok().map(|b| sha256_hex(&b));
        if current.as_deref() != Some(sha) {
            let url = format!("https://github.com/quickjs-ng/quickjs/releases/download/v0.17.0/{asset}");
            log::info!("Downloading QuickJS for yt-dlp from {url}.");
            let bytes = match self.http.get_ok(crate::http::Request::get(&url).timeout(Duration::from_secs(120))) {
                Ok(r) => r.body,
                Err(e) => {
                    log::warn!("QuickJS could not be downloaded: {e}");
                    return None;
                }
            };
            if sha256_hex(&bytes) != sha {
                log::error!("QuickJS checksum mismatch.");
                return None;
            }
            if paths::write_atomic(&qjs, &bytes).is_err() {
                return None;
            }
            make_executable(&qjs);
        }
        Some(format!("--js-runtimes \"quickjs:{}\"", qjs.display()))
    }

    fn write_config(&self, runtime: Option<&str>, overwrite: bool) {
        let conf = self.executable().with_file_name("yt-dlp.conf");
        let wanted = config_text(runtime);
        let current = std::fs::read_to_string(&conf).ok();
        if (current.is_none() || (overwrite && current.as_deref() != Some(&wanted)))
            && let Err(e) = paths::write_atomic(&conf, wanted.as_bytes()) {
                log::warn!("yt-dlp.conf could not be written: {e}");
            }
    }

    fn cookie_args(&self, args: &mut Vec<String>, cookies_file: Option<&Path>) {
        if let Some(file) = cookies_file {
            args.push("--cookies".into());
            args.push(file.display().to_string());
        } else if let Some(browser) = self.settings.with(|c| c.backend.yt_dlp_browser_cookies.clone()).filter(|b| !b.trim().is_empty()) {
            args.push("--cookies-from-browser".into());
            args.push(browser);
        }
    }

    fn timeout(&self) -> Duration {
        Duration::from_secs(self.settings.with(|c| c.backend.yt_dlp_timeout_seconds).max(5))
    }

    fn ensure_installed(&self) -> Result<()> {
        if self.is_installed() { Ok(()) } else { self.ensure_ready().map(|_| ()) }
    }

    /// Pages are windows into a larger ytsearch; yt-dlp is slow, so no more than ten.
    pub fn search(&self, query: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        if query.trim().is_empty() {
            return Err(AppError::new("EMPTY_QUERY", "Search query cannot be empty."));
        }
        self.ensure_installed()?;
        let per_page = self.settings.with(|c| c.backend.yt_dlp_max_search_results).max(1) as usize;
        let page: usize = page_token.and_then(|t| t.parse().ok()).filter(|p| *p > 1).unwrap_or(1);
        let (first, last) = ((page - 1) * per_page + 1, page * per_page);
        let mut args: Vec<String> =
            ["--dump-single-json", "--default-search", "ytsearch", "--flat-playlist", "--skip-download", "--no-warnings"].map(String::from).to_vec();
        if page > 1 {
            args.push("--playlist-items".into());
            args.push(format!("{first}:{last}"));
        }
        self.cookie_args(&mut args, None);
        args.push(format!("ytsearch{last}:{query}"));
        let out = self.run(args, self.timeout(), None, &Cancel::new())?;
        if !out.success() {
            return Err(classify(out.exit_code, &out.stderr, &out.stdout, ""));
        }
        if out.stdout.trim().is_empty() {
            return Ok(Page::default());
        }
        let json: Value = serde_json::from_str(&out.stdout)?;
        let items: Vec<SearchResult> = json
            .get("entries")
            .and_then(Value::as_array)
            .map(|entries| entries.iter().filter_map(entry_to_result).collect())
            .unwrap_or_default();
        let more = items.len() >= per_page && page < 10;
        Ok(Page { items, next_token: more.then(|| (page + 1).to_string()) })
    }

    pub fn details(&self, video_id: &str, cookies_file: Option<&Path>) -> Result<VideoDetails> {
        self.ensure_installed()?;
        let mut args: Vec<String> = ["--dump-single-json", "--skip-download", "--no-warnings"].map(String::from).to_vec();
        self.cookie_args(&mut args, cookies_file);
        args.push(crate::links::watch_url(video_id));
        log::info!("Extracting video details via yt-dlp for {video_id}...");
        let out = self.run(args, self.timeout(), None, &Cancel::new())?;
        if !out.success() {
            let error = classify(out.exit_code, &out.stderr, &out.stdout, video_id);
            log::warn!("yt-dlp video extraction failed for {video_id}: {}", error.message);
            return Err(error);
        }
        let v: Value = serde_json::from_str(&out.stdout)?;
        if v.get("id").and_then(Value::as_str).is_none_or(str::is_empty) {
            return Err(AppError::new("INVALID_YTDLP_JSON", "yt-dlp returned invalid or empty video JSON."));
        }
        Ok(info_to_details(video_id, &v))
    }

    /// The streams to play: the described track when asked for and present, otherwise the best ordinary audio.
    pub fn resolve(&self, video_id: &str, quality: QualityPreference, audio_only: bool, audio_description: bool, cookies_file: Option<&Path>) -> Result<ResolvedStream> {
        let details = self.details(video_id, cookies_file)?;
        choose_streams(video_id, &details, quality, audio_only, audio_description)
    }

    /// Downloads a video or its audio, reporting progress lines; the path of the saved file.
    pub fn download(&self, video_id: &str, options: &DownloadOptions, on_progress: Option<&(dyn Fn(DownloadProgress) + Sync)>, cancel: &Cancel) -> Result<PathBuf> {
        self.ensure_installed()?;
        std::fs::create_dir_all(&options.output_directory)?;
        let output = options.output_directory.join(&options.file_name_pattern);
        let mut args: Vec<String> = ["--newline", "--no-playlist", "--no-warnings", "-o"].map(String::from).to_vec();
        args.push(output.display().to_string());
        // Converting to MP3 and merging video with audio need ffmpeg; without it, take single files.
        if let Some(ffmpeg) = &options.ffmpeg_location {
            args.push("--ffmpeg-location".into());
            args.push(ffmpeg.display().to_string());
        }
        let has_ffmpeg = options.ffmpeg_location.is_some();
        match (options.audio_only, has_ffmpeg) {
            (true, true) => args.extend(["-x", "--audio-format", "mp3", "--audio-quality", "0"].map(String::from)),
            (true, false) => args.extend(["-f".into(), "bestaudio[ext=m4a]/bestaudio".into()]),
            (false, true) => args.extend(["-f".into(), format_selector(options.quality), "--merge-output-format".into(), "mp4".into()]),
            (false, false) => args.extend(["-f".into(), "best[ext=mp4]/best".into()]),
        }
        self.cookie_args(&mut args, options.cookies_file.as_deref());
        args.push(crate::links::watch_url(video_id));

        let destination = std::sync::Mutex::new(None::<String>);
        let on_line = |line: &str| {
            if let Some(dest) = destination_of(line) {
                *destination.lock().unwrap() = Some(dest);
            }
            if let (Some(cb), Some(p)) = (on_progress, parse_progress(line)) {
                cb(p);
            }
        };
        log::info!("Starting yt-dlp download for {video_id} to {}.", options.output_directory.display());
        let run_options = RunOptions { timeout: Some(Duration::from_secs(7200)), working_directory: Some(options.output_directory.clone()), ..Default::default() };
        let out = match self.runner.run(&self.executable(), &args, &run_options, Some(&on_line), cancel) {
            Err(e) if e.is_canceled() => return Err(AppError::new("DOWNLOAD_CANCELED", "The media download was canceled.")),
            other => other?,
        };
        if !out.success() {
            let error = classify(out.exit_code, &out.stderr, &out.stdout, video_id);
            log::warn!("yt-dlp download failed for {video_id}: {}", error.message);
            return Err(error);
        }
        let saved = destination.into_inner().unwrap().map(PathBuf::from).unwrap_or(output);
        log::info!("yt-dlp download completed for {video_id}: {}", saved.display());
        Ok(saved)
    }

    /// Reads the sign-in from an installed browser ("firefox", "chrome", ...) into a cookie jar.
    pub fn read_browser_sign_in(&self, browser: &str) -> Result<String> {
        self.ensure_installed()?;
        let jar = std::env::temp_dir().join(format!("timeplayer-signin-{}.txt", uuid::Uuid::new_v4().simple()));
        let args: Vec<String> = vec![
            "--cookies-from-browser".into(),
            browser.into(),
            "--cookies".into(),
            jar.display().to_string(),
            "--skip-download".into(),
            "--no-warnings".into(),
            "--no-playlist".into(),
            "https://www.youtube.com/watch?v=jNQXAC9IVRw".into(),
        ];
        let result = self.run(args, Duration::from_secs(90), None, &Cancel::new());
        let text = std::fs::read_to_string(&jar);
        let _ = std::fs::remove_file(&jar);
        match text {
            Ok(text) => Ok(text),
            Err(_) => {
                log::info!("Reading the sign-in from {browser} failed ({:?}).", result.map(|o| o.exit_code));
                let chromium = matches!(browser, "chrome" | "edge" | "brave" | "opera" | "vivaldi");
                Err(AppError::new(
                    "SIGNIN_BROWSER",
                    if chromium {
                        crate::t!("TimePlayer could not read the sign-in from that browser; Chrome-based browsers now lock their cookies. Use the Play in TimePlayer extension's \"Export my YouTube sign-in\" instead, or Firefox.")
                    } else {
                        crate::t!("TimePlayer could not read the sign-in from that browser. Close the browser and try again.")
                    },
                ))
            }
        }
    }

    pub fn account(&self) -> Option<&Account> {
        self.account.as_ref()
    }
}

pub fn config_text(runtime: Option<&str>) -> String {
    match runtime.filter(|r| !r.is_empty()) {
        Some(r) => format!("{PLAYER_CLIENTS}\n{r}\n"),
        None => format!("{PLAYER_CLIENTS}\n"),
    }
}

fn make_executable(_path: &Path) {
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        let _ = std::fs::set_permissions(_path, std::fs::Permissions::from_mode(0o755));
    }
}

fn checksum_for(sums: &str, file: &str) -> Option<String> {
    sums.lines().find_map(|line| {
        let mut parts = line.split_whitespace();
        let hash = parts.next()?;
        let name = parts.next()?.trim_start_matches('*');
        (name.eq_ignore_ascii_case(file)).then(|| hash.to_lowercase())
    })
}

/// yt-dlp's format choice for video downloads with ffmpeg.
pub fn format_selector(quality: QualityPreference) -> String {
    match quality.max_height() {
        Some(h) => format!("bestvideo[height<={h}][ext=mp4]+bestaudio[ext=m4a]/bestvideo[height<={h}]+bestaudio/best[height<={h}]/best"),
        None => "bestvideo[ext=mp4]+bestaudio[ext=m4a]/bestvideo+bestaudio/best".into(),
    }
}

#[derive(Debug, Clone)]
pub struct DownloadOptions {
    pub output_directory: PathBuf,
    pub audio_only: bool,
    pub quality: QualityPreference,
    /// yt-dlp's output template, "%(title)s.%(ext)s".
    pub file_name_pattern: String,
    /// The folder ffmpeg is in; without it audio is M4A and video a single MP4.
    pub ffmpeg_location: Option<PathBuf>,
    pub cookies_file: Option<PathBuf>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct DownloadProgress {
    pub percent: f64,
    pub total_size: Option<String>,
    pub speed: Option<String>,
    pub eta: Option<String>,
}

fn parse_progress(line: &str) -> Option<DownloadProgress> {
    static RE: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let re = RE.get_or_init(|| {
        regex::Regex::new(r"(?i)\[download\]\s+(?P<pct>\d+(\.\d+)?)%\s+of\s+(?P<size>\S+)\s+(?:at\s+(?P<speed>\S+))?\s*(?:ETA\s+(?P<eta>\S+))?").unwrap()
    });
    let c = re.captures(line)?;
    Some(DownloadProgress {
        percent: c["pct"].parse().ok()?,
        total_size: c.name("size").map(|m| m.as_str().into()),
        speed: c.name("speed").map(|m| m.as_str().into()),
        eta: c.name("eta").map(|m| m.as_str().into()),
    })
}

fn destination_of(line: &str) -> Option<String> {
    static RE: std::sync::OnceLock<regex::Regex> = std::sync::OnceLock::new();
    let re = RE.get_or_init(|| {
        regex::Regex::new(r#"(?i)\[(?:download|ExtractAudio)\]\s+Destination:\s+(?P<a>.+)|\[Merger\]\s+Merging formats into "(?P<b>.+)"|\[download\]\s+(?P<c>.+?) has already been downloaded"#).unwrap()
    });
    let c = re.captures(line)?;
    c.name("a").or(c.name("b")).or(c.name("c")).map(|m| m.as_str().trim().to_owned())
}

fn str_of(v: &Value, key: &str) -> Option<String> {
    v.get(key).and_then(Value::as_str).filter(|s| !s.is_empty()).map(str::to_owned)
}

fn upload_date_text(yyyymmdd: Option<String>) -> Option<String> {
    let d = chrono::NaiveDate::parse_from_str(&yyyymmdd?, "%Y%m%d").ok()?;
    Some(d.format("%B %-d, %Y").to_string())
}

fn entry_to_result(e: &Value) -> Option<SearchResult> {
    let id = str_of(e, "id")?;
    Some(SearchResult {
        channel_id: str_of(e, "channel_id").or_else(|| str_of(e, "uploader_id")),
        duration: e.get("duration").and_then(Value::as_f64).filter(|d| *d > 0.0).map(Duration::from_secs_f64),
        view_count: e.get("view_count").and_then(Value::as_i64),
        upload_date_text: upload_date_text(str_of(e, "upload_date")),
        thumbnail_url: e.get("thumbnails").and_then(Value::as_array).and_then(|t| t.last()).and_then(|t| str_of(t, "url")),
        description_snippet: str_of(e, "description"),
        ..SearchResult::video(
            &id,
            &str_of(e, "title").unwrap_or_else(|| "Untitled Video".into()),
            &str_of(e, "channel").or_else(|| str_of(e, "uploader")).unwrap_or_else(|| "Unknown Channel".into()),
        )
    })
}

pub fn info_to_details(video_id: &str, v: &Value) -> VideoDetails {
    VideoDetails {
        id: video_id.into(),
        title: str_of(v, "title").unwrap_or_else(|| "Untitled Video".into()),
        description: str_of(v, "description"),
        channel: ChannelInfo {
            id: str_of(v, "channel_id").or_else(|| str_of(v, "uploader_id")).unwrap_or_default(),
            name: str_of(v, "channel").or_else(|| str_of(v, "uploader")).unwrap_or_else(|| "Unknown Channel".into()),
        },
        duration: v.get("duration").and_then(Value::as_f64).map(Duration::from_secs_f64).unwrap_or_default(),
        view_count: v.get("view_count").and_then(Value::as_i64),
        like_count: v.get("like_count").and_then(Value::as_i64),
        upload_date: str_of(v, "upload_date")
            .and_then(|d| chrono::NaiveDate::parse_from_str(&d, "%Y%m%d").ok())
            .and_then(|d| d.and_hms_opt(0, 0, 0))
            .map(|d| d.and_utc()),
        thumbnail_url: v.get("thumbnails").and_then(Value::as_array).and_then(|t| t.last()).and_then(|t| str_of(t, "url")),
        streams: map_formats(v.get("formats")),
        captions: map_captions(v.get("subtitles"), false).into_iter().chain(map_captions(v.get("automatic_captions"), true)).collect(),
        is_live: v.get("is_live").and_then(Value::as_bool).unwrap_or(false),
    }
}

fn codec(v: &Value, key: &str) -> Option<String> {
    str_of(v, key).filter(|c| c != "none")
}

fn map_formats(formats: Option<&Value>) -> Vec<StreamCandidate> {
    let Some(list) = formats.and_then(Value::as_array) else { return Vec::new() };
    list.iter()
        .filter_map(|f| {
            let url = str_of(f, "url")?;
            let (vcodec, acodec) = (codec(f, "vcodec"), codec(f, "acodec"));
            let ext = str_of(f, "ext").unwrap_or_else(|| "mp4".into());
            let kbps = f.get("tbr").or_else(|| f.get("vbr")).or_else(|| f.get("abr")).and_then(Value::as_f64).unwrap_or(0.0);
            let bitrate = (kbps * 1000.0) as i64;
            let height = f.get("height").and_then(Value::as_u64).map(|h| h as u32);
            let fps = f.get("fps").and_then(Value::as_f64).map(|f| f as u32).unwrap_or(30);
            match (vcodec, acodec) {
                (None, Some(a)) => {
                    let note = str_of(f, "format_note");
                    Some(StreamCandidate {
                        url,
                        format: ext,
                        bitrate: Some(bitrate),
                        audio_bitrate: Some(bitrate),
                        audio_codec: Some(a),
                        audio_only: true,
                        audio_description: is_descriptive_track_name(note.as_deref()),
                        audio_track_name: note,
                        ..Default::default()
                    })
                }
                (Some(v), a) => {
                    let h = height.unwrap_or(0);
                    Some(StreamCandidate {
                        url,
                        format: ext,
                        height: Some(h),
                        width: f.get("width").and_then(Value::as_u64).map(|w| w as u32),
                        fps: Some(fps),
                        bitrate: Some(bitrate),
                        video_codec: Some(v),
                        quality_label: Some(if fps > 30 { format!("{h}p{fps}") } else { format!("{h}p") }),
                        progressive: a.is_some(),
                        video_only: a.is_none(),
                        ..Default::default()
                    })
                }
                (None, None) => None,
            }
        })
        .collect()
}

fn map_captions(tracks: Option<&Value>, auto: bool) -> Vec<CaptionTrack> {
    let Some(map) = tracks.and_then(Value::as_object) else { return Vec::new() };
    map.iter()
        .filter_map(|(code, list)| {
            let list = list.as_array()?;
            let chosen = list
                .iter()
                .find(|t| t.get("ext").and_then(Value::as_str) == Some("vtt"))
                .or_else(|| list.iter().find(|t| t.get("ext").and_then(Value::as_str) == Some("ttml")))
                .or_else(|| list.first())?;
            let url = str_of(chosen, "url")?;
            Some(CaptionTrack {
                language_code: code.clone(),
                display_name: str_of(chosen, "name").unwrap_or_else(|| code.clone()),
                url,
                format: str_of(chosen, "ext").unwrap_or_else(|| "vtt".into()),
                auto_generated: auto,
                is_default: false,
            })
        })
        .collect()
}

/// The streams to play from yt-dlp's formats.
pub fn choose_streams(video_id: &str, d: &VideoDetails, quality: QualityPreference, audio_only: bool, audio_description: bool) -> Result<ResolvedStream> {
    let streams = &d.streams;
    if streams.is_empty() {
        return Err(AppError::stream_unavailable(video_id, Some("No stream formats found.")));
    }
    let rate = |s: &&StreamCandidate| s.audio_bitrate.or(s.bitrate).unwrap_or(0);
    let described = streams.iter().filter(|s| s.audio_only && s.audio_description).max_by_key(rate);
    let use_description = audio_description && described.is_some();
    // Ordered by bitrate alone, the described track could win by accident.
    let chosen_audio = if use_description { described } else { streams.iter().filter(|s| s.audio_only && !s.audio_description).max_by_key(rate) };
    let base = ResolvedStream {
        video_id: video_id.into(),
        captions: d.captions.clone(),
        is_live: d.is_live,
        source: SourceKind::YtDlp,
        has_audio_description: described.is_some(),
        ..Default::default()
    };
    if audio_only || quality == QualityPreference::AudioOnly {
        let audio = chosen_audio.or_else(|| streams.iter().find(|s| s.progressive));
        let Some(audio) = audio else { return Err(AppError::stream_unavailable(video_id, Some("No audio stream available."))) };
        return Ok(ResolvedStream { url: audio.url.clone(), uses_audio_description: use_description, ..base });
    }
    let mut progressive: Vec<&StreamCandidate> = streams.iter().filter(|s| s.progressive).collect();
    progressive.sort_by_key(|s| std::cmp::Reverse(s.height.unwrap_or(0)));
    let mut video_only: Vec<&StreamCandidate> = streams.iter().filter(|s| s.video_only).collect();
    video_only.sort_by_key(|s| std::cmp::Reverse(s.height.unwrap_or(0)));
    let target = quality.max_height().unwrap_or(1080);
    let matched_progressive = progressive.iter().find(|s| s.height == Some(target)).or(progressive.first()).copied();
    let matched_video_only = video_only.iter().find(|s| s.height == Some(target)).or(video_only.first()).copied();
    let (video, audio) = if use_description && matched_video_only.is_some() {
        // A progressive stream always carries the original soundtrack; description needs separate audio.
        (video_only.iter().find(|s| s.height.unwrap_or(0) <= target).copied().or(matched_video_only), chosen_audio)
    } else if quality != QualityPreference::Auto && matched_video_only.is_some_and(|v| v.height == Some(target)) && chosen_audio.is_some() {
        (matched_video_only, chosen_audio)
    } else if matched_progressive.is_some() {
        (matched_progressive, None)
    } else if matched_video_only.is_some() && chosen_audio.is_some() {
        (matched_video_only, chosen_audio)
    } else {
        (streams.first(), None)
    };
    let Some(video) = video else { return Err(AppError::stream_unavailable(video_id, Some("No suitable video stream could be resolved."))) };
    Ok(ResolvedStream {
        url: video.url.clone(),
        audio_url: audio.map(|a| a.url.clone()),
        uses_audio_description: use_description && audio.is_some_and(|a| a.audio_description),
        alternatives: streams.iter().filter(|s| s.url != video.url).cloned().collect(),
        ..base
    })
}

/// What yt-dlp's error output means, in words a person can act on.
pub fn classify(exit_code: i32, stderr: &str, stdout: &str, video_id: &str) -> AppError {
    let text = format!("{stderr}\n{stdout}");
    let lower = text.to_lowercase();
    let has = |s: &str| lower.contains(&s.to_lowercase());
    if has("not found") && (has("yt-dlp") || has("executable")) {
        return AppError::dependency_not_found("yt-dlp", "The yt-dlp executable could not be found at the configured location.");
    }
    if has("Sign in to confirm your age") || has("inappropriate for some users") {
        return AppError::age_restricted(
            video_id,
            Some("YouTube requires age verification for this video. You can provide session cookies in settings to access age-restricted videos."),
        );
    }
    if has("Private video") || has("members-only") || has("Join this channel to get access") {
        return AppError::private_video(video_id, Some("This video is private, members-only, or requires explicit account permissions."));
    }
    if has("Video unavailable") || has("This video has been removed") || has("This video is no longer available") || has("Incomplete YouTube ID") {
        return AppError::video_not_found(video_id);
    }
    if has("confirm you're not a bot") || has("--cookies-from-browser") || has("cookies are required") || has("Sign in with a web browser") {
        return AppError::cookies_required(video_id, Some("YouTube triggered bot verification or requested session cookies. Enable browser cookies in preferences."));
    }
    if has("HTTP Error 429") || has("Too Many Requests") {
        return AppError::rate_limited("yt-dlp", Some("YouTube is temporarily rate-limiting requests from this IP address."));
    }
    if ["Unable to download API page", "Failed to resolve", "Network is unreachable", "Connection refused", "getaddrinfo failed", "timed out"]
        .iter()
        .any(|s| has(s))
    {
        return AppError::network("Network error while connecting to YouTube servers through yt-dlp.");
    }
    let first = text.lines().find(|l| l.to_uppercase().contains("ERROR:")).map(str::to_owned).unwrap_or_else(|| {
        if stderr.trim().is_empty() { format!("Exit code {exit_code}") } else { stderr.trim().to_owned() }
    });
    AppError::new("YTDLP_EXECUTION_FAILED", "yt-dlp was unable to process the video.").with_details(first)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn details_with(formats: &str) -> VideoDetails {
        let v: Value = serde_json::from_str(&format!(r#"{{"id":"x","title":"T","formats":{formats}}}"#)).unwrap();
        info_to_details("x", &v)
    }

    #[test]
    fn described_audio_is_only_used_when_asked() {
        let d = details_with(
            r#"[{"url":"orig","vcodec":"none","acodec":"opus","abr":128,"format_note":"English original (default), medium"},
                {"url":"desc","vcodec":"none","acodec":"opus","abr":160,"format_note":"English descriptive, medium"},
                {"url":"v720","vcodec":"avc1","acodec":"none","height":720},
                {"url":"p360","vcodec":"avc1","acodec":"mp4a","height":360}]"#,
        );
        let plain = choose_streams("x", &d, QualityPreference::Auto, true, false).unwrap();
        assert_eq!(plain.url, "orig");
        assert!(plain.has_audio_description && !plain.uses_audio_description);
        let described = choose_streams("x", &d, QualityPreference::Auto, false, true).unwrap();
        assert_eq!((described.url.as_str(), described.audio_url.as_deref()), ("v720", Some("desc")));
        assert!(described.uses_audio_description);
        let video = choose_streams("x", &d, QualityPreference::P720, false, false).unwrap();
        assert_eq!((video.url.as_str(), video.audio_url.as_deref()), ("v720", Some("orig")));
    }

    #[test]
    fn errors_say_what_happened() {
        assert_eq!(classify(1, "ERROR: [youtube] x: Sign in to confirm your age.", "", "x").code, "AGE_RESTRICTED");
        assert_eq!(classify(1, "ERROR: Sign in to confirm you're not a bot", "", "x").code, "COOKIES_REQUIRED");
        assert_eq!(classify(1, "ERROR: Requested format is not available", "", "x").details.as_deref(), Some("ERROR: Requested format is not available"));
        assert_eq!(config_text(Some("--js-runtimes node")), "--extractor-args \"youtube:player_client=mweb,default\"\n--js-runtimes node\n");
    }

    #[test]
    fn reads_progress_and_destinations() {
        let p = parse_progress("[download]  42.5% of 3.20MiB at 1.00MiB/s ETA 00:02").unwrap();
        assert_eq!(p.percent, 42.5);
        assert_eq!(destination_of("[Merger] Merging formats into \"C:\\x\\a.mp4\"").as_deref(), Some("C:\\x\\a.mp4"));
        assert_eq!(destination_of("[ExtractAudio] Destination: a.mp3").as_deref(), Some("a.mp3"));
    }

    #[test]
    fn checksums_are_read_from_the_sums_file() {
        assert_eq!(checksum_for("abc  yt-dlp\nDEF *yt-dlp.exe\n", "yt-dlp.exe").as_deref(), Some("def"));
    }
}
