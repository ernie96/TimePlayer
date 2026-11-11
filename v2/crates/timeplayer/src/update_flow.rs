//! Checking for, downloading and installing a TimePlayer update (1.x's `UpdateFlow`): the quiet check after
//! startup, repeated every 6 hours while TimePlayer stays open, offers a new version once (No skips it) and
//! waits to offer it until nothing would be interrupted; Check for updates in Settings always says what it found.

use timeplayer_core::i18n::{t, tf};
use timeplayer_core::mpv::PlaybackState;
use timeplayer_core::periodic::Tick;
use timeplayer_core::updates::{self, UpdateInfo};

use crate::{App, dialogs, speech};

fn version_text(v: &updates::Version) -> String {
    format!("{}.{}.{}", v.0, v.1, v.2)
}

/// The release notes without Markdown, at most 1500 characters, for reading aloud.
pub fn plain_text(markdown: &str) -> String {
    let mut lines = Vec::new();
    for line in markdown.replace('\r', "").lines() {
        let mut line = line.trim_start_matches('#').trim_start().to_owned();
        let trimmed = line.trim_start();
        if let Some(rest) = trimmed.strip_prefix("* ").or_else(|| trimmed.strip_prefix("- ")).or_else(|| trimmed.strip_prefix("+ ")) {
            line = format!("- {rest}");
        }
        lines.push(strip_links(&line).replace("**", "").replace("__", "").replace('`', ""));
    }
    let mut text = lines.join("\n");
    while text.contains("\n\n\n") {
        text = text.replace("\n\n\n", "\n\n");
    }
    let text = text.trim().to_owned();
    if text.chars().count() > 1500 { text.chars().take(1500).collect::<String>().trim_end().to_owned() + "..." } else { text }
}

/// "[words](link)" becomes "words".
fn strip_links(line: &str) -> String {
    let mut out = String::new();
    let mut rest = line;
    while let Some(open) = rest.find('[') {
        let Some(close) = rest[open..].find("](").map(|c| open + c) else { break };
        let Some(end) = rest[close..].find(')').map(|e| close + e) else { break };
        out.push_str(&rest[..open]);
        out.push_str(&rest[open + 1..close]);
        rest = &rest[end + 1..];
    }
    out.push_str(rest);
    out
}

impl App {
    /// Every minute: runs the repeated update check when it's due, and offers a version it found earlier
    /// once nothing would be interrupted.
    pub(crate) fn update_tick(&mut self) {
        if self.updating {
            return;
        }
        let enabled = self.settings.with(|c| c.update.check_at_startup);
        let idle = self.quiet_enough_for_update_offer();
        match self.update_schedule.tick(std::time::Instant::now(), enabled, idle) {
            Tick::Nothing => {}
            Tick::Check => {
                log::info!("Checking for TimePlayer updates (every 6 hours while open).");
                self.check_for_app_update(false);
            }
            Tick::Offer(update) => {
                // Skipped in the meantime (the start-up check's offer, say): stays skipped.
                if !self.settings.with(|c| c.update.skipped_version.as_deref() == Some(version_text(&update.version).as_str())) {
                    self.updating = true;
                    self.offer_update(update, false);
                }
            }
        }
    }

    /// No dialog now if TimePlayer isn't the window in front, a window of its own is open, or something is
    /// starting or being saved: a video opening or buffering, a download, an import.
    fn quiet_enough_for_update_offer(&self) -> bool {
        !self.importing
            && self.import_window.is_none()
            && self.settings_window.is_none()
            && self.sign_in_window.is_none()
            && self.report_window.is_none()
            && self.download_state.active == 0
            && !matches!(self.playing.state, PlaybackState::Opening | PlaybackState::Buffering)
            && speech::app_in_front()
    }

    /// `manual`: Check for updates in Settings. Otherwise the quiet check after startup.
    pub(crate) fn check_for_app_update(&mut self, manual: bool) {
        if self.updating {
            if manual {
                speech::say_quietly(&t("Already checking for updates."));
            }
            return;
        }
        self.updating = true;
        if manual {
            speech::say_quietly(&t("Checking for TimePlayer updates..."));
        }
        let http = self.http.clone();
        let settings = self.settings.clone();
        self.jobs.run("update check", move || {
            let check = updates::check(&http, &settings);
            move |app: &mut App| app.update_checked(check, manual)
        });
    }

    fn update_checked(&mut self, check: timeplayer_core::Result<Option<UpdateInfo>>, manual: bool) {
        let update = match check {
            Err(e) => {
                self.updating = false;
                log::warn!("Update check failed: {}", e.message);
                if manual {
                    speech::say(&if e.message.is_empty() { t("Could not check for updates.") } else { e.message });
                }
                return;
            }
            Ok(None) => {
                self.updating = false;
                if manual {
                    speech::say_quietly(&tf("TimePlayer is up to date, version {0}.", &[&timeplayer_core::paths::version()]));
                }
                return;
            }
            Ok(Some(update)) => update,
        };
        let version = version_text(&update.version);
        if !manual && self.settings.with(|c| c.update.skipped_version.as_deref() == Some(version.as_str())) {
            self.updating = false;
            return;
        }
        if !manual && !self.quiet_enough_for_update_offer() {
            log::info!("TimePlayer {version} is available; it is offered once nothing would be interrupted.");
            self.updating = false;
            self.update_schedule.hold(update);
            return;
        }
        self.offer_update(update, manual);
    }

    fn offer_update(&mut self, update: UpdateInfo, manual: bool) {
        let version = version_text(&update.version);
        let notes = plain_text(&update.notes);
        let mut text = tf("TimePlayer {0} is available. You have {1}.\n\n", &[&version, &timeplayer_core::paths::version()]);
        if !notes.is_empty() {
            text += &tf("What's new:\n{0}\n\n", &[&notes]);
        }
        text += &t("Download and install it now? TimePlayer closes and opens again by itself.");
        if !manual {
            text += &t("\n\nIf you choose No, you will not be reminded about this version. Settings has Check for updates.");
        }
        if !dialogs::ask_yes_first(&self.ui.frame, &t("TimePlayer update"), &text) {
            self.updating = false;
            if !manual {
                self.settings.update(|c| c.update.skipped_version = Some(version.clone()));
            }
            return;
        }
        speech::say_quietly(&tf("Downloading TimePlayer {0}...", &[&version]));
        let http = self.http.clone();
        let sender = self.jobs.sender();
        self.jobs.run("update download", move || {
            let halfway = std::sync::atomic::AtomicBool::new(false);
            let progress = |fraction: f64| {
                if fraction >= 0.5 && !halfway.swap(true, std::sync::atomic::Ordering::Relaxed) {
                    sender.send(|_: &mut App| speech::say_quietly(&t("Update download halfway.")));
                }
            };
            let download = updates::download(&http, &update, &progress);
            move |app: &mut App| app.update_downloaded(download, &version)
        });
    }

    fn update_downloaded(&mut self, download: timeplayer_core::Result<std::path::PathBuf>, version: &str) {
        self.updating = false;
        let file = match download {
            Ok(file) => file,
            Err(e) => {
                speech::say(&if e.message.is_empty() { t("The update could not be downloaded.") } else { e.message });
                return;
            }
        };
        if let Err(e) = updates::start_install(&file) {
            speech::say(&if e.message.is_empty() { t("The update could not be started.") } else { e.message });
            return;
        }
        speech::say(&tf("Installing TimePlayer {0}. It opens again in a moment.", &[&version]));
        // Lets the announcement be heard, then closes (which saves the resume point and history).
        let frame = self.ui.frame;
        let timer = wxdragon::prelude::Timer::new(&frame);
        let closing = frame;
        timer.on_tick(move |_| {
            closing.close(true);
        });
        timer.start(1500, true);
        std::mem::forget(timer);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn notes_lose_their_markdown() {
        let notes = "## Fixed\n\n\n* **Downloads** work [again](https://x).\n- `yt-dlp` updated";
        assert_eq!(plain_text(notes), "Fixed\n\n- Downloads work again.\n- yt-dlp updated");
    }
}
