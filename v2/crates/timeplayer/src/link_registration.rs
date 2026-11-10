//! Links from other apps (1.x's `LinkProtocol` and `NativeMessagingHost`): timeplayer:// links open in
//! TimePlayer, and the "Play in TimePlayer" browser extension starts TimePlayer as its native messaging host,
//! which passes the page's link to the open TimePlayer (or starts one) and ends.

use std::io::{Read, Write};
use std::path::PathBuf;

pub const HOST_NAME: &str = "com.timeplayer.links";
pub const EXTENSION_ID: &str = "lcfhohlccjpekkeeiggdjbcgbfedjijg";

fn manifest_path() -> PathBuf {
    timeplayer_core::paths::data_folder().join("native-messaging-host.json")
}

fn manifest(exe: &str) -> String {
    let value = serde_json::json!({
        "name": HOST_NAME,
        "description": "TimePlayer: plays YouTube links from the Play in TimePlayer extension",
        "path": exe,
        "type": "stdio",
        "allowed_origins": [format!("chrome-extension://{EXTENSION_ID}/")],
    });
    serde_json::to_string_pretty(&value).unwrap_or_default()
}

/// Registers (or removes) timeplayer:// links and the browser extension's host for this user.
pub fn apply(enabled: bool) -> Result<(), String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let exe = exe.display().to_string();
    let manifest_file = manifest_path();
    if enabled {
        let text = manifest(&exe);
        if std::fs::read_to_string(&manifest_file).ok().as_deref() != Some(text.as_str()) {
            timeplayer_core::paths::write_atomic(&manifest_file, text.as_bytes()).map_err(|e| e.to_string())?;
        }
    } else {
        let _ = std::fs::remove_file(&manifest_file);
    }
    platform::apply(enabled, &exe, &manifest_file)
}

#[cfg(windows)]
mod platform {
    use std::path::Path;

    use windows::Win32::Foundation::ERROR_SUCCESS;
    use windows::Win32::System::Registry::{HKEY, HKEY_CURRENT_USER, KEY_READ, KEY_WRITE, REG_OPTION_NON_VOLATILE, REG_SZ, RegCloseKey, RegCreateKeyExW, RegDeleteTreeW, RegGetValueW, RegSetValueExW, RRF_RT_REG_SZ};
    use windows::core::{HSTRING, PCWSTR};

    const PROTOCOL_KEY: &str = r"Software\Classes\timeplayer";
    const BROWSER_KEYS: [&str; 3] = [r"Software\Google\Chrome\NativeMessagingHosts\", r"Software\Microsoft\Edge\NativeMessagingHosts\", r"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\"];

    fn read(key: &str) -> Option<String> {
        let mut buffer = vec![0u16; 2048];
        let mut size = (buffer.len() * 2) as u32;
        // SAFETY: the buffer and size are valid for the call.
        let status = unsafe { RegGetValueW(HKEY_CURRENT_USER, &HSTRING::from(key), PCWSTR::null(), RRF_RT_REG_SZ, None, Some(buffer.as_mut_ptr().cast()), Some(&mut size)) };
        if status != ERROR_SUCCESS {
            return None;
        }
        let len = (size as usize / 2).saturating_sub(1);
        Some(String::from_utf16_lossy(&buffer[..len]))
    }

    fn write(key: &str, name: Option<&str>, value: &str) -> Result<(), String> {
        let mut handle = HKEY::default();
        // SAFETY: Win32 registry calls with valid strings; the key is closed after use.
        unsafe {
            let status = RegCreateKeyExW(HKEY_CURRENT_USER, &HSTRING::from(key), None, PCWSTR::null(), REG_OPTION_NON_VOLATILE, KEY_WRITE | KEY_READ, None, &mut handle, None);
            if status != ERROR_SUCCESS {
                return Err(format!("registry error {}", status.0));
            }
            let wide: Vec<u16> = value.encode_utf16().chain(std::iter::once(0)).collect();
            let bytes = std::slice::from_raw_parts(wide.as_ptr().cast::<u8>(), wide.len() * 2);
            let name = name.map(HSTRING::from);
            let status = RegSetValueExW(handle, name.as_ref().map(|n| PCWSTR(n.as_ptr())).unwrap_or(PCWSTR::null()), None, REG_SZ, Some(bytes));
            let _ = RegCloseKey(handle);
            if status != ERROR_SUCCESS {
                return Err(format!("registry error {}", status.0));
            }
        }
        Ok(())
    }

    fn delete(key: &str) {
        // SAFETY: deletes a key under the user's own hive; a missing key is fine.
        unsafe {
            let _ = RegDeleteTreeW(HKEY_CURRENT_USER, &HSTRING::from(key));
        }
    }

    pub fn apply(enabled: bool, exe: &str, manifest: &Path) -> Result<(), String> {
        let host_keys: Vec<String> = BROWSER_KEYS.iter().map(|k| format!("{k}{}", super::HOST_NAME)).collect();
        if !enabled {
            delete(PROTOCOL_KEY);
            for key in &host_keys {
                delete(key);
            }
            return Ok(());
        }
        let manifest = manifest.display().to_string();
        for key in &host_keys {
            if read(key).as_deref() != Some(&manifest) {
                write(key, None, &manifest)?;
            }
        }
        let command = format!("\"{exe}\" \"%1\"");
        if read(&format!(r"{PROTOCOL_KEY}\shell\open\command")).as_deref() == Some(&command) {
            return Ok(());
        }
        write(PROTOCOL_KEY, None, "URL:TimePlayer link")?;
        write(PROTOCOL_KEY, Some("URL Protocol"), "")?;
        write(&format!(r"{PROTOCOL_KEY}\DefaultIcon"), None, &format!("\"{exe}\",0"))?;
        write(&format!(r"{PROTOCOL_KEY}\shell\open\command"), None, &command)
    }
}

#[cfg(not(windows))]
mod platform {
    use std::path::{Path, PathBuf};

    /// Where Chrome, Chromium, Edge and Brave look for native messaging hosts of this user.
    fn host_folders() -> Vec<PathBuf> {
        let Some(home) = dirs::home_dir() else { return Vec::new() };
        if cfg!(target_os = "macos") {
            let support = home.join("Library/Application Support");
            ["Google/Chrome", "Chromium", "Microsoft Edge", "BraveSoftware/Brave-Browser"].iter().map(|b| support.join(b).join("NativeMessagingHosts")).collect()
        } else {
            let config = dirs::config_dir().unwrap_or_else(|| home.join(".config"));
            ["google-chrome", "chromium", "microsoft-edge", "BraveSoftware/Brave-Browser"].iter().map(|b| config.join(b).join("NativeMessagingHosts")).collect()
        }
    }

    pub fn apply(enabled: bool, exe: &str, manifest: &Path) -> Result<(), String> {
        let file_name = format!("{}.json", super::HOST_NAME);
        for folder in host_folders() {
            let target = folder.join(&file_name);
            if enabled {
                // Only for browsers that are installed (their profile folder exists).
                if folder.parent().is_some_and(Path::is_dir) {
                    let _ = std::fs::create_dir_all(&folder);
                    let _ = std::fs::copy(manifest, &target);
                }
            } else {
                let _ = std::fs::remove_file(&target);
            }
        }
        #[cfg(target_os = "linux")]
        linux_scheme(enabled, exe)?;
        #[cfg(target_os = "macos")]
        let _ = exe; // macOS takes timeplayer:// from the app bundle's Info.plist.
        Ok(())
    }

    /// A desktop entry that handles timeplayer:// links, registered with xdg-mime.
    #[cfg(target_os = "linux")]
    fn linux_scheme(enabled: bool, exe: &str) -> Result<(), String> {
        let Some(apps) = dirs::data_dir().map(|d| d.join("applications")) else { return Ok(()) };
        let file = apps.join("timeplayer-links.desktop");
        if !enabled {
            let _ = std::fs::remove_file(&file);
            return Ok(());
        }
        let exe = std::env::var("APPIMAGE").unwrap_or_else(|_| exe.to_owned());
        let entry = format!(
            "[Desktop Entry]\nType=Application\nName=TimePlayer\nExec=\"{exe}\" %u\nNoDisplay=true\nMimeType=x-scheme-handler/timeplayer;\n"
        );
        if std::fs::read_to_string(&file).ok().as_deref() == Some(entry.as_str()) {
            return Ok(());
        }
        std::fs::create_dir_all(&apps).map_err(|e| e.to_string())?;
        std::fs::write(&file, entry).map_err(|e| e.to_string())?;
        let _ = std::process::Command::new("xdg-mime").args(["default", "timeplayer-links.desktop", "x-scheme-handler/timeplayer"]).status();
        Ok(())
    }
}

/// The browser starts the host with the extension's origin among the arguments.
pub fn is_native_host_start(args: &[String]) -> bool {
    args.iter().any(|a| a.to_lowercase().starts_with("chrome-extension://"))
}

/// Reads the extension's one message, opens the link, answers and returns (1.x's `NativeMessagingHost.Run`).
pub fn run_native_host(args: &[String]) {
    let reply = match host_message(args) {
        Ok(None) => serde_json::json!({ "ok": false, "error": "not a YouTube video or playlist" }),
        Ok(Some(link)) => {
            if !crate::instance::send_to_open_copy(&link) {
                let started = std::env::current_exe().and_then(|exe| std::process::Command::new(exe).arg(&link).spawn());
                if let Err(e) = started {
                    let reply = serde_json::json!({ "ok": false, "error": e.to_string() });
                    write_message(&reply.to_string());
                    return;
                }
            }
            serde_json::json!({ "ok": true })
        }
        Err(e) => serde_json::json!({ "ok": false, "error": e }),
    };
    write_message(&reply.to_string());
}

fn host_message(args: &[String]) -> Result<Option<String>, String> {
    let origin = format!("chrome-extension://{EXTENSION_ID}/");
    if !args.iter().any(|a| a.to_lowercase().starts_with(&origin)) {
        return Err("not the TimePlayer extension".into());
    }
    let mut input = std::io::stdin();
    let mut header = [0u8; 4];
    input.read_exact(&mut header).map_err(|_| "the browser closed the connection".to_owned())?;
    let length = u32::from_ne_bytes(header) as usize;
    if length == 0 || length > 64 * 1024 {
        return Err("message too long".into());
    }
    let mut body = vec![0u8; length];
    input.read_exact(&mut body).map_err(|_| "the browser closed the connection".to_owned())?;
    let message: serde_json::Value = serde_json::from_slice(&body).map_err(|e| e.to_string())?;
    let url = message.get("url").and_then(|u| u.as_str()).unwrap_or_default();
    Ok(timeplayer_core::links::normalize_incoming(url))
}

fn write_message(json: &str) {
    let mut output = std::io::stdout();
    let body = json.as_bytes();
    let _ = output.write_all(&(body.len() as u32).to_ne_bytes());
    let _ = output.write_all(body);
    let _ = output.flush();
}
