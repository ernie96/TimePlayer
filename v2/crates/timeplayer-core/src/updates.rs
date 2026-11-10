//! Updates from GitHub releases (1.x's `UpdateService` and `UpdateSignature`): the same release assets,
//! Ed25519 key and signed message as 1.x, so 1.x installs 2.0 by itself and 2.0 keeps updating the same way.
//! Windows runs the installer (or unpacks the portable zip) once TimePlayer has closed; Linux replaces the
//! AppImage; macOS replaces the app from its zip.

use base64::Engine as _;
use ed25519_dalek::{Signature, Verifier, VerifyingKey};
use serde_json::Value;
use std::path::{Path, PathBuf};
use std::time::Duration;

use crate::config::{OFFICIAL_REPOSITORY, Settings};
use crate::error::{AppError, Result};
use crate::http::{Http, Request};
use crate::paths;
use crate::ytdlp::sha256_hex;

/// The public half of TimePlayer's release key (the private half never leaves the release machine).
pub const PUBLIC_KEY: &str = "e0cquDjbkNHNi/BFw0vG25dCmRpi/GutstuyMoTPCYI=";
pub const INSTALLER_ASSET: &str = "TimePlayer_setup.exe";
pub const PORTABLE_ASSET: &str = "TimePlayer.zip";
pub const APPIMAGE_ASSET: &str = "TimePlayer-x86_64.AppImage";
pub const MACOS_ASSET: &str = "TimePlayer-macOS.zip";

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InstallKind {
    /// Installed with TimePlayer_setup.exe (an uninstaller sits next to it).
    Installer,
    /// The Windows zip, unpacked anywhere.
    Portable,
    AppImage,
    MacApp,
}

#[derive(Debug, Clone, PartialEq, PartialOrd, Eq, Ord)]
pub struct Version(pub u32, pub u32, pub u32);

impl std::fmt::Display for Version {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}.{}.{}", self.0, self.1, self.2)
    }
}

/// "v1.12.1", "2.0.0-beta" (only 2.0.0 counts) → a version.
pub fn parse_version(text: &str) -> Option<Version> {
    let core = text.trim().trim_start_matches(['v', 'V']);
    let core = core.split(['-', '+', ' ']).next()?;
    let mut parts = core.split('.').map(|p| p.parse::<u32>());
    let major = parts.next()?.ok()?;
    let minor = parts.next().unwrap_or(Ok(0)).ok()?;
    let patch = parts.next().unwrap_or(Ok(0)).ok()?;
    Some(Version(major, minor, patch))
}

pub fn current_version() -> Version {
    parse_version(paths::version()).unwrap_or(Version(0, 0, 0))
}

pub fn install_kind() -> InstallKind {
    if cfg!(target_os = "macos") {
        InstallKind::MacApp
    } else if cfg!(target_os = "linux") {
        InstallKind::AppImage
    } else if paths::program_folder().join("unins000.exe").is_file() {
        InstallKind::Installer
    } else {
        InstallKind::Portable
    }
}

fn asset_for(kind: InstallKind) -> &'static str {
    match kind {
        InstallKind::Installer => INSTALLER_ASSET,
        InstallKind::Portable => PORTABLE_ASSET,
        InstallKind::AppImage => APPIMAGE_ASSET,
        InstallKind::MacApp => MACOS_ASSET,
    }
}

#[derive(Debug, Clone)]
pub struct UpdateInfo {
    pub version: Version,
    /// The changelog section; TimePlayer reads it out when it offers the update.
    pub notes: String,
    pub asset_name: String,
    pub download_url: String,
    pub signature_url: String,
}

/// "TimePlayer-update-v1:{asset}:{sha256 hex}", the message 1.x signs and checks.
pub fn signed_message(asset: &str, sha256: &str) -> String {
    format!("TimePlayer-update-v1:{asset}:{}", sha256.to_lowercase())
}

pub fn verify(public_key_b64: &str, file_bytes: &[u8], asset: &str, signature_b64: &str) -> bool {
    let engine = base64::engine::general_purpose::STANDARD;
    let (Ok(key), Ok(sig)) = (engine.decode(public_key_b64.trim()), engine.decode(signature_b64.trim())) else { return false };
    let (Ok(key), Ok(sig)) = (<[u8; 32]>::try_from(key.as_slice()), <[u8; 64]>::try_from(sig.as_slice())) else { return false };
    let Ok(key) = VerifyingKey::from_bytes(&key) else { return false };
    key.verify(signed_message(asset, &sha256_hex(file_bytes)).as_bytes(), &Signature::from_bytes(&sig)).is_ok()
}

/// A repository from Settings, or the official one when it's missing, malformed or a placeholder.
pub fn effective_repository(configured: &str) -> String {
    let value = configured.trim();
    let parts: Vec<&str> = value.split('/').collect();
    let well_formed = parts.len() == 2 && parts.iter().all(|p| !p.is_empty() && p.chars().all(|c| c.is_ascii_alphanumeric() || "-_.".contains(c)));
    if !well_formed || parts[0].eq_ignore_ascii_case("OWNER") { OFFICIAL_REPOSITORY.into() } else { value.into() }
}

/// The latest release, if it's newer than this one; None when up to date.
pub fn check(http: &Http, settings: &Settings) -> Result<Option<UpdateInfo>> {
    let mut repository = effective_repository(&settings.with(|c| c.update.repository.clone()));
    let get = |repo: &str| {
        http.send(
            Request::get(format!("https://api.github.com/repos/{repo}/releases/latest"))
                .timeout(Duration::from_secs(15))
                .header("User-Agent", &format!("TimePlayer/{}", paths::version()))
                .header("Accept", "application/vnd.github+json"),
        )
    };
    let mut response = get(&repository).map_err(|e| AppError::new("UPDATE_NETWORK", format!("Could not check for updates: {e}")))?;
    if response.status == 404 && repository != OFFICIAL_REPOSITORY {
        log::warn!("Update repository {repository} was not found; checking {OFFICIAL_REPOSITORY} instead.");
        repository = OFFICIAL_REPOSITORY.into();
        response = get(&repository).map_err(|e| AppError::new("UPDATE_NETWORK", format!("Could not check for updates: {e}")))?;
    }
    if !response.ok() {
        return Err(AppError::new("UPDATE_HTTP", format!("GitHub answered {} when checking for updates.", response.status)));
    }
    let release = response.json()?;
    let Some(latest) = release.get("tag_name").and_then(Value::as_str).and_then(parse_version) else {
        return Err(AppError::new("UPDATE_BAD_RELEASE", "The latest release has no usable version number."));
    };
    log::info!("Update check: running {}, latest release {latest}.", current_version());
    if latest <= current_version() {
        return Ok(None);
    }
    pick_assets(&release, latest.clone(), install_kind())
        .map(Some)
        .ok_or_else(|| AppError::new("UPDATE_NO_ASSET", format!("TimePlayer {latest} is out, but its download for this kind of install is missing.")))
}

pub fn pick_assets(release: &Value, version: Version, kind: InstallKind) -> Option<UpdateInfo> {
    let name = asset_for(kind);
    let assets = release.get("assets")?.as_array()?;
    let url_of = |wanted: &str| {
        assets.iter().find(|a| a.get("name").and_then(Value::as_str).is_some_and(|n| n.eq_ignore_ascii_case(wanted))).and_then(|a| {
            a.get("browser_download_url").and_then(Value::as_str).map(str::to_owned)
        })
    };
    Some(UpdateInfo {
        version,
        notes: release.get("body").and_then(Value::as_str).unwrap_or("").to_owned(),
        asset_name: name.into(),
        download_url: url_of(name)?,
        signature_url: url_of(&format!("{name}.sig"))?,
    })
}

fn update_folder() -> PathBuf {
    std::env::temp_dir().join("TimePlayerUpdate")
}

/// Downloads the update and checks its signature; the checked file.
pub fn download(http: &Http, update: &UpdateInfo, progress: &dyn Fn(f64)) -> Result<PathBuf> {
    let folder = update_folder();
    std::fs::create_dir_all(&folder)?;
    let target = folder.join(&update.asset_name);
    let signature = http.get_text(&update.signature_url, Duration::from_secs(30))?;
    progress(0.1);
    let bytes = http.get_ok(Request::get(&update.download_url).timeout(Duration::from_secs(900)))?.body;
    progress(0.9);
    if !verify(PUBLIC_KEY, &bytes, &update.asset_name, &signature) {
        log::error!("Update {} failed signature verification; discarded.", update.version);
        return Err(AppError::new("UPDATE_BAD_SIGNATURE", "The downloaded update is not signed by TimePlayer's publisher, so it was not installed."));
    }
    std::fs::write(&target, &bytes)?;
    progress(1.0);
    log::info!("Update {} downloaded and verified: {}", update.version, target.display());
    Ok(target)
}

pub fn failure_note_path() -> PathBuf {
    update_folder().join("install-failed.txt")
}

/// The installer's exit code if the last update failed (said once, at the next start).
pub fn take_install_failure() -> Option<i32> {
    let path = failure_note_path();
    let text = std::fs::read_to_string(&path).ok()?;
    let _ = std::fs::remove_file(&path);
    Some(text.trim().parse().unwrap_or(-1))
}

/// PowerShell single quotes, doubling curly apostrophes too (a user name such as O’Brien).
pub fn ps_quote(value: &str) -> String {
    let mut out = String::from("'");
    for c in value.chars() {
        out.push(c);
        if matches!(c, '\'' | '\u{2018}' | '\u{2019}' | '\u{201A}' | '\u{201B}') {
            out.push(c);
        }
    }
    out.push('\'');
    out
}

pub fn installer_script(pid: u32, installer: &Path, exe: &Path) -> String {
    format!(
        "Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; \
         $setup = Start-Process -FilePath {} -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/CLOSEAPPLICATIONS' -Wait -PassThru; \
         if ($setup.ExitCode -ne 0) {{ Set-Content -LiteralPath {} -Value $setup.ExitCode }}; \
         Remove-Item -LiteralPath {} -Force -ErrorAction SilentlyContinue; \
         Start-Process -FilePath {}",
        ps_quote(&installer.display().to_string()),
        ps_quote(&failure_note_path().display().to_string()),
        ps_quote(&installer.display().to_string()),
        ps_quote(&exe.display().to_string())
    )
}

pub fn portable_script(pid: u32, zip: &Path, program_folder: &Path, exe: &Path) -> String {
    let staging = zip.parent().unwrap_or(Path::new(".")).join("TimePlayerUpdate-unpacked");
    let q = |p: &Path| ps_quote(&p.display().to_string());
    format!(
        "Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; \
         Remove-Item -LiteralPath {s} -Recurse -Force -ErrorAction SilentlyContinue; \
         Expand-Archive -LiteralPath {z} -DestinationPath {s} -Force; \
         $source = Join-Path {s} 'TimePlayer'; if (-not (Test-Path $source)) {{ $source = {s} }}; \
         for ($i = 0; $i -lt 15; $i++) {{ try {{ Copy-Item -Path (Join-Path $source '*') -Destination {p} -Recurse -Force -ErrorAction Stop; break }} catch {{ Start-Sleep -Seconds 1 }} }}; \
         Remove-Item -LiteralPath {s} -Recurse -Force -ErrorAction SilentlyContinue; \
         Remove-Item -LiteralPath {z} -Force -ErrorAction SilentlyContinue; \
         Start-Process -FilePath {e}",
        s = q(&staging),
        z = q(zip),
        p = q(program_folder),
        e = q(exe)
    )
}

fn sh_quote(value: &str) -> String {
    format!("'{}'", value.replace('\'', "'\\''"))
}

/// Starts the helper that installs the update once TimePlayer has closed; the app quits right after.
pub fn start_install(file: &Path) -> Result<()> {
    let exe = std::env::current_exe().unwrap_or_else(|_| paths::program_folder().join("TimePlayer.exe"));
    let pid = std::process::id();
    let mut command = match install_kind() {
        InstallKind::Installer | InstallKind::Portable => {
            let script = if install_kind() == InstallKind::Installer {
                installer_script(pid, file, &exe)
            } else {
                portable_script(pid, file, &paths::program_folder(), &exe)
            };
            let mut c = std::process::Command::new("powershell.exe");
            c.args(["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", &script]);
            c
        }
        InstallKind::AppImage => {
            let target = std::env::var("APPIMAGE").map(PathBuf::from).unwrap_or(exe.clone());
            let script = format!(
                "while kill -0 {pid} 2>/dev/null; do sleep 1; done; chmod +x {f}; mv -f {f} {t} && exec {t}",
                f = sh_quote(&file.display().to_string()),
                t = sh_quote(&target.display().to_string())
            );
            let mut c = std::process::Command::new("sh");
            c.args(["-c", &script]);
            c
        }
        InstallKind::MacApp => {
            // TimePlayer.app/Contents/MacOS/TimePlayer → TimePlayer.app
            let app = exe.ancestors().nth(3).map(Path::to_path_buf).unwrap_or_else(|| exe.clone());
            let parent = app.parent().unwrap_or(Path::new("/Applications")).to_path_buf();
            let script = format!(
                "while kill -0 {pid} 2>/dev/null; do sleep 1; done; rm -rf {a} && ditto -x -k {f} {p} && rm -f {f} && open {a}",
                a = sh_quote(&app.display().to_string()),
                f = sh_quote(&file.display().to_string()),
                p = sh_quote(&parent.display().to_string())
            );
            let mut c = std::process::Command::new("sh");
            c.args(["-c", &script]);
            c
        }
    };
    crate::process::hide_window(&mut command);
    command.spawn().map(|_| ()).map_err(|e| AppError::new("UPDATE_INSTALL", format!("Could not start the update: {e}")))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_compare_like_1x() {
        assert!(parse_version("v1.12.1").unwrap() < parse_version("2.0.0-beta").unwrap());
        assert_eq!(parse_version("1.2"), Some(Version(1, 2, 0)));
        assert_eq!(effective_repository("OWNER/REPO"), OFFICIAL_REPOSITORY);
        assert_eq!(effective_repository("someone/fork"), "someone/fork");
    }

    #[test]
    fn signatures_from_1x_check_out() {
        use ed25519_dalek::{Signer, SigningKey};
        let key = SigningKey::from_bytes(&[7u8; 32]);
        let public = base64::engine::general_purpose::STANDARD.encode(key.verifying_key().to_bytes());
        let file = b"installer bytes";
        let sig = key.sign(signed_message("TimePlayer_setup.exe", &sha256_hex(file)).as_bytes());
        let sig = base64::engine::general_purpose::STANDARD.encode(sig.to_bytes());
        assert!(verify(&public, file, "TimePlayer_setup.exe", &sig));
        assert!(!verify(&public, b"tampered", "TimePlayer_setup.exe", &sig));
        assert!(!verify(&public, file, "TimePlayer.zip", &sig));
    }

    #[test]
    fn quotes_curly_apostrophes() {
        assert_eq!(ps_quote("C:\\Users\\O\u{2019}Brien"), "'C:\\Users\\O\u{2019}\u{2019}Brien'");
    }
}
