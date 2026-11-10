//! What can be done with a video, channel or playlist (1.x's `.Library`, `.Playlists`, `.Share`, `.Account`
//! and the download half of `.Shorts`): the list's keys and Applications menu, the player's Actions menu,
//! playlists, sharing, downloads and imports.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU32, Ordering};

use chrono::Utc;
use timeplayer_core::http::Cancel;
use timeplayer_core::i18n::{plural, t, tf};
use timeplayer_core::imports;
use timeplayer_core::library::{HistoryEntry, LIKED_PLAYLIST_ID, PlaylistKind};
use timeplayer_core::links;
use timeplayer_core::models::{ItemKind, SearchResult};
use timeplayer_core::{downloads, text};
use wxdragon::prelude::*;

use crate::dialogs::{self, MenuTarget, PopupMenu};
use crate::sections::Section;
use crate::speech;
use crate::App;

/// What Control+C shares when asked each time.
#[derive(Clone)]
pub struct ShareRequest {
    pub youtube_link: String,
    pub youtube_message: String,
    pub timeplayer_message: String,
}

/// Downloads running, and the switch that cancels them.
#[derive(Default)]
pub struct Downloads {
    pub active: usize,
    pub cancel: Cancel,
    pub last_file: Option<PathBuf>,
}

pub fn share_link(item: &SearchResult, start_at: Option<f64>) -> String {
    match item.kind {
        ItemKind::Channel => format!("https://www.youtube.com/channel/{}", item.id),
        ItemKind::Playlist => format!("https://www.youtube.com/playlist?list={}", item.id),
        ItemKind::Video => {
            let link = format!("https://youtu.be/{}", item.id);
            match start_at.filter(|s| *s >= 1.0) {
                Some(at) => format!("{link}?t={}", at as i64),
                None => link,
            }
        }
    }
}

impl App {
    // ----- The list -----

    /// Enter on a row.
    pub(crate) fn activate_selected(&mut self) {
        let Some(index) = self.ui.list.selected_index() else { return };
        if self.browse.current() == Section::Queue {
            self.play_queue_item(index);
            return;
        }
        if let Some(item) = self.browse.items.get(index).cloned() {
            self.play_item(item, false);
        }
    }

    /// The Applications key or Shift+F10 on a row.
    pub(crate) fn list_context_menu(&mut self) {
        if self.browse.current() == Section::Queue {
            let index = self.ui.list.selected_index();
            let menu = self.queue_menu(index);
            menu.show_at(self, MenuTarget::List);
            return;
        }
        let Some(item) = self.selected_item() else { return };
        let menu = self.item_menu(item, true);
        menu.show_at(self, MenuTarget::List);
    }

    pub(crate) fn up_next_context_menu(&mut self) {
        let Some(item) = self.selected_up_next() else { return };
        let menu = self.item_menu(item, false);
        menu.show_at(self, MenuTarget::UpNext);
    }

    pub(crate) fn queue_context_menu(&mut self) {
        let index = self.ui.queue.selected_index();
        let menu = self.queue_menu(index);
        menu.show_at(self, MenuTarget::PlayerQueue);
    }

    fn queue_menu(&self, index: Option<usize>) -> PopupMenu {
        let mut menu = PopupMenu::new();
        if let Some(index) = index {
            menu.item("_Play now", "Enter", move |app| app.play_queue_item(index));
            menu.item("_Remove from queue", "Delete", move |app| app.remove_queue_item(index));
        }
        menu.item("_Clear the whole queue", "", App::clear_queue);
        menu
    }

    /// 1.x's list row menu; `in_list` adds what only the main list does (remove, not interested).
    fn item_menu(&self, item: SearchResult, in_list: bool) -> PopupMenu {
        let mut menu = PopupMenu::new();
        let video = item.is_video();
        let i = item.clone();
        menu.item("_Play or open", "Enter", move |app| app.play_item(i, false));
        if video {
            let i = item.clone();
            menu.item("Start radi_o", "Ctrl+R", move |app| app.start_radio(Some(i)));
            let i = item.clone();
            menu.item("Add to _queue", "Ctrl+Q", move |app| app.enqueue_item(&i));
            let i = item.clone();
            menu.item("Save to or remove from _Watch Later", "Ctrl+L", move |app| app.toggle_watch_later_item(&i));
            let i = item.clone();
            menu.item("_Like or unlike", "Ctrl+K", move |app| app.toggle_like_item(&i));
            let i = item.clone();
            menu.item("Add to playlis_t...", "Ctrl+P", move |app| app.show_add_to_playlist_menu(Some(i)));
        }
        let i = item.clone();
        menu.item("_Subscribe or unsubscribe", "Ctrl+S", move |app| app.toggle_subscription_item(&i));
        let i = item.clone();
        menu.item("Go to _channel", "Ctrl+G", move |app| app.open_channel(&i));
        let i = item.clone();
        menu.item("Cop_y share link", "Ctrl+C", move |app| app.copy_link_item(&i));
        if video {
            let i = item.clone();
            menu.item("_Download audio", "Ctrl+D", move |app| app.download(Some(i), true));
            let i = item.clone();
            menu.item("Download _video", "Ctrl+D", move |app| app.download(Some(i), false));
        }
        if in_list {
            let section = self.browse.current();
            let removable = matches!(section, Section::History | Section::MusicRecent | Section::WatchLater | Section::Channels | Section::Playlists)
                || self.browse.is_open_playlist_in_library();
            if removable {
                let i = item.clone();
                menu.item("_Remove from this list", "Delete", move |app| app.remove_item(&i));
            }
            if section == Section::ForYou && video {
                let i = item.clone();
                menu.item("_Not interested", "Delete", move |app| app.not_interested(&i));
                let i = item.clone();
                menu.item("Don't recommend this c_hannel", "Shift+Delete", move |app| app.dont_recommend_channel(&i));
            }
        }
        menu
    }

    /// Control+Q.
    pub(crate) fn enqueue_item(&mut self, item: &SearchResult) {
        if !item.is_video() {
            return;
        }
        let mut queued = item.clone();
        queued.status_prefix = None;
        self.coordinator.enqueue(&queued);
        speech::say_quietly(&t("Added to queue."));
    }

    /// Control+L on a row.
    pub(crate) fn toggle_watch_later_item(&mut self, item: &SearchResult) {
        if !item.is_video() {
            return;
        }
        let saved = self.library.toggle_watch_later(item);
        speech::say(&if saved { tf("Saved to Watch Later: {0}.", &[&item.title]) } else { tf("Removed from Watch Later: {0}.", &[&item.title]) });
        if !saved && self.browse.current() == Section::WatchLater {
            self.remove_item_keeping_position(&item.id);
        }
    }

    /// Control+K on a row.
    pub(crate) fn toggle_like_item(&mut self, item: &SearchResult) {
        if !item.is_video() {
            return;
        }
        let liked = self.library.toggle_like(item);
        self.sync_like_to_youtube(&item.id, liked);
        speech::say(&if liked { tf("Liked {0}.", &[&item.title]) } else { tf("Removed like from {0}.", &[&item.title]) });
        if !liked && self.browse.current() == Section::Playlist && self.browse.library_playlist.as_deref() == Some(LIKED_PLAYLIST_ID) {
            self.remove_item_keeping_position(&item.id);
        }
    }

    /// Control+S on a row.
    pub(crate) fn toggle_subscription_item(&mut self, item: &SearchResult) {
        let (channel_id, channel_name) =
            if item.kind == ItemKind::Channel { (Some(item.id.clone()), item.title.clone()) } else { (item.channel_id.clone(), item.channel_title.clone()) };
        let Some(channel_id) = channel_id.filter(|c| !c.trim().is_empty()) else {
            speech::say(&t("The channel for this video is not known yet. Play the video, then press Control+S in the player."));
            return;
        };
        let subscribed = self.library.toggle_subscription(&channel_id, &channel_name);
        self.sync_subscription_to_youtube(&channel_id, subscribed);
        speech::say(&if subscribed { tf("Subscribed to {0}.", &[&channel_name]) } else { tf("Unsubscribed from {0}.", &[&channel_name]) });
        if self.browse.current() == Section::Channels
            && let Some(row) = self.browse.items.iter_mut().find(|r| r.id == item.id)
        {
            row.status_prefix = if subscribed { None } else { Some("Unsubscribed".into()) };
            self.show_rows(quiet_list::Select::Keep);
        }
    }

    /// Delete on a row.
    pub(crate) fn remove_selected(&mut self) {
        if self.browse.current() == Section::Queue {
            if let Some(index) = self.ui.list.selected_index() {
                self.remove_queue_item(index);
            }
            return;
        }
        if let Some(item) = self.selected_item() {
            self.remove_item(&item);
        }
    }

    pub(crate) fn remove_item(&mut self, item: &SearchResult) {
        match self.browse.current() {
            Section::History | Section::MusicRecent => {
                self.library.remove_from_history(&item.id);
                speech::say(&tf("Removed from history: {0}.", &[&item.title]));
            }
            Section::WatchLater => {
                self.library.remove_from_watch_later(&item.id);
                speech::say(&tf("Removed from Watch Later: {0}.", &[&item.title]));
            }
            Section::Channels => {
                self.library.unsubscribe(&item.id);
                self.sync_subscription_to_youtube(&item.id, false);
                speech::say(&tf("Unsubscribed from {0}.", &[&item.title]));
            }
            Section::ForYou => {
                self.not_interested(item);
                return;
            }
            Section::Playlists => {
                self.delete_playlist();
                return;
            }
            Section::Playlist if self.browse.library_playlist.is_some() => {
                let playlist_id = self.browse.library_playlist.clone().unwrap_or_default();
                if playlist_id == LIKED_PLAYLIST_ID {
                    let liked = self.library.toggle_like(item);
                    self.sync_like_to_youtube(&item.id, liked);
                } else {
                    self.library.remove_from_playlist(&playlist_id, &item.id);
                }
                speech::say(&tf("Removed {0}.", &[&item.title]));
            }
            _ => {
                speech::say(&t("Delete removes items from History, Watch Later, Channels, For You and your playlists."));
                return;
            }
        }
        self.remove_item_keeping_position(&item.id);
    }

    pub(crate) fn not_interested(&mut self, item: &SearchResult) {
        if !item.is_video() {
            return;
        }
        self.library.hide_video(&item.id);
        let id = item.id.clone();
        self.recommendations.forget(move |v| v.id == id);
        speech::say(&t("Not interested. It won't be recommended again."));
        if self.browse.current() == Section::ForYou {
            self.remove_item_keeping_position(&item.id);
        }
    }

    /// Shift+Delete in For You.
    pub(crate) fn dont_recommend_channel(&mut self, item: &SearchResult) {
        let channel_name = if item.kind == ItemKind::Channel { item.title.clone() } else { item.channel_title.clone() };
        if channel_name.trim().is_empty() {
            return;
        }
        let channel_id = (if item.kind == ItemKind::Channel { Some(item.id.clone()) } else { item.channel_id.clone() }).unwrap_or_else(|| channel_name.clone());
        self.library.block_channel(&channel_id, &channel_name);
        let (id, name) = (channel_id.clone(), channel_name.clone());
        self.recommendations.forget(move |v| v.channel_id.as_deref() == Some(&id) || v.channel_title.eq_ignore_ascii_case(&name));
        speech::say(&tf("{0} won't be recommended again.", &[&channel_name]));
        if self.browse.current() == Section::ForYou {
            let index = self.ui.list.selected_index().unwrap_or(0);
            self.browse.items.retain(|v| !(v.channel_id.as_deref() == Some(&channel_id) || v.channel_title == channel_name));
            if !self.browse.items.is_empty() {
                let keep = index.min(self.browse.items.len() - 1);
                self.show_rows(quiet_list::Select::Key(keep));
            } else {
                self.show_rows(quiet_list::Select::Start);
            }
        }
    }

    // ----- The video playing -----

    /// Control+K in the player.
    pub(crate) fn toggle_like_current(&mut self) {
        let Some(video) = self.current_video_for_actions() else {
            speech::say(&t("No video is loaded."));
            return;
        };
        let liked = self.library.toggle_like(&video);
        self.sync_like_to_youtube(&video.id, liked);
        speech::say(&if liked { t("Liked.") } else { t("Like removed.") });
    }

    /// Control+S in the player.
    pub(crate) fn toggle_subscribe_current(&mut self) {
        let channel_id = self.playing.details.as_ref().map(|d| d.channel.id.clone()).filter(|c| !c.is_empty()).or_else(|| self.playing.current.as_ref().and_then(|c| c.channel_id.clone()));
        let channel_name = self
            .playing
            .details
            .as_ref()
            .map(|d| d.channel.name.clone())
            .or_else(|| self.playing.current.as_ref().map(|c| c.channel_title.clone()))
            .unwrap_or_else(|| self.playing.channel.clone());
        let Some(channel_id) = channel_id.filter(|c| !c.trim().is_empty()) else {
            speech::say(&t("The channel is not known yet; try again once the video is playing."));
            return;
        };
        let subscribed = self.library.toggle_subscription(&channel_id, &channel_name);
        self.sync_subscription_to_youtube(&channel_id, subscribed);
        speech::say(&if subscribed { tf("Subscribed to {0}.", &[&channel_name]) } else { tf("Unsubscribed from {0}.", &[&channel_name]) });
    }

    /// Control+L in the player.
    pub(crate) fn toggle_watch_later_current(&mut self) {
        let Some(video) = self.playing.current.clone() else {
            speech::say(&t("No video is loaded."));
            return;
        };
        let saved = self.library.toggle_watch_later(&video);
        speech::say(&if saved { t("Saved to Watch Later.") } else { t("Removed from Watch Later.") });
    }

    /// Control+G in the player.
    pub(crate) fn open_current_channel(&mut self) {
        let video = self.playing.details.as_ref().map(|d| d.to_search_result()).or_else(|| self.playing.current.clone());
        let Some(video) = video.filter(|v| v.channel_id.as_ref().is_some_and(|c| !c.trim().is_empty())) else {
            speech::say(&t("The channel is not known yet; try again once the video is playing."));
            return;
        };
        self.leave_player_view();
        self.open_channel(&video);
    }

    /// Alt+A, the Applications key or Actions in the player.
    pub(crate) fn show_actions_menu(&mut self) {
        let mut menu = PopupMenu::new();
        let liked = self.playing.current_id().is_some_and(|id| self.library.is_liked(&id));
        let channel_id = self.playing.details.as_ref().map(|d| d.channel.id.clone()).filter(|c| !c.is_empty()).or_else(|| self.playing.current.as_ref().and_then(|c| c.channel_id.clone()));
        let subscribed = channel_id.is_some_and(|c| self.library.is_subscribed(&c));
        let in_watch_later = self.playing.current_id().is_some_and(|id| self.library.is_in_watch_later(&id));
        menu.item(if liked { "Unlike" } else { "Like" }, "Ctrl+K", App::toggle_like_current);
        menu.item(if subscribed { "Unsubscribe" } else { "Subscribe" }, "Ctrl+S", App::toggle_subscribe_current);
        menu.item(if in_watch_later { "Remove from Watch Later" } else { "Watch Later" }, "Ctrl+L", App::toggle_watch_later_current);
        menu.item("Add to playlis_t...", "Ctrl+P", |app| app.show_add_to_playlist_menu(None));
        menu.item("Go to _channel", "Ctrl+G", App::open_current_channel);
        menu.item("Copy share _link at this point", "Ctrl+C", App::copy_share_link_at_current_time);
        menu.item("_Download...", "Ctrl+D", |app| app.show_download_menu(None));
        menu.separator();
        let captions = self.caption_choices();
        let speak_captions = self.settings.with(|c| c.accessibility.speak_captions);
        menu.submenu("Ca_ptions", |sub| {
            for (track, label, chosen) in captions {
                sub.plain_check(&label, chosen, move |app| app.select_caption_track(track));
            }
            sub.separator();
            sub.check("_Read captions aloud", "C", speak_captions, App::toggle_speak_captions);
        });
        let speed = self.playing.speed;
        menu.submenu(&tf("_Speed, {0:0.##}x", &[&speed]), |sub| {
            sub.item("_Faster", "Shift+Period", |app| app.change_speed(1));
            sub.item("_Slower", "Shift+Comma", |app| app.change_speed(-1));
            sub.item("_Normal speed", "", |app| app.change_speed(0));
        });
        let active = self.active_sound_preset();
        let music = self.music_mode;
        let devices = self.audio_device_choices();
        let chosen_device = self.settings.with(|c| c.playback.audio_device.clone());
        menu.submenu("S_ound", |sub| {
            for name in timeplayer_core::audio::preset_names() {
                let name = name.to_owned();
                let checked = name == active;
                sub.plain_check(&t(&name), checked, move |app| app.select_sound_preset(&name));
            }
            sub.separator();
            sub.check("_Music mode", "Ctrl+M", music, App::toggle_music_mode);
            if devices.len() > 1 {
                sub.separator();
                for (device, label) in devices {
                    let checked = device == chosen_device;
                    let shown = tf("Output: {0}", &[&label]);
                    sub.plain_check(&shown, checked, move |app| app.choose_audio_device(&device, &label));
                }
            }
        });
        let (autoplay, description, lyrics) = self.settings.with(|c| (c.library.autoplay, c.playback.audio_description, c.accessibility.speak_lyrics));
        menu.check("_Autoplay related videos", "A", autoplay, App::toggle_autoplay);
        menu.item("S_kip SponsorBlock segment", "S", App::skip_current_segment);
        menu.item("Jump to _highlight", "H", App::jump_to_highlight);
        menu.item("_Video information", "I", |app| app.announce_video_info());
        menu.check("Audio d_escription", "D", description, App::toggle_audio_description);
        menu.check("Read l_yrics aloud", "L", lyrics, App::toggle_speak_lyrics);
        menu.item("Start _radio from this song", "R", |app| app.start_radio(None));
        menu.show_at(self, MenuTarget::Actions);
    }

    /// The sound outputs mpv knows: the system's default first.
    pub(crate) fn audio_device_choices(&self) -> Vec<(String, String)> {
        let mut choices = vec![("auto".to_owned(), t("Windows default"))];
        for device in self.coordinator.engine().audio_devices() {
            if device.name != "auto" {
                choices.push((device.name, device.description));
            }
        }
        let chosen = self.settings.with(|c| c.playback.audio_device.clone());
        if !choices.iter().any(|(name, _)| *name == chosen) {
            choices.push((chosen, t("The device chosen before (not connected now)")));
        }
        choices
    }

    // ----- Playlists -----

    /// Control+P: the playlists to add to, and New playlist.
    pub(crate) fn show_add_to_playlist_menu(&mut self, item: Option<SearchResult>) {
        if item.as_ref().is_some_and(|i| !i.is_video()) {
            return;
        }
        if item.is_none() && self.current_video_for_actions().is_none() {
            speech::say(&t("No video is loaded."));
            return;
        }
        let mut menu = PopupMenu::new();
        for playlist in self.library.playlists().into_iter().filter(|p| p.kind == PlaylistKind::Local) {
            let i = item.clone();
            menu.plain_item(&playlist.name, move |app| app.add_to_playlist(i, Some(playlist.id)));
        }
        let i = item.clone();
        menu.item("_New playlist...", "", move |app| app.add_to_playlist(i, None));
        if item.is_none() && self.player_visible {
            menu.show_at(self, MenuTarget::Actions);
        } else if self.ui.up_next.has_focus() {
            menu.show_at(self, MenuTarget::UpNext);
        } else {
            menu.show_at(self, MenuTarget::List);
        }
    }

    fn add_to_playlist(&mut self, item: Option<SearchResult>, playlist_id: Option<String>) {
        let Some(item) = item.or_else(|| self.current_video_for_actions()).filter(|i| i.is_video()) else { return };
        let Some(playlist_id) = playlist_id else {
            let Some(name) = dialogs::ask_text(&self.ui.frame, &t("New playlist"), &t("Playlist name:"), "") else { return };
            let created = self.library.create_playlist(&name, std::slice::from_ref(&item));
            speech::say(&tf("Created {0} with {1}.", &[&created.name, &item.title]));
            return;
        };
        let name = self.library.get_playlist(&playlist_id).map(|p| p.name).unwrap_or_else(|| "playlist".into());
        let added = self.library.add_to_playlist(&playlist_id, &item);
        speech::say(&if added { tf("Added to {0}.", &[&name]) } else { tf("Already in {0}.", &[&name]) });
    }

    pub(crate) fn create_playlist(&mut self) {
        let Some(name) = dialogs::ask_text(&self.ui.frame, &t("New playlist"), &t("Playlist name:"), "") else { return };
        let playlist = self.library.create_playlist(&name, &[]);
        speech::say(&tf("Created playlist {0}.", &[&playlist.name]));
        if self.browse.current() == Section::Playlists {
            self.show_playlists(false);
            if let Some(index) = self.browse.items.iter().position(|r| r.library_playlist_id.as_deref() == Some(&playlist.id)) {
                self.ui.list.select(Some(&index));
            }
            self.ui.focus_list();
        }
    }

    pub(crate) fn rename_playlist(&mut self) {
        let id = match self.browse.current() {
            Section::Playlists => self.selected_item().and_then(|r| r.library_playlist_id),
            _ => self.browse.library_playlist.clone(),
        };
        let playlist = id.as_deref().filter(|id| *id != LIKED_PLAYLIST_ID).and_then(|id| self.library.get_playlist(id));
        let Some(playlist) = playlist else {
            speech::say(&t("Liked videos can't be renamed."));
            return;
        };
        let Some(name) = dialogs::ask_text(&self.ui.frame, &t("Rename playlist"), &t("New name:"), &playlist.name) else { return };
        if name == playlist.name {
            return;
        }
        self.library.rename_playlist(&playlist.id, &name);
        speech::say(&tf("Renamed to {0}.", &[&name]));
        if self.browse.current() == Section::Playlist {
            self.browse.playlist_title = name.clone();
            self.browse.title = name;
            self.show_rows(quiet_list::Select::Keep);
        } else {
            let index = self.ui.list.selected_index().unwrap_or(0);
            self.show_playlists(false);
            if index < self.browse.items.len() {
                self.ui.list.select(Some(&index));
            }
            self.ui.focus_list();
        }
    }

    pub(crate) fn delete_playlist(&mut self) {
        let id = match self.browse.current() {
            Section::Playlists => self.selected_item().and_then(|r| r.library_playlist_id),
            _ => self.browse.library_playlist.clone(),
        };
        let Some(playlist) = id.and_then(|id| self.library.get_playlist(&id)) else { return };
        if playlist.kind == PlaylistKind::Liked {
            speech::say(&t("Liked videos can't be deleted. Remove videos with Delete instead."));
            return;
        }
        let what = if playlist.kind == PlaylistKind::YouTube {
            tf("Remove the saved YouTube playlist \"{0}\" from your library? It stays on YouTube.", &[&playlist.name])
        } else {
            tf("Delete the playlist \"{0}\" with {1}? This cannot be undone.", &[&playlist.name, &plural(playlist.videos.len() as i64, "{0} video", "{0} videos")])
        };
        if !dialogs::confirm(&self.ui.frame, &t("Delete playlist"), &what) {
            return;
        }
        self.library.delete_playlist(&playlist.id);
        speech::say(&tf("Deleted {0}.", &[&playlist.name]));
        if self.browse.current() == Section::Playlist {
            self.go_back();
        } else if let Some(row) = self.selected_item() {
            self.remove_item_keeping_position(&row.id);
        }
    }

    pub(crate) fn save_open_playlist(&mut self) {
        let Some(playlist_id) = self.browse.youtube_playlist.clone().filter(|_| self.browse.library_playlist.is_none()) else { return };
        speech::say_quietly(&t("Saving playlist..."));
        let playlists = self.playlists.clone();
        let fallback_title = self.browse.playlist_title.clone();
        self.jobs.run("save playlist", move || {
            let all = playlists.all(&playlist_id, 5000, &Cancel::new());
            move |app: &mut App| match all {
                Err(e) => speech::say(&tf("Could not save the playlist. {0}", &[&e.message])),
                Ok(page) => {
                    let title = if page.title.trim().is_empty() { fallback_title } else { page.title.clone() };
                    let saved = app.library.save_youtube_playlist(&playlist_id, &title, page.owner.as_deref(), &page.videos);
                    if app.browse.current() == Section::Playlist && app.browse.youtube_playlist.as_deref() == Some(&playlist_id) {
                        app.browse.library_playlist = Some(saved.id.clone());
                        app.ui.show_section_controls(&app.browse, app.music_mode);
                    }
                    speech::say(&tf(
                        "Saved {0} to Playlists, {1}. Press Control+5 to find it.",
                        &[&saved.name, &plural(saved.videos.len() as i64, "{0} video", "{0} videos")],
                    ));
                }
            }
        });
    }

    /// Play all and Shuffle.
    pub(crate) fn play_playlist(&mut self, shuffle: bool) {
        let section = self.browse.current();
        if section == Section::Playlists
            && let Some(row) = self.selected_item().filter(|r| r.kind == ItemKind::Playlist)
        {
            if let Some(id) = &row.library_playlist_id {
                let videos = self.library.get_playlist(id).map(|p| p.videos.iter().map(|v| v.to_search_result()).collect()).unwrap_or_default();
                self.play_videos(videos, shuffle);
            } else {
                self.play_whole_youtube_playlist(row.id.clone(), Vec::new(), shuffle);
            }
            return;
        }
        if section == Section::Playlist
            && self.browse.library_playlist.is_none()
            && let Some(id) = self.browse.youtube_playlist.clone()
            && self.browse.can_load_more()
        {
            let shown = self.browse.items.clone();
            self.play_whole_youtube_playlist(id, shown, shuffle);
            return;
        }
        let videos = self.browse.items.iter().filter(|r| r.is_video()).cloned().collect();
        self.play_videos(videos, shuffle);
    }

    fn play_whole_youtube_playlist(&mut self, id: String, fallback: Vec<SearchResult>, shuffle: bool) {
        speech::say_quietly(&t("Loading the whole playlist..."));
        let playlists = self.playlists.clone();
        self.jobs.run("whole playlist", move || {
            let all = playlists.all(&id, 5000, &Cancel::new());
            move |app: &mut App| match all {
                Ok(page) => app.play_videos(page.videos, shuffle),
                Err(e) => {
                    if fallback.is_empty() {
                        speech::say(&tf("Could not load the playlist. {0}", &[&e.message]));
                    } else {
                        app.play_videos(fallback, shuffle);
                    }
                }
            }
        });
    }

    fn play_videos(&mut self, videos: Vec<SearchResult>, shuffle: bool) {
        let mut videos: Vec<SearchResult> = videos
            .into_iter()
            .filter(|v| v.is_video())
            .map(|mut v| {
                v.status_prefix = None;
                v
            })
            .collect();
        if videos.is_empty() {
            speech::say(&t("Nothing to play."));
            return;
        }
        if shuffle {
            timeplayer_core::discovery::shuffle(&mut videos);
        }
        let count = plural(videos.len() as i64, "{0} video", "{0} videos");
        speech::say_quietly(&if shuffle { tf("Shuffling {0}.", &[&count]) } else { tf("Playing {0}.", &[&count]) });
        self.coordinator.replace_queue(&videos);
        let first = videos[0].clone();
        self.play_item(first, false);
    }

    /// Alt+Up and Alt+Down in your own playlist.
    pub(crate) fn move_selected_in_playlist(&mut self, offset: i64) {
        if !self.browse.is_open_playlist_in_library() {
            return;
        }
        let Some(index) = self.ui.list.selected_index() else { return };
        let Some(item) = self.browse.items.get(index).cloned().filter(|i| i.is_video()) else { return };
        let Some(id) = self.browse.library_playlist.clone() else { return };
        match self.library.move_in_playlist(&id, &item.id, offset) {
            Some(target) if target != index => {
                let moved = self.browse.items.remove(index);
                self.browse.items.insert(target, moved);
                self.show_rows(quiet_list::Select::Key(target));
                speech::say_quietly(&tf("Moved to {0} of {1}.", &[&(target + 1), &self.browse.items.len()]));
            }
            _ => speech::say_quietly(&if offset < 0 { t("Already first.") } else { t("Already last.") }),
        }
    }

    // ----- Sharing -----

    /// Control+C on a row.
    pub(crate) fn copy_link_item(&mut self, item: &SearchResult) {
        match item.kind {
            ItemKind::Playlist => self.copy_playlist_link(item.library_playlist_id.clone(), Some(item.id.clone())),
            ItemKind::Channel => {
                if self.copy_to_clipboard(&share_link(item, None)) {
                    speech::say_quietly(&t("Channel link copied."));
                }
            }
            ItemKind::Video => self.share_video_link(ShareRequest {
                youtube_link: share_link(item, None),
                youtube_message: t("Share link copied."),
                timeplayer_message: t("TimePlayer link copied."),
            }),
        }
    }

    pub(crate) fn copy_open_playlist_link(&mut self) {
        self.copy_playlist_link(self.browse.library_playlist.clone(), self.browse.youtube_playlist.clone());
    }

    fn copy_playlist_link(&mut self, library_id: Option<String>, youtube_id: Option<String>) {
        let playlist = library_id.and_then(|id| self.library.get_playlist(&id));
        let youtube = match &playlist {
            Some(p) => p.youtube_id.clone(),
            None => youtube_id,
        };
        if let Some(id) = youtube {
            self.share_video_link(ShareRequest {
                youtube_link: format!("https://www.youtube.com/playlist?list={id}"),
                youtube_message: t("Playlist link copied."),
                timeplayer_message: t("TimePlayer playlist link copied."),
            });
            return;
        }
        let Some(playlist) = playlist.filter(|p| !p.videos.is_empty()) else {
            speech::say_quietly(&t("This playlist is empty; there is nothing to share."));
            return;
        };
        let ids: Vec<&str> = playlist.videos.iter().take(50).map(|v| v.video_id.as_str()).collect();
        if self.copy_to_clipboard(&format!("https://www.youtube.com/watch_videos?video_ids={}", ids.join(","))) {
            speech::say_quietly(&if playlist.videos.len() > 50 { t("Link copied with the first 50 videos; YouTube allows no more in a shared list.") } else { t("Playlist link copied.") });
        }
    }

    /// Control+C in the player.
    pub(crate) fn copy_share_link_at_current_time(&mut self) {
        let Some(video) = self.current_video_for_actions() else { return };
        let at = self.playing.position;
        if at >= 1.0 {
            let spoken = text::time_spoken_secs(at);
            self.share_video_link(ShareRequest {
                youtube_link: share_link(&video, Some(at)),
                youtube_message: tf("Share link copied, starts at {0}.", &[&spoken]),
                timeplayer_message: tf("TimePlayer link copied, starts at {0}.", &[&spoken]),
            });
        } else {
            self.share_video_link(ShareRequest {
                youtube_link: share_link(&video, None),
                youtube_message: t("Share link copied."),
                timeplayer_message: t("TimePlayer link copied."),
            });
        }
    }

    /// Control+Shift+C in the player.
    pub(crate) fn copy_share_link_current(&mut self) {
        let Some(video) = self.current_video_for_actions() else { return };
        self.share_video_link(ShareRequest {
            youtube_link: share_link(&video, None),
            youtube_message: t("Share link copied, from the beginning."),
            timeplayer_message: t("TimePlayer link copied, from the beginning."),
        });
    }

    fn share_video_link(&mut self, request: ShareRequest) {
        match self.settings.with(|c| c.share_links.clone()).as_str() {
            "youtube" => self.copy_chosen_link(&request, false),
            "timeplayer" => self.copy_chosen_link(&request, true),
            _ => {
                let mut menu = PopupMenu::new();
                let r = request.clone();
                menu.item("_YouTube link, opens anywhere", "", move |app| app.copy_chosen_link(&r, false));
                let r = request.clone();
                menu.item("_TimePlayer link, opens in TimePlayer for people who have it", "", move |app| app.copy_chosen_link(&r, true));
                self.show_menu_where_focus_is(menu);
            }
        }
    }

    fn copy_chosen_link(&mut self, request: &ShareRequest, timeplayer: bool) {
        let link = if timeplayer { links::to_protocol_link(&request.youtube_link) } else { request.youtube_link.clone() };
        if self.copy_to_clipboard(&link) {
            speech::say_quietly(if timeplayer { &request.timeplayer_message } else { &request.youtube_message });
        }
    }

    /// Shows a menu under the list or panel that has focus, or under Actions in the player.
    fn show_menu_where_focus_is(&mut self, menu: PopupMenu) {
        if self.ui.list.has_focus() {
            menu.show_at(self, MenuTarget::List);
        } else if self.ui.up_next.has_focus() {
            menu.show_at(self, MenuTarget::UpNext);
        } else if self.player_visible {
            menu.show_at(self, MenuTarget::Actions);
        } else {
            menu.show_at(self, MenuTarget::Frame);
        }
    }

    pub(crate) fn copy_to_clipboard(&self, text: &str) -> bool {
        if Clipboard::get().set_text(text) {
            true
        } else {
            log::warn!("Couldn't copy to the clipboard");
            speech::say_quietly(&t("Could not copy; the clipboard is busy. Try again."));
            false
        }
    }

    // ----- Downloads -----

    /// Control+D: the download choices for a row, or for the video playing.
    pub(crate) fn show_download_menu(&mut self, item: Option<SearchResult>) {
        let mut menu = PopupMenu::new();
        let for_player = item.is_none();
        if !for_player && self.browse.items.iter().filter(|r| r.is_video()).count() > 1 {
            menu.item("Download all in this list as a_udio...", "", |app| app.download_all(true));
            menu.item("Download all in this list as vid_eo...", "", |app| app.download_all(false));
        }
        let audio_label = if self.downloads.can_convert() { "Download audio (_MP3)" } else { "Download audio (M_4A)" };
        let i = item.clone();
        menu.item(audio_label, "", move |app| app.download(i, true));
        let i = item.clone();
        menu.item("Download _video (MP4)", "", move |app| app.download(i, false));
        menu.item("_Open downloads folder", "", App::open_downloads_folder);
        if self.download_state.active > 0 {
            menu.item("_Cancel downloads", "", App::cancel_downloads);
        }
        if for_player {
            menu.show_at(self, MenuTarget::Actions);
        } else {
            self.show_menu_where_focus_is(menu);
        }
    }

    /// One video; None downloads the one playing.
    pub(crate) fn download(&mut self, item: Option<SearchResult>, audio_only: bool) {
        let item = item.or_else(|| self.current_video_for_actions());
        let Some(item) = item.filter(|i| i.kind == ItemKind::Video) else {
            speech::say(&t("Choose a video to download."));
            return;
        };
        if item.is_live {
            speech::say(&t("Live streams cannot be downloaded."));
            return;
        }
        let title = if item.title.trim().is_empty() { "video".to_owned() } else { item.title.clone() };
        let format = self.download_format(audio_only);
        speech::say_quietly(&tf("Downloading {0}: {1}", &[&format, &title]));
        self.download_state.active += 1;
        let downloads = self.downloads.clone();
        let cancel = self.download_state.cancel.clone();
        let sender = self.jobs.sender();
        self.jobs.run("download", move || {
            // Halfway is said once, from the download's own thread.
            let milestone = Arc::new(AtomicU32::new(0));
            let halfway_title = title.clone();
            let progress = move |p: timeplayer_core::ytdlp::DownloadProgress| {
                if p.percent >= 50.0 && p.percent < 100.0 && milestone.swap(50, Ordering::Relaxed) < 50 {
                    let text = tf("Download halfway: {0}", &[&halfway_title]);
                    sender.send(move |_| speech::say_quietly(&text));
                }
            };
            let result = downloads.download(&item.id, audio_only, Some(&progress), &cancel);
            move |app: &mut App| {
                app.download_state.active -= 1;
                match result {
                    Ok(file) => {
                        app.download_state.last_file = Some(file);
                        speech::say_quietly(&tf("Downloaded: {0}. Saved in {1}.", &[&title, &app.short_download_folder()]));
                    }
                    Err(e) if e.code == "DOWNLOAD_CANCELED" => speech::say_quietly(&tf("Download canceled: {0}", &[&title])),
                    Err(e) => speech::say(&tf("Download failed: {0}. {1}", &[&title, &e.message])),
                }
            }
        });
    }

    fn download_format(&self, audio_only: bool) -> String {
        if !audio_only {
            t("video")
        } else if self.downloads.can_convert() {
            t("audio, MP3")
        } else {
            t("audio, M4A")
        }
    }

    fn short_download_folder(&self) -> String {
        let folder = self.downloads.folder();
        let name = folder.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        match folder.parent().and_then(Path::file_name) {
            Some(parent) => format!("{}, {name}", parent.to_string_lossy()),
            None => name,
        }
    }

    /// Download all in this list.
    pub(crate) fn download_all(&mut self, audio_only: bool) {
        if self.browse.current() == Section::Playlist
            && self.browse.library_playlist.is_none()
            && let Some(id) = self.browse.youtube_playlist.clone()
        {
            speech::say_quietly(&t("Loading the whole playlist..."));
            let playlists = self.playlists.clone();
            let shown = self.browse.items.clone();
            self.jobs.run("whole playlist", move || {
                let all = playlists.all(&id, 5000, &Cancel::new());
                move |app: &mut App| app.download_videos(all.map(|p| p.videos).unwrap_or(shown), audio_only)
            });
            return;
        }
        // Loads the rest of the list first, up to 500.
        if self.browse.can_load_more() && self.browse.items.len() < 500 {
            self.after_more_loaded = Some(Box::new(move |app: &mut App, _| app.download_all(audio_only)));
            self.load_more(false);
            return;
        }
        let videos = self.browse.items.clone();
        self.download_videos(videos, audio_only);
    }

    fn download_videos(&mut self, videos: Vec<SearchResult>, audio_only: bool) {
        let mut seen = std::collections::HashSet::new();
        let videos: Vec<SearchResult> = videos.into_iter().filter(|v| v.is_video() && !v.is_live && seen.insert(v.id.clone())).collect();
        if videos.is_empty() {
            speech::say(&t("There are no videos to download here."));
            return;
        }
        let list_name = match self.browse.current() {
            Section::Playlist => self.browse.playlist_title.clone(),
            Section::Channel => format!("{} - {}", self.browse.channel_name, self.browse.channel_tab.label()),
            _ => self.browse.title.clone(),
        };
        let fallback = t("Downloads");
        let folder_name = downloads::safe_folder_name(if list_name.trim().is_empty() { &fallback } else { &list_name });
        let count = plural(videos.len() as i64, "{0} video", "{0} videos");
        let format = self.download_format(audio_only);
        if !dialogs::confirm(
            &self.ui.frame,
            &t("Download all"),
            &tf("Download {0} as {1} into the folder {2}? They download one after another; Cancel downloads stops them.", &[&count, &format, &folder_name]),
        ) {
            return;
        }
        speech::say_quietly(&tf("Downloading {0} into {1}.", &[&count, &folder_name]));
        self.download_state.active += 1;
        let downloads = self.downloads.clone();
        let cancel = self.download_state.cancel.clone();
        let sender = self.jobs.sender();
        self.jobs.run("download all", move || {
            let (mut done, mut failed) = (0usize, 0usize);
            for (i, video) in videos.iter().enumerate() {
                if cancel.is_canceled() {
                    break;
                }
                match downloads.download_into_folder(&video.id, audio_only, &folder_name, i + 1, None, &cancel) {
                    Ok(_) => done += 1,
                    Err(e) if e.code == "DOWNLOAD_CANCELED" => break,
                    Err(_) => failed += 1,
                }
                if videos.len() >= 6 && i + 1 == videos.len() / 2 {
                    let text = tf("Download halfway: {0}", &[&folder_name]);
                    sender.send(move |_| speech::say_quietly(&text));
                }
            }
            let canceled = cancel.is_canceled();
            move |app: &mut App| {
                app.download_state.active -= 1;
                let videos = plural(done as i64, "{0} video", "{0} videos");
                speech::say_quietly(&if canceled {
                    tf("Downloads canceled after {0}.", &[&videos])
                } else if failed == 0 {
                    tf("Downloaded {0} into {1}.", &[&videos, &folder_name])
                } else {
                    tf("Downloaded {0} into {1}; {2} could not be downloaded.", &[&videos, &folder_name, &failed])
                });
            }
        });
    }

    pub(crate) fn cancel_downloads(&mut self) {
        if self.download_state.active == 0 {
            speech::say_quietly(&t("No downloads are running."));
            return;
        }
        self.download_state.cancel.cancel();
        self.download_state.cancel = Cancel::new();
    }

    pub(crate) fn open_downloads_folder(&mut self) {
        let folder = self.downloads.folder();
        let _ = std::fs::create_dir_all(&folder);
        match self.download_state.last_file.as_ref().filter(|f| f.exists()) {
            Some(file) => crate::platform::show_in_folder(file),
            None => downloads::open_folder(&folder),
        }
    }

    // ----- Subscriptions, history and imports -----

    pub(crate) fn clear_history(&mut self) {
        if !dialogs::confirm(&self.ui.frame, &t("Clear watch history"), &t("Clear your whole watch history? Resume positions are lost too. This cannot be undone.")) {
            return;
        }
        self.library.clear_history();
        speech::say(&t("Watch history cleared."));
        if self.browse.current() == Section::History {
            self.show_section(Section::History, false, false);
        }
    }

    pub(crate) fn import_subscriptions_from_file(&mut self) {
        let filter = t("Subscription exports (Google Takeout zip or CSV, NewPipe, LibreTube, FreeTube, OPML)|*.zip;*.json;*.csv;*.opml;*.xml;*.db|All files|*.*");
        let Some(path) = dialogs::open_file(&self.ui.frame, &t("Import subscriptions"), &filter) else { return };
        if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("zip")) {
            self.import_takeout(path);
            return;
        }
        let parsed = std::fs::read(&path).map_err(|e| e.to_string()).and_then(|bytes| {
            if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("db")) {
                imports::parse_subscriptions(&String::from_utf8_lossy(&bytes)).map_err(|e| e.message)
            } else {
                imports::parse_subscriptions(&String::from_utf8_lossy(&bytes)).map_err(|e| e.message)
            }
        });
        match parsed {
            Err(e) => speech::say(&tf("Could not import subscriptions: {0}", &[&e])),
            Ok(parsed) => {
                let added = self.library.import_subscriptions(&parsed.channels);
                let already = parsed.channels.len() - added;
                let mut message = tf("Imported {0} from {1}.", &[&plural(added as i64, "{0} channel", "{0} channels"), &parsed.format_name]);
                if already > 0 {
                    message += &tf(" {0} already subscribed.", &[&already]);
                }
                if parsed.skipped > 0 {
                    message += &tf(" {0} entries were not YouTube channels and were skipped.", &[&parsed.skipped]);
                }
                if added > 0 {
                    message += &t(" Press Control+3 to see their latest videos.");
                    self.browse.forget_feed();
                }
                speech::say(&message);
                if self.browse.current() == Section::Channels {
                    self.show_section(Section::Channels, false, false);
                }
            }
        }
    }

    pub(crate) fn export_subscriptions_to_file(&mut self) {
        let filter = t("NewPipe JSON, for NewPipe, LibreTube and Tubular (*.json)|*.json|OPML, for RSS readers and FreeTube (*.opml)|*.opml");
        let Some(path) = dialogs::save_file(&self.ui.frame, &t("Export subscriptions"), &filter, "subscriptions.json") else { return };
        let subs = self.library.subscriptions();
        let content = if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("opml")) { imports::to_opml(&subs) } else { imports::to_newpipe_json(&subs) };
        match std::fs::write(&path, content) {
            Ok(()) => speech::say(&tf(
                "Exported {0} to {1}.",
                &[&plural(subs.len() as i64, "{0} channel", "{0} channels"), &path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()],
            )),
            Err(e) => speech::say(&tf("Could not export subscriptions: {0}", &[&e])),
        }
    }

    /// How to get my YouTube data / subscriptions: the steps, and Google Takeout on Yes.
    pub(crate) fn show_youtube_data_steps(&mut self) {
        if dialogs::ask(&self.ui.frame, &t("How to get my YouTube data"), &crate::help::full::youtube_subscriptions_steps()) {
            crate::platform::open_url("https://takeout.google.com/settings/takeout/custom/youtube");
        }
    }

    pub(crate) fn import_takeout_from_file(&mut self) {
        let filter = t("Google Takeout zip (*.zip)|*.zip|All files|*.*");
        if let Some(path) = dialogs::open_file(&self.ui.frame, &t("Import from Google Takeout"), &filter) {
            self.import_takeout(path);
        }
    }

    fn import_takeout(&mut self, path: PathBuf) {
        if self.importing {
            return;
        }
        self.importing = true;
        let playlists = self.playlists.clone();
        let library = self.library.clone();
        let sender = self.jobs.sender();
        self.jobs.run("takeout", move || {
            let contents = match imports::parse_takeout_zip(&path) {
                Ok(c) => c,
                Err(e) => return Box::new(move |app: &mut App| {
                    app.importing = false;
                    speech::say(&tf("Could not import: {0}", &[&e.message]));
                }) as Box<dyn FnOnce(&mut App) + Send>,
            };
            if contents.is_empty() {
                return Box::new(|app: &mut App| {
                    app.importing = false;
                    speech::say(&t("Nothing to import was found in this zip. In Google Takeout, choose YouTube and YouTube Music, with subscriptions, playlists and history."));
                });
            }
            let mut report = Vec::new();
            let mut new_channels = false;
            if let Some(subs) = contents.subscriptions.as_ref().filter(|s| !s.channels.is_empty()) {
                let added = library.import_subscriptions(&subs.channels);
                new_channels = added > 0;
                report.push(plural(added as i64, "{0} channel", "{0} channels"));
            }
            if !contents.history.is_empty() {
                let added = library.import_history(contents.history.clone());
                report.push(plural(added as i64, "{0} watched video", "{0} watched videos"));
            }
            let mut known: std::collections::HashMap<String, SearchResult> = library.history().iter().map(|h| (h.video_id.clone(), h.to_search_result())).collect();
            let mut ids: Vec<String> = contents.playlists.iter().flat_map(|p| p.video_ids.iter().cloned()).chain(contents.liked_video_ids.iter().cloned()).chain(contents.watch_later_ids.iter().cloned()).collect();
            let mut seen = std::collections::HashSet::new();
            ids.retain(|id| !known.contains_key(id) && seen.insert(id.clone()));
            if !ids.is_empty() {
                let text = tf("Looking up {0}. This can take a minute.", &[&plural(ids.len() as i64, "{0} video title", "{0} video titles")]);
                sender.send(move |_| speech::say_quietly(&text));
                for video in playlists.describe(&ids, &|_| {}) {
                    known.insert(video.id.clone(), video);
                }
            }
            let video = |id: &String| known.get(id).cloned().unwrap_or_else(|| imports::placeholder_video(id));
            let liked: Vec<SearchResult> = contents.liked_video_ids.iter().map(video).collect();
            let watch_later: Vec<SearchResult> = contents.watch_later_ids.iter().map(video).collect();
            let lists: Vec<(String, Vec<SearchResult>)> = contents.playlists.iter().map(|p| (p.name.clone(), p.video_ids.iter().map(video).collect())).collect();
            add_imported_lists(&library, &liked, &watch_later, &lists, &mut report);
            Box::new(move |app: &mut App| {
                app.importing = false;
                if new_channels {
                    app.browse.forget_feed();
                }
                speech::say(&tf("Imported from Google Takeout: {0}.", &[&report.join(", ")]));
                if matches!(app.browse.current(), Section::Playlists | Section::Channels | Section::History) {
                    app.show_section(app.browse.current(), false, false);
                }
            })
        });
    }

    /// After the sign-in page: reads the account's lists and imports them (1.x's ImportFromYouTubeAccountAsync).
    pub(crate) fn import_from_account(&mut self, was_signed_in: bool) {
        if self.importing {
            return;
        }
        self.importing = true;
        let progress = crate::import_progress::ImportProgress::open(&self.ui.frame);
        let cancel = progress.cancel();
        let inner_tube = self.sources.inner_tube.clone();
        let library = self.library.clone();
        let sender = self.jobs.sender();
        self.import_window = Some(progress);
        self.jobs.run("account import", move || {
            let report_progress = {
                let sender = sender.clone();
                move |p: timeplayer_core::innertube::ImportProgress| sender.send(move |app: &mut App| app.import_progress(p))
            };
            let data = inner_tube.read_account(&report_progress, &cancel);
            let canceled = cancel.is_canceled();
            let summary = match data {
                _ if canceled => t("Import canceled. Nothing was imported."),
                Err(e) => tf("Could not read your YouTube lists: {0}", &[&e.message]),
                Ok(data) => {
                    let mut report = Vec::new();
                    if !data.channels.is_empty() {
                        report.push(plural(library.import_subscriptions(&data.channels) as i64, "{0} channel", "{0} channels"));
                    }
                    if !data.history.is_empty() {
                        let now = Utc::now();
                        let entries = data
                            .history
                            .iter()
                            .enumerate()
                            .map(|(i, v)| {
                                let mut entry = HistoryEntry::new(&v.id, &v.title);
                                entry.channel_title = v.channel_title.clone();
                                entry.channel_id = v.channel_id.clone();
                                entry.duration_seconds = v.duration.map(|d| d.as_secs_f64()).unwrap_or(0.0);
                                entry.last_watched_utc = now - chrono::TimeDelta::minutes(i as i64 + 1);
                                entry
                            })
                            .collect();
                        report.push(plural(library.import_history(entries) as i64, "{0} watched video", "{0} watched videos"));
                    }
                    add_imported_lists(&library, &data.liked, &data.watch_later, &data.playlists, &mut report);
                    if report.is_empty() {
                        t("Your YouTube account has no subscriptions, history, likes or playlists to import.")
                    } else {
                        tf("Imported from YouTube: {0}.", &[&report.join(", ")])
                    }
                }
            };
            move |app: &mut App| app.account_import_finished(summary, was_signed_in)
        });
    }

    fn import_progress(&mut self, progress: timeplayer_core::innertube::ImportProgress) {
        if let Some(window) = &self.import_window {
            window.update(&progress);
        }
    }

    fn account_import_finished(&mut self, summary: String, was_signed_in: bool) {
        self.importing = false;
        if let Some(window) = self.import_window.take() {
            window.finish();
        }
        self.browse.forget_feed();
        if matches!(self.browse.current(), Section::Playlists | Section::Channels | Section::History) {
            self.show_section(self.browse.current(), false, false);
        }
        if was_signed_in {
            speech::say(&summary);
            return;
        }
        let keep = dialogs::confirm(
            &self.ui.frame,
            &t("YouTube sign-in"),
            &(summary + "\n\n" + &t("Keep TimePlayer signed in to YouTube? It's then used for videos YouTube only plays when signed in, and Settings, YouTube account, can do more with it. Choose No to erase the sign-in now; what was imported stays.")),
        );
        if keep {
            self.after_sign_in(Ok(()));
            return;
        }
        self.account.sign_out();
        self.refresh_section_tabs();
        speech::say_quietly(&t("The sign-in is erased. What was imported stays."));
    }

    /// After any sign-in: the smallest use of the account when it wasn't used, and the YouTube tab.
    pub(crate) fn after_sign_in(&mut self, result: Result<(), String>) {
        if let Err(e) = result {
            speech::say(&if e.is_empty() { t("Signing in did not work.") } else { e });
            return;
        }
        if self.settings.with(|c| c.account.use_ == timeplayer_core::config::AccountUse::Off) {
            self.settings.update(|c| c.account.use_ = timeplayer_core::config::AccountUse::RestrictedOnly);
        }
        self.refresh_section_tabs();
        speech::say(&tf("Signed in to YouTube. {0}", &[&self.account_status()]));
    }

    pub(crate) fn account_status(&self) -> String {
        use timeplayer_core::config::AccountUse;
        if !self.account.is_signed_in() {
            return t("Not signed in.");
        }
        match self.settings.with(|c| c.account.use_) {
            AccountUse::Off => t("Signed in, but not used. Choose how to use it above."),
            AccountUse::RestrictedOnly => t("Signed in. Used only for videos that need it."),
            AccountUse::Full => t("Signed in. Control+9 shows your YouTube pages; likes and subscriptions are sent to YouTube too."),
        }
    }

    // ----- Likes and subscriptions sent to YouTube -----

    fn sync_like_to_youtube(&self, video_id: &str, liked: bool) {
        if !self.account_features_on() {
            return;
        }
        let inner_tube = self.sources.inner_tube.clone();
        let id = video_id.to_owned();
        self.jobs.run("like", move || {
            let result = inner_tube.set_like(&id, liked);
            move |_: &mut App| report_youtube_result(result)
        });
    }

    fn sync_subscription_to_youtube(&self, channel_id: &str, subscribed: bool) {
        if !self.account_features_on() || channel_id.is_empty() {
            return;
        }
        let inner_tube = self.sources.inner_tube.clone();
        let id = channel_id.to_owned();
        self.jobs.run("subscribe", move || {
            let result = inner_tube.set_subscribed(&id, subscribed);
            move |_: &mut App| report_youtube_result(result)
        });
    }
}

fn report_youtube_result(result: timeplayer_core::Result<()>) {
    if let Err(e) = result {
        log::info!("Sending to YouTube failed: {}", e.message);
        speech::say_quietly(&tf("Saved here, but YouTube didn't take it: {0}", &[&e.message]));
    }
}

/// Likes, Watch Later and playlists from an import; playlists already imported (same name) are left out.
fn add_imported_lists(
    library: &timeplayer_core::library::Library,
    liked: &[SearchResult],
    watch_later: &[SearchResult],
    playlists: &[(String, Vec<SearchResult>)],
    report: &mut Vec<String>,
) {
    if !liked.is_empty() {
        report.push(plural(library.import_likes(liked) as i64, "{0} liked video", "{0} liked videos"));
    }
    if !watch_later.is_empty() {
        report.push(plural(library.import_watch_later(watch_later) as i64, "{0} Watch Later video", "{0} Watch Later videos"));
    }
    let mut existing: std::collections::HashSet<String> = library.playlists().iter().map(|p| p.name.to_lowercase()).collect();
    let mut added = 0;
    for (name, videos) in playlists {
        if !existing.insert(name.to_lowercase()) {
            continue;
        }
        library.create_playlist(name, videos);
        added += 1;
    }
    if !playlists.is_empty() {
        report.push(plural(added, "{0} playlist", "{0} playlists"));
    }
}
