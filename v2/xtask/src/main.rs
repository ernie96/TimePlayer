//! Packaging tasks. `cargo xtask macos` makes TimePlayer.app from the release build and zips it as
//! TimePlayer-macOS.zip, the file TimePlayer's updater on macOS looks for (it unzips it in place of the app).

use std::error::Error;
use std::path::{Path, PathBuf};

fn main() -> Result<(), Box<dyn Error>> {
    match std::env::args().nth(1).as_deref() {
        Some("macos") => macos(),
        _ => {
            println!("Tasks:");
            println!("    macos    TimePlayer.app and TimePlayer-macOS.zip from the release build (on macOS)");
            Ok(())
        }
    }
}

fn root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).parent().map(Path::to_path_buf).unwrap_or_default()
}

#[cfg(target_os = "macos")]
fn macos() -> Result<(), Box<dyn Error>> {
    use shipfitter::macos::{DeveloperId, MacApp, sign_bundle};

    let release = root().join("target/release");
    let binary = release.join("TimePlayer");
    if !binary.exists() {
        return Err("Build it first: cargo build --release -p timeplayer".into());
    }
    let app = MacApp {
        name: "TimePlayer",
        identifier: "io.github.ernie96.timeplayer",
        executable: "TimePlayer",
        version: env!("CARGO_PKG_VERSION"),
        // macOS 15 or later (Homebrew's libmpv inside is built for it), and timeplayer:// links open in TimePlayer.
        extra: "\t<key>LSMinimumSystemVersion</key>\n\t<string>15.0</string>\n\
\t<key>CFBundleURLTypes</key>\n\t<array>\n\t\t<dict>\n\t\t\t<key>CFBundleURLName</key>\n\t\t\t<string>TimePlayer link</string>\n\
\t\t\t<key>CFBundleURLSchemes</key>\n\t\t\t<array>\n\t\t\t\t<string>timeplayer</string>\n\t\t\t</array>\n\t\t</dict>\n\t</array>\n",
        ..MacApp::default()
    };
    let bundle = app.bundle(&release, &binary, &[], &[])?;
    bundle_mpv(&bundle)?;
    bundle_libmpv(&bundle)?;
    match DeveloperId::from_env() {
        Some(signer) => sign_bundle(&bundle, &[], &signer)?,
        None => {
            // The ad hoc signature made with the bundle doesn't cover mpv, added after it.
            let status = std::process::Command::new("codesign").args(["--force", "--deep", "--sign", "-"]).arg(&bundle).status()?;
            if !status.success() {
                return Err("codesign couldn't sign TimePlayer.app ad hoc".into());
            }
            println!("No signing identity, so TimePlayer.app is signed ad hoc.");
        }
    }
    println!("Built {}", bundle.display());
    let zip = release.join("TimePlayer-macOS.zip");
    let _ = std::fs::remove_file(&zip);
    let status = std::process::Command::new("ditto").args(["-c", "-k", "--keepParent"]).arg(&bundle).arg(&zip).status()?;
    if !status.success() {
        return Err("ditto couldn't zip TimePlayer.app".into());
    }
    println!("Built {}", zip.display());
    Ok(())
}

/// Copies mpv (MPV_APP: an mpv.app, from the build checked in build-v2-macos.yml) into
/// TimePlayer.app/Contents/Resources/mpv: only its program and the libraries beside it, not its mpv.conf,
/// which would ask for a window.
#[cfg(target_os = "macos")]
fn bundle_mpv(bundle: &Path) -> Result<(), Box<dyn Error>> {
    let Some(source) = std::env::var_os("MPV_APP").map(PathBuf::from) else {
        println!("No MPV_APP, so TimePlayer.app has no mpv of its own and needs one installed.");
        return Ok(());
    };
    let from = source.join("Contents/MacOS");
    let to = bundle.join("Contents/Resources/mpv");
    std::fs::create_dir_all(&to)?;
    for item in ["mpv", "lib"] {
        let status = std::process::Command::new("ditto").arg(from.join(item)).arg(to.join(item)).status()?;
        if !status.success() {
            return Err(format!("ditto couldn't copy mpv's {item}").into());
        }
    }
    println!("Added mpv to {}", to.display());
    Ok(())
}

/// Copies libmpv (LIBMPV: Homebrew's libmpv.2.dylib, installed by build-v2-macos.yml) into
/// TimePlayer.app/Contents/Frameworks, with the libraries it needs, pointed at each other there
/// (dylibbundler). TimePlayer loads it to draw video inside its own window.
#[cfg(target_os = "macos")]
fn bundle_libmpv(bundle: &Path) -> Result<(), Box<dyn Error>> {
    let Some(source) = std::env::var_os("LIBMPV").map(PathBuf::from) else {
        println!("No LIBMPV, so TimePlayer.app has no libmpv and videos play their sound only.");
        return Ok(());
    };
    let frameworks = bundle.join("Contents/Frameworks");
    std::fs::create_dir_all(&frameworks)?;
    let library = frameworks.join("libmpv.2.dylib");
    std::fs::copy(&source, &library)?;
    let mut permissions = std::fs::metadata(&library)?.permissions();
    std::os::unix::fs::PermissionsExt::set_mode(&mut permissions, 0o755);
    std::fs::set_permissions(&library, permissions)?;
    // Its own name still says Homebrew; name it after where it now sits.
    let renamed = std::process::Command::new("install_name_tool")
        .args(["-id", "@loader_path/libmpv.2.dylib"])
        .arg(&library)
        .status()?;
    if !renamed.success() {
        return Err("install_name_tool couldn't rename libmpv".into());
    }
    let status = std::process::Command::new("dylibbundler")
        .args(["-of", "-b", "-cd", "-x"])
        .arg(&library)
        .arg("-d")
        .arg(&frameworks)
        .args(["-p", "@loader_path/"])
        .status()?;
    if !status.success() {
        return Err("dylibbundler couldn't gather libmpv's libraries".into());
    }
    for entry in std::fs::read_dir(&frameworks)? {
        let path = entry?.path();
        if path.extension().is_some_and(|e| e == "dylib") {
            fix_rpaths(&path)?;
        }
    }
    println!("Added libmpv to {}", frameworks.display());
    Ok(())
}

/// dylibbundler turns each of a library's search paths into "@loader_path/", and macOS refuses to load a
/// library that lists one path twice. Keeps each path once, and keeps /usr/lib/swift, where macOS has the
/// Swift libraries mpv's Mac code uses.
#[cfg(target_os = "macos")]
fn fix_rpaths(library: &Path) -> Result<(), Box<dyn Error>> {
    let output = std::process::Command::new("otool").arg("-l").arg(library).output()?;
    let text = String::from_utf8_lossy(&output.stdout);
    let mut rpaths: Vec<String> = Vec::new();
    let mut in_rpath = false;
    for line in text.lines().map(str::trim) {
        if line.starts_with("cmd ") {
            in_rpath = line == "cmd LC_RPATH";
        } else if in_rpath && let Some(rest) = line.strip_prefix("path ") {
            rpaths.push(rest.rsplit_once(" (offset").map_or(rest, |(p, _)| p).to_owned());
        }
    }
    let had_rpaths = !rpaths.is_empty();
    let mut wanted: Vec<String> = Vec::new();
    for path in &rpaths {
        if !wanted.contains(path) {
            wanted.push(path.clone());
        }
    }
    if had_rpaths && !wanted.iter().any(|p| p == "/usr/lib/swift") {
        wanted.push("/usr/lib/swift".into());
    }
    if wanted == rpaths {
        return Ok(());
    }
    let tool = |args: &[&str]| -> Result<(), Box<dyn Error>> {
        let status = std::process::Command::new("install_name_tool").args(args).arg(library).status()?;
        if status.success() { Ok(()) } else { Err(format!("install_name_tool {args:?} failed on {}", library.display()).into()) }
    };
    for path in &rpaths {
        tool(&["-delete_rpath", path])?;
    }
    for path in &wanted {
        tool(&["-add_rpath", path])?;
    }
    let status = std::process::Command::new("codesign").args(["--force", "--sign", "-"]).arg(library).status()?;
    if !status.success() {
        return Err(format!("codesign failed on {}", library.display()).into());
    }
    println!("Search paths of {}: {}", library.display(), wanted.join(", "));
    Ok(())
}

#[cfg(not(target_os = "macos"))]
fn macos() -> Result<(), Box<dyn Error>> {
    let _ = root();
    Err("The macOS app can only be packaged on macOS.".into())
}
