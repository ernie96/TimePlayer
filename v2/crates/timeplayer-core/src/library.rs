//! The local library, kept as JSON files exactly as TimePlayer 1 writes them (1.x's `LibraryService`):
//! subscriptions.json (NewPipe's export format), history.json, watch_later.json, feed.json, playlists.json
//! and hidden.json in the library folder. Everything stays on this computer.

use chrono::{DateTime, Duration as ChronoDuration, Utc};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::imports;
use crate::models::{ItemKind, SearchResult, dateoffset};
use crate::paths;

pub const LIKED_PLAYLIST_ID: &str = "liked";
const MAX_HISTORY_ENTRIES: usize = 1000;
const PROGRESS_SAVE_INTERVAL_SECS: i64 = 10;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChannelSubscription {
    #[serde(rename = "channelId")]
    pub channel_id: String,
    #[serde(rename = "name")]
    pub name: String,
}

impl ChannelSubscription {
    pub fn new(id: &str, name: &str) -> Self {
        Self { channel_id: id.into(), name: name.into() }
    }

    pub fn url(&self) -> String {
        format!("https://www.youtube.com/channel/{}", self.channel_id)
    }

    pub fn to_search_result(&self) -> SearchResult {
        SearchResult {
            kind: ItemKind::Channel,
            id: self.channel_id.clone(),
            title: self.name.clone(),
            channel_title: self.name.clone(),
            channel_id: Some(self.channel_id.clone()),
            ..Default::default()
        }
    }
}

/// A watched (or partly watched) video, for history and resume.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HistoryEntry {
    pub video_id: String,
    pub title: String,
    #[serde(default)]
    pub channel_title: String,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub duration_seconds: f64,
    #[serde(default)]
    pub position_seconds: f64,
    #[serde(with = "dateoffset", default = "min_date")]
    pub last_watched_utc: DateTime<Utc>,
    #[serde(default = "one")]
    pub watch_count: i64,
    /// Played in Music; listed under Music, Recently played.
    #[serde(default)]
    pub is_music: bool,
}

fn min_date() -> DateTime<Utc> {
    DateTime::<Utc>::MIN_UTC
}

fn one() -> i64 {
    1
}

impl HistoryEntry {
    pub fn new(video_id: &str, title: &str) -> Self {
        Self {
            video_id: video_id.into(),
            title: title.into(),
            channel_title: String::new(),
            channel_id: None,
            duration_seconds: 0.0,
            position_seconds: 0.0,
            last_watched_utc: min_date(),
            watch_count: 1,
            is_music: false,
        }
    }

    /// The last 5% (or last 20 seconds) counts as finished, so credits don't leave a stale resume point.
    pub fn is_finished(&self) -> bool {
        self.duration_seconds > 0.0
            && (self.position_seconds >= self.duration_seconds * 0.95 || self.duration_seconds - self.position_seconds <= 20.0)
    }

    /// Where to resume, or None when there's nothing worth resuming.
    pub fn resume_position(&self) -> Option<f64> {
        (!self.is_finished() && self.position_seconds >= 15.0).then_some(self.position_seconds)
    }

    pub fn to_search_result(&self) -> SearchResult {
        SearchResult {
            id: self.video_id.clone(),
            title: self.title.clone(),
            channel_title: self.channel_title.clone(),
            channel_id: self.channel_id.clone(),
            duration: (self.duration_seconds > 0.0).then(|| Duration::from_secs_f64(self.duration_seconds)),
            ..Default::default()
        }
    }
}

/// 1.x also wrote two computed values into history.json; they're written back so its files stay the same.
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct HistoryEntryOut<'a> {
    #[serde(flatten)]
    entry: &'a HistoryEntry,
    is_finished: bool,
    #[serde(with = "crate::models::timespan")]
    resume_position: Option<Duration>,
}

/// A video saved to Watch Later or a playlist.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SavedVideo {
    pub video_id: String,
    pub title: String,
    #[serde(default)]
    pub channel_title: String,
    #[serde(default)]
    pub channel_id: Option<String>,
    #[serde(default)]
    pub duration_seconds: Option<f64>,
    #[serde(with = "dateoffset", default = "min_date")]
    pub added_utc: DateTime<Utc>,
}

impl SavedVideo {
    pub fn from(r: &SearchResult, now: DateTime<Utc>) -> Self {
        Self {
            video_id: r.id.clone(),
            title: r.title.clone(),
            channel_title: r.channel_title.clone(),
            channel_id: r.channel_id.clone(),
            duration_seconds: r.duration.map(|d| d.as_secs_f64()),
            added_utc: now,
        }
    }

    pub fn to_search_result(&self) -> SearchResult {
        SearchResult {
            id: self.video_id.clone(),
            title: self.title.clone(),
            channel_title: self.channel_title.clone(),
            channel_id: self.channel_id.clone(),
            duration: self.duration_seconds.filter(|d| *d > 0.0).map(Duration::from_secs_f64),
            ..Default::default()
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PlaylistKind {
    /// Made in TimePlayer (or imported).
    #[default]
    Local,
    /// "Liked videos", filled by Like.
    Liked,
    /// A YouTube playlist saved to the library; refreshed from YouTube.
    YouTube,
}

impl Serialize for PlaylistKind {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_u8(*self as u8)
    }
}

impl<'de> Deserialize<'de> for PlaylistKind {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Ok(match u8::deserialize(d)? {
            1 => Self::Liked,
            2 => Self::YouTube,
            _ => Self::Local,
        })
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Playlist {
    pub id: String,
    pub name: String,
    #[serde(default)]
    pub kind: PlaylistKind,
    /// For saved YouTube playlists: the ID after "list=".
    #[serde(rename = "youTubeId", default)]
    pub youtube_id: Option<String>,
    #[serde(default)]
    pub owner: Option<String>,
    #[serde(with = "dateoffset", default = "min_date")]
    pub created_utc: DateTime<Utc>,
    #[serde(with = "dateoffset", default = "min_date")]
    pub updated_utc: DateTime<Utc>,
    #[serde(default)]
    pub videos: Vec<SavedVideo>,
}

impl Playlist {
    pub fn to_search_result(&self) -> SearchResult {
        SearchResult {
            kind: ItemKind::Playlist,
            id: self.youtube_id.clone().unwrap_or_else(|| self.id.clone()),
            library_playlist_id: Some(self.id.clone()),
            title: self.name.clone(),
            channel_title: if self.kind == PlaylistKind::YouTube { self.owner.clone().unwrap_or_default() } else { String::new() },
            playlist_video_count: Some(self.videos.len() as i64),
            ..Default::default()
        }
    }
}

/// Things the user asked not to be recommended.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct HiddenFromRecommendations {
    pub video_ids: Vec<String>,
    pub channels: Vec<ChannelSubscription>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
struct FeedFile {
    #[serde(with = "dateoffset", default = "min_date")]
    last_viewed_utc: DateTime<Utc>,
    #[serde(with = "dateoffset", default = "min_date")]
    fetched_utc: DateTime<Utc>,
    videos: Vec<SearchResult>,
}

/// What's New as last fetched.
#[derive(Debug, Clone)]
pub struct SubscriptionFeed {
    pub videos: Vec<SearchResult>,
    pub failed_channels: Vec<ChannelSubscription>,
    pub fetched_utc: DateTime<Utc>,
}

struct Inner {
    folder: PathBuf,
    subscriptions: Vec<ChannelSubscription>,
    history: Vec<HistoryEntry>,
    watch_later: Vec<SavedVideo>,
    feed: FeedFile,
    playlists: Vec<Playlist>,
    hidden: HiddenFromRecommendations,
    last_history_save: DateTime<Utc>,
    history_dirty: bool,
}

type Listener = Box<dyn Fn() + Send + Sync>;

/// The library, shared by every thread.
#[derive(Clone)]
pub struct Library {
    inner: Arc<Mutex<Inner>>,
    listeners: Arc<Mutex<Vec<Listener>>>,
    clock: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl Library {
    pub fn open() -> Self {
        Self::open_in(paths::library_folder())
    }

    pub fn open_in(folder: PathBuf) -> Self {
        Self::with_clock(folder, Arc::new(Utc::now))
    }

    pub fn with_clock(folder: PathBuf, clock: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        let subscriptions = std::fs::read_to_string(folder.join("subscriptions.json"))
            .ok()
            .and_then(|text| match imports::parse_subscriptions(&text) {
                Ok(parsed) => Some(parsed.channels),
                Err(_) => {
                    keep_corrupt_copy(&folder.join("subscriptions.json"));
                    None
                }
            })
            .unwrap_or_default();
        let inner = Inner {
            subscriptions,
            history: load(&folder.join("history.json")).unwrap_or_default(),
            watch_later: load(&folder.join("watch_later.json")).unwrap_or_default(),
            feed: load(&folder.join("feed.json")).unwrap_or_default(),
            playlists: load(&folder.join("playlists.json")).unwrap_or_default(),
            hidden: load(&folder.join("hidden.json")).unwrap_or_default(),
            folder,
            last_history_save: min_date(),
            history_dirty: false,
        };
        Self { inner: Arc::new(Mutex::new(inner)), listeners: Arc::default(), clock }
    }

    /// Called after any change that alters a list (not after every progress update).
    pub fn on_change(&self, listener: impl Fn() + Send + Sync + 'static) {
        self.listeners.lock().unwrap().push(Box::new(listener));
    }

    fn changed(&self) {
        for listener in self.listeners.lock().unwrap().iter() {
            listener();
        }
    }

    fn now(&self) -> DateTime<Utc> {
        (self.clock)()
    }

    pub fn folder(&self) -> PathBuf {
        self.inner.lock().unwrap().folder.clone()
    }

    // ----- Subscriptions -----

    pub fn subscriptions(&self) -> Vec<ChannelSubscription> {
        self.inner.lock().unwrap().subscriptions.clone()
    }

    pub fn is_subscribed(&self, channel_id: &str) -> bool {
        self.inner.lock().unwrap().subscriptions.iter().any(|s| s.channel_id == channel_id)
    }

    /// True when now subscribed.
    pub fn toggle_subscription(&self, channel_id: &str, name: &str) -> bool {
        let subscribed = {
            let mut i = self.inner.lock().unwrap();
            let subscribed = if let Some(pos) = i.subscriptions.iter().position(|s| s.channel_id == channel_id) {
                i.subscriptions.remove(pos);
                false
            } else {
                i.subscriptions.push(ChannelSubscription::new(channel_id, name));
                sort_subscriptions(&mut i.subscriptions);
                true
            };
            save_subscriptions(&i);
            subscribed
        };
        self.changed();
        subscribed
    }

    pub fn unsubscribe(&self, channel_id: &str) {
        {
            let mut i = self.inner.lock().unwrap();
            let before = i.subscriptions.len();
            i.subscriptions.retain(|s| s.channel_id != channel_id);
            if i.subscriptions.len() == before {
                return;
            }
            save_subscriptions(&i);
        }
        self.changed();
    }

    /// The number of channels that weren't already subscribed.
    pub fn import_subscriptions(&self, channels: &[ChannelSubscription]) -> usize {
        let added = {
            let mut i = self.inner.lock().unwrap();
            let mut added = 0;
            for channel in channels {
                if i.subscriptions.iter().any(|s| s.channel_id == channel.channel_id) {
                    continue;
                }
                i.subscriptions.push(channel.clone());
                added += 1;
            }
            if added > 0 {
                sort_subscriptions(&mut i.subscriptions);
                save_subscriptions(&i);
            }
            added
        };
        if added > 0 {
            self.changed();
        }
        added
    }

    // ----- History -----

    pub fn history(&self) -> Vec<HistoryEntry> {
        self.inner.lock().unwrap().history.clone()
    }

    pub fn get_history(&self, video_id: &str) -> Option<HistoryEntry> {
        self.inner.lock().unwrap().history.iter().find(|h| h.video_id == video_id).cloned()
    }

    /// Records playback progress; written at most every ten seconds unless `flush`.
    pub fn record_progress(&self, video: &SearchResult, position: f64, duration: f64, flush: bool, is_music: bool) {
        if video.id.trim().is_empty() || video.kind != ItemKind::Video {
            return;
        }
        let now = self.now();
        let list_changed = {
            let mut i = self.inner.lock().unwrap();
            let index = i.history.iter().position(|h| h.video_id == video.id);
            let previous = index.map(|ix| i.history[ix].clone());
            // A new viewing starts when the video was last touched more than an hour ago.
            let new_session = previous.as_ref().is_none_or(|p| now - p.last_watched_utc > ChronoDuration::hours(1));
            let entry = HistoryEntry {
                video_id: video.id.clone(),
                title: if video.title.trim().is_empty() {
                    previous.as_ref().map(|p| p.title.clone()).unwrap_or_else(|| video.id.clone())
                } else {
                    video.title.clone()
                },
                channel_title: if video.channel_title.trim().is_empty() {
                    previous.as_ref().map(|p| p.channel_title.clone()).unwrap_or_default()
                } else {
                    video.channel_title.clone()
                },
                channel_id: video.channel_id.clone().or_else(|| previous.as_ref().and_then(|p| p.channel_id.clone())),
                duration_seconds: if duration > 0.0 { duration } else { previous.as_ref().map(|p| p.duration_seconds).unwrap_or(0.0) },
                position_seconds: position.max(0.0),
                last_watched_utc: now,
                watch_count: previous.as_ref().map(|p| p.watch_count).unwrap_or(0) + i64::from(new_session),
                is_music: is_music || previous.as_ref().is_some_and(|p| p.is_music),
            };
            let list_changed = index != Some(0);
            if let Some(ix) = index {
                i.history.remove(ix);
            }
            i.history.insert(0, entry);
            i.history.truncate(MAX_HISTORY_ENTRIES);
            i.history_dirty = true;
            if flush || list_changed || (now - i.last_history_save).num_seconds() >= PROGRESS_SAVE_INTERVAL_SECS {
                save_history(&mut i, now);
            }
            list_changed
        };
        if list_changed {
            self.changed();
        }
    }

    pub fn remove_from_history(&self, video_id: &str) {
        let now = self.now();
        {
            let mut i = self.inner.lock().unwrap();
            let before = i.history.len();
            i.history.retain(|h| h.video_id != video_id);
            if i.history.len() == before {
                return;
            }
            save_history(&mut i, now);
        }
        self.changed();
    }

    pub fn clear_history(&self) {
        let now = self.now();
        {
            let mut i = self.inner.lock().unwrap();
            i.history.clear();
            save_history(&mut i, now);
        }
        self.changed();
    }

    /// Adds history from an export, keeping what's here. Returns how many weren't already there.
    pub fn import_history(&self, entries: Vec<HistoryEntry>) -> usize {
        let now = self.now();
        let added = {
            let mut i = self.inner.lock().unwrap();
            let mut known: std::collections::HashSet<String> = i.history.iter().map(|h| h.video_id.clone()).collect();
            let mut added = 0;
            for entry in entries {
                if known.insert(entry.video_id.clone()) {
                    i.history.push(entry);
                    added += 1;
                }
            }
            if added == 0 {
                return 0;
            }
            i.history.sort_by(|a, b| b.last_watched_utc.cmp(&a.last_watched_utc));
            i.history.truncate(MAX_HISTORY_ENTRIES);
            save_history(&mut i, now);
            added
        };
        self.changed();
        added
    }

    // ----- Watch Later -----

    pub fn watch_later(&self) -> Vec<SavedVideo> {
        self.inner.lock().unwrap().watch_later.clone()
    }

    pub fn is_in_watch_later(&self, video_id: &str) -> bool {
        self.inner.lock().unwrap().watch_later.iter().any(|v| v.video_id == video_id)
    }

    /// True when now saved.
    pub fn toggle_watch_later(&self, video: &SearchResult) -> bool {
        let now = self.now();
        let saved = {
            let mut i = self.inner.lock().unwrap();
            let before = i.watch_later.len();
            i.watch_later.retain(|v| v.video_id != video.id);
            let saved = i.watch_later.len() == before;
            if saved {
                i.watch_later.push(SavedVideo::from(video, now));
            }
            write_json(&i.folder.join("watch_later.json"), &i.watch_later);
            saved
        };
        self.changed();
        saved
    }

    pub fn remove_from_watch_later(&self, video_id: &str) {
        {
            let mut i = self.inner.lock().unwrap();
            let before = i.watch_later.len();
            i.watch_later.retain(|v| v.video_id != video_id);
            if i.watch_later.len() == before {
                return;
            }
            write_json(&i.folder.join("watch_later.json"), &i.watch_later);
        }
        self.changed();
    }

    pub fn import_watch_later(&self, videos: &[SearchResult]) -> usize {
        let now = self.now();
        let added = {
            let mut i = self.inner.lock().unwrap();
            let mut added = 0;
            for v in videos {
                if !v.is_video() || v.id.trim().is_empty() || i.watch_later.iter().any(|x| x.video_id == v.id) {
                    continue;
                }
                i.watch_later.push(SavedVideo::from(v, now));
                added += 1;
            }
            if added > 0 {
                write_json(&i.folder.join("watch_later.json"), &i.watch_later);
            }
            added
        };
        if added > 0 {
            self.changed();
        }
        added
    }

    // ----- What's New cache -----

    pub fn feed_last_viewed(&self) -> DateTime<Utc> {
        self.inner.lock().unwrap().feed.last_viewed_utc
    }

    pub fn cached_feed(&self) -> Option<SubscriptionFeed> {
        let i = self.inner.lock().unwrap();
        (i.feed.fetched_utc > min_date()).then(|| SubscriptionFeed {
            videos: i.feed.videos.clone(),
            failed_channels: Vec::new(),
            fetched_utc: i.feed.fetched_utc,
        })
    }

    pub fn save_feed(&self, feed: &SubscriptionFeed) {
        let mut i = self.inner.lock().unwrap();
        i.feed.fetched_utc = feed.fetched_utc;
        i.feed.videos = feed.videos.clone();
        write_json(&i.folder.join("feed.json"), &i.feed);
    }

    pub fn mark_feed_viewed(&self, when: DateTime<Utc>) {
        let mut i = self.inner.lock().unwrap();
        i.feed.last_viewed_utc = when;
        write_json(&i.folder.join("feed.json"), &i.feed);
    }

    // ----- Playlists -----

    /// "Liked videos" first, then the user's in the order they were made.
    pub fn playlists(&self) -> Vec<Playlist> {
        let i = self.inner.lock().unwrap();
        let liked = i.playlists.iter().filter(|p| p.kind == PlaylistKind::Liked);
        liked.chain(i.playlists.iter().filter(|p| p.kind != PlaylistKind::Liked)).cloned().collect()
    }

    pub fn get_playlist(&self, id: &str) -> Option<Playlist> {
        let now = self.now();
        let i = self.inner.lock().unwrap();
        i.playlists.iter().find(|p| p.id == id).cloned().or_else(|| (id == LIKED_PLAYLIST_ID).then(|| new_liked(now)))
    }

    pub fn create_playlist(&self, name: &str, videos: &[SearchResult]) -> Playlist {
        let now = self.now();
        let created = {
            let mut i = self.inner.lock().unwrap();
            let created = Playlist {
                id: uuid::Uuid::new_v4().simple().to_string()[..12].to_owned(),
                name: clean_name(name),
                kind: PlaylistKind::Local,
                youtube_id: None,
                owner: None,
                created_utc: now,
                updated_utc: now,
                videos: distinct(videos, now),
            };
            i.playlists.push(created.clone());
            save_playlists(&i);
            created
        };
        self.changed();
        created
    }

    pub fn rename_playlist(&self, id: &str, name: &str) {
        let name = clean_name(name);
        self.update_playlist(id, |p| {
            if p.kind == PlaylistKind::Liked {
                return false;
            }
            p.name = name.clone();
            true
        });
    }

    pub fn delete_playlist(&self, id: &str) {
        {
            let mut i = self.inner.lock().unwrap();
            let before = i.playlists.len();
            i.playlists.retain(|p| p.id != id);
            if i.playlists.len() == before {
                return;
            }
            save_playlists(&i);
        }
        self.changed();
    }

    /// False when the video was already there.
    pub fn add_to_playlist(&self, id: &str, video: &SearchResult) -> bool {
        if !video.is_video() || video.id.trim().is_empty() {
            return false;
        }
        let now = self.now();
        let mut added = false;
        self.update_playlist(id, |p| {
            if p.videos.iter().any(|v| v.video_id == video.id) {
                return false;
            }
            p.videos.push(SavedVideo::from(video, now));
            added = true;
            true
        });
        added
    }

    pub fn remove_from_playlist(&self, id: &str, video_id: &str) {
        self.update_playlist(id, |p| {
            let before = p.videos.len();
            p.videos.retain(|v| v.video_id != video_id);
            p.videos.len() != before
        });
    }

    /// Moves a video up (negative) or down (positive); its new index, or None.
    pub fn move_in_playlist(&self, id: &str, video_id: &str, offset: i64) -> Option<usize> {
        let mut new_index = None;
        self.update_playlist(id, |p| {
            let Some(index) = p.videos.iter().position(|v| v.video_id == video_id) else { return false };
            let target = (index as i64 + offset).clamp(0, p.videos.len() as i64 - 1) as usize;
            new_index = Some(target);
            if target == index {
                return false;
            }
            let item = p.videos.remove(index);
            p.videos.insert(target, item);
            true
        });
        new_index
    }

    /// Saves (or refreshes) a YouTube playlist in the library; a name the user changed is kept.
    pub fn save_youtube_playlist(&self, youtube_id: &str, title: &str, owner: Option<&str>, videos: &[SearchResult]) -> Playlist {
        let now = self.now();
        let saved = {
            let mut i = self.inner.lock().unwrap();
            let list = distinct(videos, now);
            let saved = if let Some(existing) =
                i.playlists.iter_mut().find(|p| p.kind == PlaylistKind::YouTube && p.youtube_id.as_deref() == Some(youtube_id))
            {
                if let Some(owner) = owner {
                    existing.owner = Some(owner.into());
                }
                existing.updated_utc = now;
                existing.videos = list;
                existing.clone()
            } else {
                let p = Playlist {
                    id: format!("yt-{youtube_id}"),
                    name: clean_name(title),
                    kind: PlaylistKind::YouTube,
                    youtube_id: Some(youtube_id.into()),
                    owner: owner.map(str::to_owned),
                    created_utc: now,
                    updated_utc: now,
                    videos: list,
                };
                i.playlists.push(p.clone());
                p
            };
            save_playlists(&i);
            saved
        };
        self.changed();
        saved
    }

    pub fn find_saved_youtube_playlist(&self, youtube_id: &str) -> Option<Playlist> {
        self.inner
            .lock()
            .unwrap()
            .playlists
            .iter()
            .find(|p| p.kind == PlaylistKind::YouTube && p.youtube_id.as_deref() == Some(youtube_id))
            .cloned()
    }

    fn update_playlist(&self, id: &str, change: impl FnOnce(&mut Playlist) -> bool) {
        let now = self.now();
        {
            let mut i = self.inner.lock().unwrap();
            if id == LIKED_PLAYLIST_ID {
                ensure_liked(&mut i, now);
            }
            let Some(p) = i.playlists.iter_mut().find(|p| p.id == id) else { return };
            if !change(p) {
                return;
            }
            p.updated_utc = now;
            save_playlists(&i);
        }
        self.changed();
    }

    // ----- Likes -----

    pub fn is_liked(&self, video_id: &str) -> bool {
        let i = self.inner.lock().unwrap();
        i.playlists.iter().find(|p| p.kind == PlaylistKind::Liked).is_some_and(|p| p.videos.iter().any(|v| v.video_id == video_id))
    }

    pub fn liked_videos(&self) -> Vec<SavedVideo> {
        let i = self.inner.lock().unwrap();
        i.playlists.iter().find(|p| p.kind == PlaylistKind::Liked).map(|p| p.videos.clone()).unwrap_or_default()
    }

    /// True when now liked. Newest likes first, as on YouTube.
    pub fn toggle_like(&self, video: &SearchResult) -> bool {
        let now = self.now();
        let liked = {
            let mut i = self.inner.lock().unwrap();
            let index = ensure_liked(&mut i, now);
            let p = &mut i.playlists[index];
            let before = p.videos.len();
            p.videos.retain(|v| v.video_id != video.id);
            let liked = p.videos.len() == before;
            if liked {
                p.videos.insert(0, SavedVideo::from(video, now));
            }
            p.updated_utc = now;
            save_playlists(&i);
            liked
        };
        self.changed();
        liked
    }

    pub fn import_likes(&self, videos: &[SearchResult]) -> usize {
        let now = self.now();
        let added = {
            let mut i = self.inner.lock().unwrap();
            let index = ensure_liked(&mut i, now);
            let p = &mut i.playlists[index];
            let mut added = 0;
            for v in videos {
                if !v.is_video() || v.id.trim().is_empty() || p.videos.iter().any(|x| x.video_id == v.id) {
                    continue;
                }
                p.videos.push(SavedVideo::from(v, now));
                added += 1;
            }
            if added > 0 {
                p.updated_utc = now;
                save_playlists(&i);
            }
            added
        };
        if added > 0 {
            self.changed();
        }
        added
    }

    // ----- Not interested -----

    pub fn hidden(&self) -> HiddenFromRecommendations {
        self.inner.lock().unwrap().hidden.clone()
    }

    pub fn hide_video(&self, video_id: &str) {
        {
            let mut i = self.inner.lock().unwrap();
            if i.hidden.video_ids.iter().any(|v| v == video_id) {
                return;
            }
            i.hidden.video_ids.push(video_id.into());
            // Keep the file small; old entries rarely come back into recommendations.
            let len = i.hidden.video_ids.len();
            if len > 2000 {
                i.hidden.video_ids.drain(..len - 2000);
            }
            write_json(&i.folder.join("hidden.json"), &i.hidden);
        }
        self.changed();
    }

    pub fn block_channel(&self, channel_id: &str, name: &str) {
        {
            let mut i = self.inner.lock().unwrap();
            if i.hidden.channels.iter().any(|c| c.channel_id == channel_id) {
                return;
            }
            i.hidden.channels.push(ChannelSubscription::new(channel_id, name));
            write_json(&i.folder.join("hidden.json"), &i.hidden);
        }
        self.changed();
    }

    pub fn unblock_channel(&self, channel_id: &str) {
        {
            let mut i = self.inner.lock().unwrap();
            let before = i.hidden.channels.len();
            i.hidden.channels.retain(|c| c.channel_id != channel_id);
            if i.hidden.channels.len() == before {
                return;
            }
            write_json(&i.folder.join("hidden.json"), &i.hidden);
        }
        self.changed();
    }

    pub fn is_hidden(&self, video: &SearchResult) -> bool {
        let i = self.inner.lock().unwrap();
        i.hidden.video_ids.contains(&video.id)
            || video.channel_id.as_ref().is_some_and(|c| i.hidden.channels.iter().any(|b| &b.channel_id == c))
            || (!video.channel_title.trim().is_empty()
                && i.hidden.channels.iter().any(|b| b.name.eq_ignore_ascii_case(&video.channel_title)))
    }

    /// Writes history if progress is waiting to be saved (on exit).
    pub fn flush(&self) {
        let now = self.now();
        let mut i = self.inner.lock().unwrap();
        if i.history_dirty {
            save_history(&mut i, now);
        }
    }
}

fn new_liked(now: DateTime<Utc>) -> Playlist {
    Playlist {
        id: LIKED_PLAYLIST_ID.into(),
        name: "Liked videos".into(),
        kind: PlaylistKind::Liked,
        youtube_id: None,
        owner: None,
        created_utc: now,
        updated_utc: now,
        videos: Vec::new(),
    }
}

fn ensure_liked(i: &mut Inner, now: DateTime<Utc>) -> usize {
    if let Some(index) = i.playlists.iter().position(|p| p.kind == PlaylistKind::Liked) {
        return index;
    }
    i.playlists.insert(0, new_liked(now));
    0
}

fn distinct(videos: &[SearchResult], now: DateTime<Utc>) -> Vec<SavedVideo> {
    let mut seen = std::collections::HashSet::new();
    videos
        .iter()
        .filter(|v| v.is_video() && !v.id.trim().is_empty() && seen.insert(v.id.clone()))
        .map(|v| SavedVideo::from(v, now))
        .collect()
}

fn clean_name(name: &str) -> String {
    if name.trim().is_empty() { "Untitled playlist".into() } else { name.trim().into() }
}

fn sort_subscriptions(list: &mut [ChannelSubscription]) {
    list.sort_by_key(|s| s.name.to_lowercase());
}

fn save_subscriptions(i: &Inner) {
    write_text(&i.folder.join("subscriptions.json"), &imports::to_newpipe_json(&i.subscriptions));
}

fn save_history(i: &mut Inner, now: DateTime<Utc>) {
    let out: Vec<HistoryEntryOut> = i
        .history
        .iter()
        .map(|entry| HistoryEntryOut {
            entry,
            is_finished: entry.is_finished(),
            resume_position: entry.resume_position().map(Duration::from_secs_f64),
        })
        .collect();
    write_json(&i.folder.join("history.json"), &out);
    i.last_history_save = now;
    i.history_dirty = false;
}

fn save_playlists(i: &Inner) {
    write_json(&i.folder.join("playlists.json"), &i.playlists);
}

fn load<T: for<'de> Deserialize<'de>>(path: &Path) -> Option<T> {
    let text = std::fs::read_to_string(path).ok()?;
    match serde_json::from_str(text.trim_start_matches('\u{feff}')) {
        Ok(value) => Some(value),
        Err(e) => {
            log::warn!("{} could not be read: {e}", path.display());
            keep_corrupt_copy(path);
            None
        }
    }
}

/// Never silently overwrite a file that couldn't be read; the user may want to recover it.
fn keep_corrupt_copy(path: &Path) {
    let mut copy = path.as_os_str().to_owned();
    copy.push(".unreadable");
    let _ = std::fs::copy(path, PathBuf::from(copy));
}

fn write_json<T: Serialize + ?Sized>(path: &Path, value: &T) {
    match serde_json::to_string_pretty(value) {
        Ok(text) => write_text(path, &text),
        Err(e) => log::warn!("{} could not be written: {e}", path.display()),
    }
}

/// Best effort: the in-memory state stays right and the next change tries again.
fn write_text(path: &Path, text: &str) {
    if let Err(e) = paths::write_atomic(path, text.as_bytes()) {
        log::warn!("{} could not be saved: {e}", path.display());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp_library() -> (Library, PathBuf) {
        let folder = std::env::temp_dir().join(format!("tp2-lib-{}", uuid::Uuid::new_v4().simple()));
        (Library::open_in(folder.clone()), folder)
    }

    #[test]
    fn subscriptions_are_saved_in_newpipe_format_and_read_back() {
        let (lib, folder) = temp_library();
        assert!(lib.toggle_subscription("UCaaaaaaaaaaaaaaaaaaaaaa", "Beta"));
        assert!(lib.toggle_subscription("UCbbbbbbbbbbbbbbbbbbbbbb", "alpha"));
        let text = std::fs::read_to_string(folder.join("subscriptions.json")).unwrap();
        assert!(text.contains("\"service_id\": 0"));
        let again = Library::open_in(folder.clone());
        assert_eq!(again.subscriptions().iter().map(|s| s.name.as_str()).collect::<Vec<_>>(), ["alpha", "Beta"]);
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn progress_resume_and_sessions() {
        let (lib, folder) = temp_library();
        let video = SearchResult::video("dQw4w9WgXcQ", "Song", "Rick");
        lib.record_progress(&video, 30.0, 200.0, true, false);
        let entry = lib.get_history("dQw4w9WgXcQ").unwrap();
        assert_eq!(entry.resume_position(), Some(30.0));
        assert_eq!(entry.watch_count, 1);
        lib.record_progress(&video, 195.0, 200.0, true, false);
        assert!(lib.get_history("dQw4w9WgXcQ").unwrap().is_finished());
        let saved: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(folder.join("history.json")).unwrap()).unwrap();
        assert_eq!(saved[0]["isFinished"], true);
        assert_eq!(saved[0]["watchCount"], 1);
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn likes_playlists_and_moves() {
        let (lib, folder) = temp_library();
        let a = SearchResult::video("aaaaaaaaaaa", "A", "C");
        let b = SearchResult::video("bbbbbbbbbbb", "B", "C");
        assert!(lib.toggle_like(&a));
        assert!(lib.is_liked("aaaaaaaaaaa"));
        assert_eq!(lib.playlists()[0].id, LIKED_PLAYLIST_ID);
        let p = lib.create_playlist("  Road trip ", &[a.clone(), b.clone(), a.clone()]);
        assert_eq!(p.name, "Road trip");
        assert_eq!(p.videos.len(), 2);
        assert_eq!(lib.move_in_playlist(&p.id, "bbbbbbbbbbb", -1), Some(0));
        assert!(!lib.add_to_playlist(&p.id, &a));
        let reread = Library::open_in(folder.clone());
        assert_eq!(reread.get_playlist(&p.id).unwrap().videos[0].video_id, "bbbbbbbbbbb");
        let _ = std::fs::remove_dir_all(folder);
    }

    #[test]
    fn reads_1x_playlists_file() {
        let folder = std::env::temp_dir().join(format!("tp2-lib-{}", uuid::Uuid::new_v4().simple()));
        std::fs::create_dir_all(&folder).unwrap();
        std::fs::write(
            folder.join("playlists.json"),
            r#"[{"id":"liked","name":"Liked videos","kind":1,"youTubeId":null,"owner":null,
                "createdUtc":"2026-11-07T19:19:14.8997607+00:00","updatedUtc":"2026-11-07T19:19:14.899769+00:00",
                "videos":[{"videoId":"8fPaSr0lQPE","title":"T","channelTitle":"C","channelId":"UC0","durationSeconds":1587,
                "addedUtc":"2026-11-07T19:19:14.899769+00:00"}]}]"#,
        )
        .unwrap();
        let lib = Library::open_in(folder.clone());
        assert!(lib.is_liked("8fPaSr0lQPE"));
        assert_eq!(lib.liked_videos()[0].duration_seconds, Some(1587.0));
        let _ = std::fs::remove_dir_all(folder);
    }
}
