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
        // macOS 13 or later, and timeplayer:// links open in TimePlayer.
        extra: "\t<key>LSMinimumSystemVersion</key>\n\t<string>13.0</string>\n\
\t<key>CFBundleURLTypes</key>\n\t<array>\n\t\t<dict>\n\t\t\t<key>CFBundleURLName</key>\n\t\t\t<string>TimePlayer link</string>\n\
\t\t\t<key>CFBundleURLSchemes</key>\n\t\t\t<array>\n\t\t\t\t<string>timeplayer</string>\n\t\t\t</array>\n\t\t</dict>\n\t</array>\n",
        ..MacApp::default()
    };
    let bundle = app.bundle(&release, &binary, &[], &[])?;
    match DeveloperId::from_env() {
        Some(signer) => sign_bundle(&bundle, &[], &signer)?,
        None => println!("No signing identity, so TimePlayer.app keeps its ad hoc signature."),
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

#[cfg(not(target_os = "macos"))]
fn macos() -> Result<(), Box<dyn Error>> {
    let _ = root();
    Err("The macOS app can only be packaged on macOS.".into())
}
