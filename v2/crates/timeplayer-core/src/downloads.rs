//! Saving videos or their audio (1.x's `DownloadService`): into Downloads/TimePlayer (or the folder in
//! Settings), MP3 and full-quality video with ffmpeg, M4A and a single MP4 without; whole lists into a
//! numbered folder; and age-restricted videos tried again with the YouTube sign-in, as playback does.

use std::path::{Path, PathBuf};
use std::sync::OnceLock;

use crate::account::Account;
use crate::config::{QualityPreference, Settings};
use crate::error::{AppError, Result};
use crate::http::Cancel;
use crate::t;
use crate::ytdlp::{DownloadOptions, DownloadProgress, YtDlp};

/// What YouTube says when a video needs a signed-in viewer.
const SIGN_IN_ERRORS: [&str; 3] = ["AGE_RESTRICTED", "PRIVATE_VIDEO", "COOKIES_REQUIRED"];

#[derive(Clone)]
pub struct Downloads {
    ytdlp: YtDlp,
    settings: Settings,
    account: Option<Account>,
}

impl Downloads {
    pub fn new(ytdlp: YtDlp, settings: Settings, account: Option<Account>) -> Self {
        Self { ytdlp, settings, account }
    }

    pub fn folder(&self) -> PathBuf {
        self.settings
            .with(|c| c.backend.yt_dlp_download_directory.clone())
            .filter(|d| !d.trim().is_empty())
            .map(PathBuf::from)
            .unwrap_or_else(|| dirs::download_dir().unwrap_or_else(|| dirs::home_dir().unwrap_or_default().join("Downloads")).join("TimePlayer"))
    }

    /// ffmpeg is there, so audio can be MP3 and video full quality.
    pub fn can_convert(&self) -> bool {
        ffmpeg_folder().is_some()
    }

    pub fn download(&self, video_id: &str, audio_only: bool, progress: Option<&(dyn Fn(DownloadProgress) + Sync)>, cancel: &Cancel) -> Result<PathBuf> {
        self.run(video_id, audio_only, self.folder(), "%(title)s.%(ext)s".into(), progress, cancel)
    }

    /// One video of a whole list, as "003 - Title" in a folder named after the list.
    pub fn download_into_folder(
        &self,
        video_id: &str,
        audio_only: bool,
        folder_name: &str,
        number: usize,
        progress: Option<&(dyn Fn(DownloadProgress) + Sync)>,
        cancel: &Cancel,
    ) -> Result<PathBuf> {
        self.run(video_id, audio_only, self.folder().join(safe_folder_name(folder_name)), format!("{number:03} - %(title)s.%(ext)s"), progress, cancel)
    }

    fn run(&self, video_id: &str, audio_only: bool, folder: PathBuf, pattern: String, progress: Option<&(dyn Fn(DownloadProgress) + Sync)>, cancel: &Cancel) -> Result<PathBuf> {
        if !crate::links::is_video_id(video_id) {
            return Err(AppError::new("INVALID_ID", "This is not a YouTube video."));
        }
        let quality = match self.settings.with(|c| c.playback.preferred_quality) {
            QualityPreference::AudioOnly => QualityPreference::Auto,
            q => q,
        };
        let mut options = DownloadOptions {
            output_directory: folder,
            audio_only,
            quality,
            file_name_pattern: pattern,
            ffmpeg_location: ffmpeg_folder(),
            cookies_file: None,
        };
        let first = self.ytdlp.download(video_id, &options, progress, cancel);
        let error = match first {
            Ok(path) => return Ok(path),
            Err(e) if !SIGN_IN_ERRORS.contains(&e.code.as_str()) => return Err(e),
            Err(e) => e,
        };
        let Some(account) = self.account.as_ref().filter(|a| a.for_restricted_videos()) else {
            return Err(AppError::new(
                &error.code,
                t!("YouTube only gives this video to signed-in viewers. Sign in at Settings, YouTube account, and TimePlayer uses it for videos like this one."),
            ));
        };
        let cookies = account.write_temporary_cookies_file();
        options.cookies_file = cookies.clone();
        let result = self.ytdlp.download(video_id, &options, progress, cancel);
        if let Some(file) = cookies {
            let _ = std::fs::remove_file(file);
        }
        result
    }
}

const RESERVED: [&str; 22] = [
    "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5",
    "LPT6", "LPT7", "LPT8", "LPT9",
];

/// A playlist or channel name as a folder name every system accepts.
pub fn safe_folder_name(name: &str) -> String {
    let invalid = |c: char| c.is_control() || matches!(c, '<' | '>' | ':' | '"' | '/' | '\\' | '|' | '?' | '*');
    let mut safe: String = name.chars().map(|c| if invalid(c) { '_' } else { c }).collect();
    safe = safe.trim().trim_end_matches(['.', ' ']).to_owned();
    if safe.chars().count() > 80 {
        safe = safe.chars().take(80).collect::<String>().trim_end_matches(['.', ' ']).to_owned();
    }
    if safe.is_empty() {
        return "TimePlayer list".into();
    }
    // Names Windows keeps for devices (a playlist called "Con" or "Aux").
    let stem = safe.split('.').next().unwrap_or("");
    if RESERVED.iter().any(|r| r.eq_ignore_ascii_case(stem)) { format!("_{safe}") } else { safe }
}

/// The folder with ffmpeg in it: next to TimePlayer, TimePlayer's tools folder, or anywhere on the PATH.
pub fn ffmpeg_folder() -> Option<PathBuf> {
    static FOUND: OnceLock<Option<PathBuf>> = OnceLock::new();
    FOUND
        .get_or_init(|| {
            let name = if cfg!(windows) { "ffmpeg.exe" } else { "ffmpeg" };
            let program = crate::paths::program_folder();
            let mut candidates = vec![program.join("bin"), program, crate::paths::data_folder().join("bin")];
            if let Some(path) = std::env::var_os("PATH") {
                candidates.extend(std::env::split_paths(&path));
            }
            candidates.into_iter().find(|dir| dir.join(name).is_file())
        })
        .clone()
}

pub fn open_folder(path: &Path) {
    let _ = std::fs::create_dir_all(path);
    #[cfg(windows)]
    let _ = std::process::Command::new("explorer").arg(path).spawn();
    #[cfg(target_os = "macos")]
    let _ = std::process::Command::new("open").arg(path).spawn();
    #[cfg(all(unix, not(target_os = "macos")))]
    let _ = std::process::Command::new("xdg-open").arg(path).spawn();
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{AccountUse, AppConfig};
    use crate::http::Http;
    use crate::process::Output;
    use std::sync::{Arc, Mutex};

    #[test]
    fn names_become_safe_folders() {
        assert_eq!(safe_folder_name("Lofi: beats / to study?"), "Lofi_ beats _ to study_");
        assert_eq!(safe_folder_name("  ...  "), "TimePlayer list");
        assert_eq!(safe_folder_name("Ends with dot and space. ."), "Ends with dot and space");
        assert_eq!(safe_folder_name("con"), "_con");
    }

    fn setup(signed_in: bool) -> (Downloads, Arc<Mutex<Vec<Vec<String>>>>, PathBuf) {
        let folder = std::env::temp_dir().join(format!("tp2-dl-{}", uuid::Uuid::new_v4().simple()));
        let mut config = AppConfig::default();
        config.backend.yt_dlp_download_directory = Some(folder.display().to_string());
        // A fake yt-dlp next to nothing: point the custom path at a file that exists.
        let fake = folder.join("yt-dlp-fake");
        std::fs::create_dir_all(&folder).unwrap();
        std::fs::write(&fake, b"").unwrap();
        config.backend.yt_dlp_custom_path = Some(fake.display().to_string());
        config.account.use_ = AccountUse::RestrictedOnly;
        let settings = Settings::in_memory(config);
        let calls = Arc::new(Mutex::new(Vec::new()));
        let seen = calls.clone();
        let runner = move |_: &Path, args: &[String]| -> crate::Result<Output> {
            seen.lock().unwrap().push(args.to_vec());
            if args.iter().any(|a| a == "--cookies") {
                Ok(Output { exit_code: 0, stdout: "[download] Destination: song.m4a\n".into(), ..Default::default() })
            } else {
                Ok(Output { exit_code: 1, stderr: "ERROR: [youtube] oVAj10cLw4k: Sign in to confirm your age.".into(), ..Default::default() })
            }
        };
        let account = Account::open_at(settings.clone(), folder.join("signin.dat"));
        if signed_in {
            account
                .accept(crate::account::parse(".youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\ta\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tb\n"))
                .unwrap();
        }
        let ytdlp = YtDlp::new(Http::with_transport(|_| Err(AppError::network("offline"))), settings.clone(), Arc::new(runner), Some(account.clone()));
        (Downloads::new(ytdlp, settings, Some(account)), calls, folder)
    }

    #[test]
    fn age_restricted_downloads_use_the_sign_in_then_delete_it() {
        let (downloads, calls, folder) = setup(true);
        let saved = downloads.download("oVAj10cLw4k", true, None, &Cancel::new()).unwrap();
        assert_eq!(saved, PathBuf::from("song.m4a"));
        let calls = calls.lock().unwrap();
        assert_eq!(calls.len(), 2);
        assert!(!calls[0].contains(&"--cookies".to_string()));
        let file = &calls[1][calls[1].iter().position(|a| a == "--cookies").unwrap() + 1];
        assert!(!Path::new(file).exists());
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn without_a_sign_in_it_says_where_to_sign_in() {
        let (downloads, calls, folder) = setup(false);
        let error = downloads.download("oVAj10cLw4k", true, None, &Cancel::new()).unwrap_err();
        assert!(error.message.contains("Settings, YouTube account"));
        assert_eq!(calls.lock().unwrap().len(), 1);
        let _ = std::fs::remove_dir_all(folder);
    }
}
