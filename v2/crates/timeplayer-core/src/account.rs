//! The optional YouTube sign-in, kept as browser cookies (1.x's `YouTubeAccount`). Only YouTube's and
//! Google's cookies are kept; they are stored encrypted (DPAPI on Windows, in the same file and with the
//! same entropy as 1.x, so its sign-in carries over; the system keyring elsewhere), are only ever sent to
//! youtube.com, and never appear in logs or problem reports. Sign out erases them.

use sha1::{Digest, Sha1};
use std::path::PathBuf;
use std::sync::{Arc, RwLock};

use crate::config::{AccountUse, Settings};
use crate::error::{AppError, Result};
use crate::paths;
use crate::t;

const ORIGIN: &str = "https://www.youtube.com";

/// One cookie of a sign-in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SignInCookie {
    pub domain: String,
    pub include_subdomains: bool,
    pub path: String,
    pub secure: bool,
    pub expires: i64,
    pub name: String,
    pub value: String,
}

#[derive(Clone)]
pub struct Account {
    cookies: Arc<RwLock<Vec<SignInCookie>>>,
    settings: Settings,
    store: PathBuf,
}

impl Account {
    pub fn open(settings: Settings) -> Self {
        Self::open_at(settings, paths::data_folder().join("youtube-signin.dat"))
    }

    pub fn open_at(settings: Settings, store: PathBuf) -> Self {
        let account = Self { cookies: Arc::default(), settings, store };
        if let Some(text) = platform::load(&account.store) {
            *account.cookies.write().unwrap() = parse(&text);
        }
        remove_leftover_cookie_files();
        account
    }

    pub fn is_signed_in(&self) -> bool {
        !self.cookies.read().unwrap().is_empty()
    }

    pub fn use_(&self) -> AccountUse {
        self.settings.with(|c| c.account.use_)
    }

    /// Signed in and allowed for restricted videos.
    pub fn for_restricted_videos(&self) -> bool {
        self.is_signed_in() && self.use_() != AccountUse::Off
    }

    /// Signed in and allowed for the account's pages and actions.
    pub fn for_account_features(&self) -> bool {
        self.is_signed_in() && self.use_() == AccountUse::Full
    }

    /// Reads a cookies.txt file (from the browser extension or another exporter).
    pub fn import_file(&self, path: &std::path::Path) -> Result<usize> {
        let text = std::fs::read_to_string(path).map_err(|e| AppError::new("SIGNIN_FILE", t!("Could not read that file: {0}", e)))?;
        self.accept(parse(&text))
    }

    /// Takes the cookies of a sign-in done in TimePlayer's sign-in window.
    pub fn import_cookies(&self, cookies: Vec<SignInCookie>) -> Result<usize> {
        self.accept(cookies)
    }

    pub(crate) fn accept(&self, cookies: Vec<SignInCookie>) -> Result<usize> {
        let kept: Vec<SignInCookie> = cookies.into_iter().filter(|c| is_youtube_or_google(&c.domain)).collect();
        let has = |names: &[&str]| kept.iter().any(|c| names.contains(&c.name.as_str()));
        if !has(&["SAPISID", "__Secure-3PAPISID"]) || !has(&["LOGIN_INFO", "SID", "__Secure-3PSID"]) {
            return Err(AppError::new(
                "SIGNIN_MISSING",
                t!("That doesn't contain a YouTube sign-in. Sign in to YouTube in your browser first, then try again."),
            ));
        }
        let count = kept.len();
        if let Err(e) = platform::save(&self.store, &format(&kept)) {
            log::warn!("The YouTube sign-in could not be saved: {e}");
        }
        *self.cookies.write().unwrap() = kept;
        log::info!("Signed in to YouTube ({count} cookies kept).");
        Ok(count)
    }

    pub fn sign_out(&self) {
        self.cookies.write().unwrap().clear();
        platform::forget(&self.store);
        log::info!("Signed out of YouTube.");
    }

    /// Headers that make an InnerTube request signed in; empty when not signed in.
    pub fn request_headers(&self, now_unix: i64) -> Vec<(String, String)> {
        let cookies = self.cookies.read().unwrap();
        let mut seen = std::collections::HashSet::new();
        let youtube: Vec<&SignInCookie> = cookies.iter().filter(|c| is_youtube(&c.domain) && seen.insert(c.name.clone())).collect();
        if youtube.is_empty() {
            return Vec::new();
        }
        let mut headers = vec![
            ("Cookie".to_owned(), youtube.iter().map(|c| format!("{}={}", c.name, c.value)).collect::<Vec<_>>().join("; ")),
            ("Origin".into(), ORIGIN.into()),
            ("X-Origin".into(), ORIGIN.into()),
            ("X-Goog-AuthUser".into(), "0".into()),
            ("X-Youtube-Bootstrap-Logged-In".into(), "true".into()),
        ];
        if let Some(auth) = authorization(&youtube, now_unix) {
            headers.push(("Authorization".into(), auth));
        }
        headers
    }

    /// A temporary cookies file for yt-dlp; the caller deletes it. None when not signed in.
    pub fn write_temporary_cookies_file(&self) -> Option<PathBuf> {
        let cookies = self.cookies.read().unwrap();
        if cookies.is_empty() {
            return None;
        }
        let path = std::env::temp_dir().join(format!("timeplayer-yt-{}.txt", uuid::Uuid::new_v4().simple()));
        std::fs::write(&path, format(&cookies)).ok()?;
        Some(path)
    }
}

/// YouTube's SAPISIDHASH: SHA-1 of "time SAPISID origin", for each kind of session cookie there is.
pub fn authorization(cookies: &[&SignInCookie], now_unix: i64) -> Option<String> {
    let value = |name: &str| cookies.iter().find(|c| c.name == name).map(|c| c.value.clone());
    let mut parts = Vec::new();
    for (scheme, sid) in [
        ("SAPISIDHASH", value("SAPISID").or_else(|| value("__Secure-3PAPISID"))),
        ("SAPISID1PHASH", value("__Secure-1PAPISID")),
        ("SAPISID3PHASH", value("__Secure-3PAPISID")),
    ] {
        let Some(sid) = sid.filter(|s| !s.is_empty()) else { continue };
        let hash = hex::encode(Sha1::digest(format!("{now_unix} {sid} {ORIGIN}").as_bytes()));
        parts.push(format!("{scheme} {now_unix}_{hash}"));
    }
    (!parts.is_empty()).then(|| parts.join(" "))
}

/// The Netscape cookies.txt format that browsers' exporters and yt-dlp use.
pub fn parse(text: &str) -> Vec<SignInCookie> {
    text.replace("\r\n", "\n")
        .split('\n')
        .filter_map(|raw| {
            let line = raw.strip_prefix("#HttpOnly_").unwrap_or(raw);
            if line.is_empty() || line.starts_with('#') {
                return None;
            }
            let f: Vec<&str> = line.split('\t').collect();
            if f.len() < 7 {
                return None;
            }
            Some(SignInCookie {
                domain: f[0].into(),
                include_subdomains: f[1].eq_ignore_ascii_case("TRUE"),
                path: f[2].into(),
                secure: f[3].eq_ignore_ascii_case("TRUE"),
                expires: f[4].parse().unwrap_or(0),
                name: f[5].into(),
                value: f[6].trim_end().into(),
            })
        })
        .collect()
}

pub fn format(cookies: &[SignInCookie]) -> String {
    let mut out = String::from("# Netscape HTTP Cookie File\n");
    for c in cookies {
        out.push_str(&format!(
            "{}\t{}\t{}\t{}\t{}\t{}\t{}\n",
            c.domain,
            if c.include_subdomains { "TRUE" } else { "FALSE" },
            c.path,
            if c.secure { "TRUE" } else { "FALSE" },
            c.expires,
            c.name,
            c.value
        ));
    }
    out
}

fn is_youtube(domain: &str) -> bool {
    let d = domain.to_lowercase();
    d.trim_start_matches('.') == "youtube.com" || d.ends_with(".youtube.com")
}

fn is_youtube_or_google(domain: &str) -> bool {
    let d = domain.to_lowercase();
    is_youtube(domain) || d.trim_start_matches('.') == "google.com" || d.ends_with(".google.com")
}

/// The temporary cookie files are deleted right after use; if TimePlayer was closed in between, this
/// removes them at the next start, so a sign-in never stays in the temp folder unencrypted.
pub fn remove_leftover_cookie_files() {
    let Ok(entries) = std::fs::read_dir(std::env::temp_dir()) else { return };
    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().into_owned();
        if (name.starts_with("timeplayer-yt-") || name.starts_with("timeplayer-signin-")) && name.ends_with(".txt") {
            let _ = std::fs::remove_file(entry.path());
        }
    }
}

#[cfg(windows)]
mod platform {
    use std::path::Path;
    use windows_sys::Win32::Foundation::LocalFree;
    use windows_sys::Win32::Security::Cryptography::{CRYPT_INTEGER_BLOB, CryptProtectData, CryptUnprotectData};

    /// The same entropy as 1.x's `ProtectedData`, so its saved sign-in reads here.
    const ENTROPY: &[u8] = b"TimePlayer YouTube sign-in";

    fn blob(data: &[u8]) -> CRYPT_INTEGER_BLOB {
        CRYPT_INTEGER_BLOB { cbData: data.len() as u32, pbData: data.as_ptr().cast_mut() }
    }

    fn take(out: CRYPT_INTEGER_BLOB) -> Vec<u8> {
        // SAFETY: DPAPI allocated cbData bytes at pbData with LocalAlloc; they're copied, then freed.
        unsafe {
            let bytes = std::slice::from_raw_parts(out.pbData, out.cbData as usize).to_vec();
            LocalFree(out.pbData.cast());
            bytes
        }
    }

    pub fn load(store: &Path) -> Option<String> {
        let encrypted = std::fs::read(store).ok()?;
        let (input, entropy) = (blob(&encrypted), blob(ENTROPY));
        let mut out = CRYPT_INTEGER_BLOB { cbData: 0, pbData: std::ptr::null_mut() };
        // SAFETY: the blobs point at live buffers for the length given; DPAPI only reads them.
        let ok = unsafe { CryptUnprotectData(&input, std::ptr::null_mut(), &entropy, std::ptr::null(), std::ptr::null(), 0, &mut out) };
        if ok == 0 {
            log::warn!("The saved YouTube sign-in could not be read; sign in again.");
            return None;
        }
        String::from_utf8(take(out)).ok()
    }

    pub fn save(store: &Path, text: &str) -> Result<(), String> {
        let (input, entropy) = (blob(text.as_bytes()), blob(ENTROPY));
        let mut out = CRYPT_INTEGER_BLOB { cbData: 0, pbData: std::ptr::null_mut() };
        // SAFETY: as in load.
        let ok = unsafe { CryptProtectData(&input, std::ptr::null(), &entropy, std::ptr::null(), std::ptr::null(), 0, &mut out) };
        if ok == 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        crate::paths::write_atomic(store, &take(out)).map_err(|e| e.to_string())
    }

    pub fn forget(store: &Path) {
        let _ = std::fs::remove_file(store);
    }
}

#[cfg(not(windows))]
mod platform {
    use std::path::Path;

    fn entry() -> keyring::Result<keyring::Entry> {
        keyring::Entry::new("TimePlayer", "YouTube sign-in")
    }

    pub fn load(_store: &Path) -> Option<String> {
        entry().and_then(|e| e.get_password()).ok()
    }

    pub fn save(_store: &Path, text: &str) -> Result<(), String> {
        entry().and_then(|e| e.set_password(text)).map_err(|e| e.to_string())
    }

    pub fn forget(_store: &Path) {
        if let Ok(e) = entry() {
            let _ = e.delete_credential();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::AppConfig;

    const COOKIES: &str = "# Netscape HTTP Cookie File\n\
        .youtube.com\tTRUE\t/\tTRUE\t1999999999\tSAPISID\tabc123\n\
        #HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t1999999999\tLOGIN_INFO\tlogin\n\
        .youtube.com\tTRUE\t/\tTRUE\t1999999999\t__Secure-3PAPISID\tthree\n\
        .google.com\tTRUE\t/\tTRUE\t1999999999\tSID\tgoogleSid\n\
        .bank.example\tTRUE\t/\tTRUE\t1999999999\tsession\tSECRET\n";

    fn account(use_: AccountUse) -> (Account, PathBuf) {
        let mut config = AppConfig::default();
        config.account.use_ = use_;
        let store = std::env::temp_dir().join(format!("tp2-signin-{}.dat", uuid::Uuid::new_v4().simple()));
        (Account::open_at(Settings::in_memory(config), store.clone()), store)
    }

    #[test]
    fn keeps_only_youtube_and_google_and_hashes_like_youtube() {
        let (a, store) = account(AccountUse::Full);
        assert_eq!(a.accept(parse(COOKIES)).unwrap(), 4);
        let headers = a.request_headers(1_700_000_000);
        let cookie = &headers.iter().find(|(n, _)| n == "Cookie").unwrap().1;
        assert!(cookie.contains("SAPISID=abc123") && !cookie.contains("googleSid") && !cookie.contains("SECRET"));
        let auth = &headers.iter().find(|(n, _)| n == "Authorization").unwrap().1;
        let expected = hex::encode(Sha1::digest(b"1700000000 abc123 https://www.youtube.com"));
        assert!(auth.starts_with(&format!("SAPISIDHASH 1700000000_{expected}")));
        assert!(auth.contains("SAPISID3PHASH"));
        #[cfg(windows)]
        {
            let saved = std::fs::read(&store).unwrap();
            assert!(!String::from_utf8_lossy(&saved).contains("abc123"), "never in plain text");
        }
        a.sign_out();
        assert!(!a.is_signed_in());
        let _ = std::fs::remove_file(store);
    }

    #[test]
    fn refuses_files_without_a_sign_in() {
        let (a, _) = account(AccountUse::Off);
        assert!(a.accept(parse(".youtube.com\tTRUE\t/\tTRUE\t0\tPREF\tx\n")).is_err());
        assert!(!a.for_restricted_videos());
    }
}
