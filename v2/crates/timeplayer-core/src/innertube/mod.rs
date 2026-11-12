//! YouTube's own web API, InnerTube (1.x's `InnerTubeBackendProvider`): search with YouTube's filters,
//! Up next and radio mixes, comments, channel pages and tabs, playlists, the signed-in account's pages,
//! likes and subscriptions sent to YouTube, reading the account's lists, and the audio-description check.
//! No API key; the sign-in is only added when the user has chosen to use it.

pub mod json;

use serde_json::{Value, json};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::account::Account;
use crate::config::Settings;
use crate::error::{AppError, Result};
use crate::http::{BROWSER_USER_AGENT, Cancel, Http, Request};
use crate::library::ChannelSubscription;
use crate::models::{CaptionTrack, CommentPage, Page, PlaylistPage, ResolvedStream, SearchResult, SourceKind, VideoDetails, WatchNext, is_descriptive_track_name};
use crate::{plural_t, t};

use json::*;

/// YouTube's own search filters (the "params" its web page sends).
pub const SHORTS_FILTER: &str = "EgIQCQ==";
pub const LIVE_FILTER: &str = "EgJAAQ==";
/// Videos, by upload date.
pub const NEWEST_FILTER: &str = "CAISAhAB";
/// Videos, by view count.
pub const MOST_VIEWED_FILTER: &str = "CAMSAhAB";
/// "Type: Playlist".
pub const PLAYLIST_FILTER: &str = "EgIQAw==";

pub const ACCOUNT_IMPORT_STEPS: usize = 5;
/// Enough for large accounts without running forever: a page holds about 30 to 100 items.
const MAX_IMPORT_PAGES: usize = 40;

/// A YouTube account's own lists, read while signed in (instead of a Takeout zip).
#[derive(Debug, Clone, Default)]
pub struct AccountData {
    pub channels: Vec<ChannelSubscription>,
    pub history: Vec<SearchResult>,
    pub liked: Vec<SearchResult>,
    pub watch_later: Vec<SearchResult>,
    pub playlists: Vec<(String, Vec<SearchResult>)>,
}

/// How far reading an account has got. `new_step` is true when a step starts (worth saying aloud).
#[derive(Debug, Clone, PartialEq)]
pub struct ImportProgress {
    pub step: usize,
    pub steps: usize,
    pub text: String,
    pub new_step: bool,
}

#[derive(Clone)]
pub struct InnerTube {
    http: Http,
    settings: Settings,
    account: Option<Account>,
    channel_tabs: Arc<Mutex<HashMap<String, HashMap<String, String>>>>,
}

fn web_context(region: Option<&str>) -> Value {
    let gl = region.map(str::trim).filter(|r| !r.is_empty()).map(str::to_uppercase).unwrap_or_else(|| "US".into());
    json!({ "client": { "clientName": "WEB", "clientVersion": "2.20261015.00.00", "hl": "en", "gl": gl } })
}

/// 16 characters from YouTube's alphabet, as its player makes for each playback.
fn client_playback_nonce() -> String {
    const ALPHABET: &[u8] = b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
    (0..16).map(|_| ALPHABET[(crate::discovery::random_unit() * 64.0) as usize % 64] as char).collect()
}

/// The playback statistics address with the version and nonce YouTube's player adds.
fn playback_stats_url(base: &str, nonce: &str) -> String {
    let separator = if base.contains('?') { '&' } else { '?' };
    format!("{base}{separator}ver=2&cpn={nonce}")
}

fn now_unix() -> i64 {
    chrono::Utc::now().timestamp()
}

fn signed_out(root: &Value) -> bool {
    find_all(root, "loggedOut").into_iter().any(|v| v.as_bool() == Some(true))
}

impl InnerTube {
    pub fn new(http: Http, settings: Settings, account: Option<Account>) -> Self {
        Self { http, settings, account, channel_tabs: Arc::default() }
    }

    fn base_url(&self) -> String {
        let url = self.settings.with(|c| c.backend.inner_tube_base_url.clone());
        let url = if url.trim().is_empty() { "https://www.youtube.com/youtubei/v1/".to_owned() } else { url };
        format!("{}/", url.trim_end_matches('/'))
    }

    fn timeout(&self) -> Duration {
        Duration::from_secs(self.settings.with(|c| c.backend.inner_tube_timeout_seconds).max(5))
    }

    /// Signed in, and the account may be used for its pages and actions.
    pub fn can_use_account(&self) -> bool {
        self.account.as_ref().is_some_and(Account::for_account_features)
    }

    fn post(&self, endpoint: &str, mut body: Value, region: Option<&str>, signed_in: bool, for_import: bool) -> Result<Value> {
        if signed_in {
            // Importing works whatever the account setting is: signing in to import is the choice.
            let allowed = self.can_use_account() || (for_import && self.account.as_ref().is_some_and(Account::is_signed_in));
            if !allowed {
                return Err(AppError::new("NOT_SIGNED_IN", t!("Sign in to YouTube in Settings, YouTube account, first.")));
            }
        }
        body["context"] = web_context(region);
        let mut request = Request::post_json(format!("{}{endpoint}?prettyPrint=false", self.base_url()), &body)
            .timeout(self.timeout())
            .header("User-Agent", BROWSER_USER_AGENT)
            .header("Accept", "*/*");
        if signed_in {
            // Only ever to youtube.com, and never logged.
            if let Some(account) = &self.account {
                for (name, value) in account.request_headers(now_unix()) {
                    request = request.header(&name, &value);
                }
            }
        }
        let response = self.http.send(request)?;
        if !response.ok() {
            log::warn!("InnerTube {endpoint} answered HTTP {}.", response.status);
            return Err(AppError::new("INNERTUBE_HTTP_ERROR", format!("InnerTube {endpoint} returned status {}.", response.status)));
        }
        response.json()
    }

    // ----- Search -----

    pub fn search(&self, query: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        self.search_with(query, None, page_token, None)
    }

    /// A search with one of YouTube's filters (Shorts, live, newest first, most viewed).
    pub fn search_filtered(&self, query: &str, filter: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        if page_token.is_some() || filter == SHORTS_FILTER || filter == LIVE_FILTER || filter == NEWEST_FILTER || filter == MOST_VIEWED_FILTER {
            let body = match page_token {
                Some(token) if !token.is_empty() => json!({ "continuation": token }),
                _ => json!({ "query": query, "params": filter }),
            };
            let root = self.post("search", body, None, false, false)?;
            let (items, next) = parse_items_page(&root);
            return Ok(Page { items, next_token: next });
        }
        self.search_with(query, Some(filter), page_token, None)
    }

    pub fn search_shorts(&self, query: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        self.search_filtered(query, SHORTS_FILTER, page_token)
    }

    /// Plain video search, with an optional filter ("sp" of a filtered search link) and region.
    pub fn search_with(&self, query: &str, params: Option<&str>, page_token: Option<&str>, region: Option<&str>) -> Result<Page<SearchResult>> {
        if query.trim().is_empty() && page_token.is_none() {
            return Err(AppError::new("EMPTY_QUERY", "Search query cannot be empty."));
        }
        let body = match page_token.filter(|t| !t.is_empty()) {
            Some(token) => json!({ "continuation": token }),
            None => match params.filter(|p| !p.is_empty()) {
                Some(p) => json!({ "query": query, "params": p }),
                None => json!({ "query": query }),
            },
        };
        let root = self.post("search", body, region, false, false)?;
        let mut seen = std::collections::HashSet::new();
        let items: Vec<SearchResult> =
            find_all(&root, "videoRenderer").into_iter().filter_map(parse_video_renderer).filter(|i| seen.insert(i.id.clone())).collect();
        let next = if items.is_empty() { None } else { list_continuation_token(&root) };
        log::info!("InnerTube search returned {} items.", items.len());
        Ok(Page { items, next_token: next })
    }

    pub fn search_playlists(&self, query: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        let body = match page_token.filter(|t| !t.is_empty()) {
            Some(token) => json!({ "continuation": token }),
            None => json!({ "query": query, "params": PLAYLIST_FILTER }),
        };
        let root = self.post("search", body, None, false, false)?;
        let mut seen = std::collections::HashSet::new();
        let items: Vec<SearchResult> =
            find_all(&root, "lockupViewModel").into_iter().filter_map(parse_playlist_lockup).filter(|i| seen.insert(i.id.clone())).collect();
        let next = if items.is_empty() { None } else { list_continuation_token(&root) };
        Ok(Page { items, next_token: next })
    }

    // ----- Watch page -----

    /// YouTube's endless mix of songs like this one (the "RDAMVM" playlist).
    pub fn radio(&self, video_id: &str) -> Result<Vec<SearchResult>> {
        let root = self.post("next", json!({ "videoId": video_id, "playlistId": format!("RDAMVM{video_id}") }), None, false, false)?;
        let mut seen = std::collections::HashSet::new();
        let songs: Vec<SearchResult> = find_all(&root, "playlistPanelVideoRenderer")
            .into_iter()
            .filter_map(parse_video_renderer)
            .filter(|s| seen.insert(s.id.clone()))
            .collect();
        if songs.is_empty() { Err(AppError::new("NO_RADIO", t!("YouTube has no radio for this song."))) } else { Ok(songs) }
    }

    pub fn watch_next(&self, video_id: &str) -> Result<WatchNext> {
        Ok(parse_watch_next(&self.post("next", json!({ "videoId": video_id }), None, false, false)?))
    }

    pub fn comments(&self, token: &str, is_reply_page: bool) -> Result<CommentPage> {
        Ok(parse_comments(&self.post("next", json!({ "continuation": token }), None, false, false)?, is_reply_page))
    }

    /// Title, channel and description from the watch page.
    pub fn details(&self, video_id: &str) -> Result<VideoDetails> {
        let root = self.post("next", json!({ "videoId": video_id }), None, false, false)?;
        let mut details = VideoDetails {
            id: video_id.into(),
            title: "Untitled Video".into(),
            thumbnail_url: Some(format!("https://i.ytimg.com/vi/{video_id}/hqdefault.jpg")),
            ..Default::default()
        };
        details.channel.name = "Unknown Channel".into();
        if let Some(primary) = find_all(&root, "videoPrimaryInfoRenderer").first()
            && let Some(title) = text(path(primary, &["title"])) {
                details.title = title;
            }
        if let Some(secondary) = find_all(&root, "videoSecondaryInfoRenderer").first() {
            if let Some(owner) = path(secondary, &["owner", "videoOwnerRenderer"]) {
                if let Some(name) = text(path(owner, &["title"])) {
                    details.channel.name = name;
                }
                if let Some(id) = str_at(owner, &["navigationEndpoint", "browseEndpoint", "browseId"]) {
                    details.channel.id = id.into();
                }
            }
            details.description = text(path(secondary, &["description"]));
        }
        if details.description.is_none() {
            details.description = parse_watch_next(&root).description;
        }
        Ok(details)
    }

    // ----- Browse -----

    /// The videos and linked playlists on a page such as a channel's home, as seen from `region`.
    pub fn browse(&self, browse_id: &str, region: Option<&str>) -> Result<(Vec<SearchResult>, Vec<SearchResult>)> {
        let root = self.post("browse", json!({ "browseId": browse_id }), region, false, false)?;
        let mut videos = Vec::new();
        let mut playlists = Vec::new();
        let mut seen = std::collections::HashSet::new();
        for lockup in find_all(&root, "lockupViewModel") {
            if let Some(v) = parse_lockup(lockup) {
                if seen.insert(v.id.clone()) {
                    videos.push(v);
                }
            } else if let Some(p) = parse_playlist_lockup(lockup).filter(|p| seen.insert(p.id.clone())) {
                playlists.push(p);
            }
        }
        for name in ["videoRenderer", "gridVideoRenderer"] {
            videos.extend(find_all(&root, name).into_iter().filter_map(parse_video_renderer).filter(|v| seen.insert(v.id.clone())));
        }
        Ok((videos, playlists))
    }

    /// One page of a YouTube playlist (the first when `continuation` is None).
    pub fn playlist(&self, playlist_id: &str, continuation: Option<&str>) -> Result<PlaylistPage> {
        let body = match continuation.filter(|c| !c.is_empty()) {
            Some(c) => json!({ "continuation": c }),
            None => json!({ "browseId": format!("VL{playlist_id}") }),
        };
        let page = parse_playlist_page(&self.post("browse", body, None, false, false)?, playlist_id);
        if continuation.is_none() && page.videos.is_empty() {
            return Err(AppError::new("PLAYLIST_EMPTY", "This playlist is private, empty or no longer exists."));
        }
        Ok(page)
    }

    /// The tabs YouTube shows for a channel, by their English names ("Videos", "Shorts", "Live", "Playlists").
    pub fn channel_tabs(&self, channel_id: &str) -> Result<HashMap<String, String>> {
        if let Some(cached) = self.channel_tabs.lock().unwrap().get(channel_id) {
            return Ok(cached.clone());
        }
        let root = self.post("browse", json!({ "browseId": channel_id }), None, false, false)?;
        let mut tabs = HashMap::new();
        for tab in find_all(&root, "tabRenderer") {
            if let (Some(title), Some(params)) = (str_at(tab, &["title"]), str_at(tab, &["endpoint", "browseEndpoint", "params"])) {
                tabs.insert(title.to_owned(), crate::http::unescape(params));
            }
        }
        self.channel_tabs.lock().unwrap().insert(channel_id.into(), tabs.clone());
        Ok(tabs)
    }

    pub fn channel_tab_page(&self, channel_id: &str, tab_params: &str, continuation: Option<&str>) -> Result<Page<SearchResult>> {
        let body = match continuation.filter(|c| !c.is_empty()) {
            Some(c) => json!({ "continuation": c }),
            None => json!({ "browseId": channel_id, "params": tab_params }),
        };
        let (items, next_token) = parse_items_page(&self.post("browse", body, None, false, false)?);
        Ok(Page { items, next_token })
    }

    // ----- The signed-in account -----

    /// "FEwhat_to_watch" (Home), "FEsubscriptions" or "FEhistory".
    pub fn account_page(&self, browse_id: &str) -> Result<Vec<SearchResult>> {
        let root = self.post("browse", json!({ "browseId": browse_id }), None, true, false)?;
        if signed_out(&root) {
            return Err(signed_out_error());
        }
        let mut seen = std::collections::HashSet::new();
        Ok(parse_account_videos(&root).into_iter().filter(|v| seen.insert(v.id.clone())).collect())
    }

    /// "LL" (liked videos) or "WL" (Watch later).
    pub fn account_playlist(&self, playlist_id: &str) -> Result<PlaylistPage> {
        let root = self.post("browse", json!({ "browseId": format!("VL{playlist_id}") }), None, true, false)?;
        if signed_out(&root) {
            return Err(signed_out_error());
        }
        Ok(parse_playlist_page(&root, playlist_id))
    }

    pub fn set_like(&self, video_id: &str, like: bool) -> Result<()> {
        let endpoint = if like { "like/like" } else { "like/removelike" };
        self.post(endpoint, json!({ "target": { "videoId": video_id } }), None, true, false).map(|_| ())
    }

    /// Adds a video to, or takes it out of, the account's Watch Later.
    pub fn set_in_watch_later(&self, video_id: &str, add: bool) -> Result<()> {
        let action = if add {
            json!({ "action": "ACTION_ADD_VIDEO", "addedVideoId": video_id })
        } else {
            json!({ "action": "ACTION_REMOVE_VIDEO_BY_VIDEO_ID", "removedVideoId": video_id })
        };
        self.post("browse/edit_playlist", json!({ "playlistId": "WL", "actions": [action] }), None, true, false).map(|_| ())
    }

    /// Puts a video in the account's watch history, the way YouTube's own player reports a play (and yt-dlp's
    /// --mark-watched): the signed-in player answer has a playback statistics address, which is then called with
    /// a new client playback nonce.
    pub fn mark_watched(&self, video_id: &str) -> Result<()> {
        let player = self.post("player", json!({ "videoId": video_id }), None, true, false)?;
        let Some(base) = player.pointer("/playbackTracking/videostatsPlaybackUrl/baseUrl").and_then(Value::as_str) else {
            return Err(AppError::new("NO_PLAYBACK_TRACKING", "YouTube gave no playback statistics address for this video."));
        };
        let url = playback_stats_url(base, &client_playback_nonce());
        let mut request = Request::get(url).timeout(self.timeout()).header("User-Agent", BROWSER_USER_AGENT);
        if let Some(account) = &self.account {
            for (name, value) in account.request_headers(now_unix()) {
                request = request.header(&name, &value);
            }
        }
        let response = self.http.send(request)?;
        if response.status >= 400 {
            return Err(AppError::new("INNERTUBE_HTTP_ERROR", format!("Marking as watched returned status {}.", response.status)));
        }
        Ok(())
    }

    pub fn set_subscribed(&self, channel_id: &str, subscribe: bool) -> Result<()> {
        let endpoint = if subscribe { "subscription/subscribe" } else { "subscription/unsubscribe" };
        self.post(endpoint, json!({ "channelIds": [channel_id] }), None, true, false).map(|_| ())
    }

    /// Subscriptions, watch history, liked videos, Watch later and the account's own playlists.
    pub fn read_account(&self, progress: &dyn Fn(ImportProgress), cancel: &Cancel) -> Result<AccountData> {
        if !self.account.as_ref().is_some_and(Account::is_signed_in) {
            return Err(AppError::new("NOT_SIGNED_IN", t!("Sign in to YouTube first.")));
        }
        let step = |n: usize, text: String| progress(ImportProgress { step: n, steps: ACCOUNT_IMPORT_STEPS, text, new_step: true });
        let count = |n: usize, text: String| progress(ImportProgress { step: n, steps: ACCOUNT_IMPORT_STEPS, text, new_step: false });

        step(1, t!("Reading your subscriptions..."));
        let mut data = AccountData::default();
        let mut seen = std::collections::HashSet::new();
        self.browse_all("FEchannels", cancel, &mut |root| {
            let before = data.channels.len();
            for name in ["channelRenderer", "gridChannelRenderer"] {
                for r in find_all(root, name) {
                    if let Some(id) = str_at(r, &["channelId"]).filter(|id| !id.is_empty() && seen.insert(id.to_string())) {
                        let name = text(path(r, &["title"])).unwrap_or_else(|| id.to_owned());
                        data.channels.push(ChannelSubscription::new(id, &name));
                    }
                }
            }
            count(1, t!("Subscriptions: {0} so far.", plural_t!(data.channels.len(), "{0} channel", "{0} channels")));
            data.channels.len() - before
        })?;

        step(2, t!("Reading your watch history..."));
        data.history = self.browse_videos("FEhistory", cancel, &|n| count(2, t!("Watch history: {0} so far.", plural_t!(n, "{0} video", "{0} videos"))))?;
        step(3, t!("Reading your liked videos..."));
        data.liked = self.browse_videos("VLLL", cancel, &|n| count(3, t!("Liked videos: {0} so far.", plural_t!(n, "{0} video", "{0} videos"))))?;
        step(4, t!("Reading your Watch Later..."));
        data.watch_later = self.browse_videos("VLWL", cancel, &|n| count(4, t!("Watch Later: {0} so far.", plural_t!(n, "{0} video", "{0} videos"))))?;

        step(5, t!("Reading your playlists..."));
        let mut lists: Vec<(String, String)> = Vec::new();
        let _ = self.browse_all("FEplaylist_aggregation", cancel, &mut |root| {
            let before = lists.len();
            for lockup in find_all(root, "lockupViewModel") {
                if let Some(p) = parse_playlist_lockup(lockup).filter(|p| p.id != "LL" && p.id != "WL")
                    && !lists.iter().any(|(id, _)| *id == p.id) {
                        lists.push((p.id, p.title));
                    }
            }
            for r in find_all(root, "gridPlaylistRenderer") {
                if let Some(id) = str_at(r, &["playlistId"]).filter(|id| !id.is_empty() && *id != "LL" && *id != "WL")
                    && !lists.iter().any(|(known, _)| known == id) {
                        lists.push((id.to_owned(), text(path(r, &["title"])).unwrap_or_else(|| id.to_owned())));
                    }
            }
            lists.len() - before
        });
        let total = lists.len();
        for (index, (id, title)) in lists.into_iter().enumerate() {
            count(5, t!("Playlist {0} of {1}: {2}", index + 1, total, title));
            let videos = self.browse_videos(&format!("VL{id}"), cancel, &|_| {})?;
            data.playlists.push((title, videos));
        }
        Ok(data)
    }

    fn browse_videos(&self, browse_id: &str, cancel: &Cancel, on_count: &dyn Fn(usize)) -> Result<Vec<SearchResult>> {
        let mut videos = Vec::new();
        let mut seen = std::collections::HashSet::new();
        self.browse_all(browse_id, cancel, &mut |root| {
            let before = videos.len();
            for v in parse_account_videos(root) {
                if seen.insert(v.id.clone()) {
                    videos.push(v);
                }
            }
            on_count(videos.len());
            videos.len() - before
        })?;
        Ok(videos)
    }

    /// A signed-in page and the pages after it; `collect` returns how many new items a page had, and a
    /// page with none ends the list. A failure after the first page keeps what was read.
    fn browse_all(&self, browse_id: &str, cancel: &Cancel, collect: &mut dyn FnMut(&Value) -> usize) -> Result<()> {
        let mut body = json!({ "browseId": browse_id });
        for page in 0..MAX_IMPORT_PAGES {
            cancel.check()?;
            let root = match self.post("browse", body, None, true, true) {
                Ok(root) => root,
                Err(e) if page == 0 => return Err(e),
                Err(_) => return Ok(()),
            };
            cancel.check()?;
            if signed_out(&root) {
                return Err(AppError::new("SIGNED_OUT", t!("YouTube says you are signed out. Sign in again.")));
            }
            if collect(&root) == 0 {
                return Ok(());
            }
            let Some(next) = list_continuation_token(&root) else { return Ok(()) };
            body = json!({ "continuation": next });
        }
        Ok(())
    }

    // ----- Streams -----

    /// Whether the video has an audio-described track: one quick request with YouTube's iPhone client (the
    /// web client's player answers "Video unavailable" without a proof-of-origin token). None when unknown.
    pub fn has_audio_description(&self, video_id: &str) -> Option<bool> {
        let body = json!({
            "videoId": video_id,
            "context": { "client": {
                "clientName": "IOS", "clientVersion": "20.10.4", "hl": "en", "gl": "US",
                "deviceMake": "Apple", "deviceModel": "iPhone16,2", "osName": "iPhone", "osVersion": "18.3.2.22D82"
            } }
        });
        let request = Request::post_json(format!("{}player?prettyPrint=false", self.base_url()), &body)
            .timeout(Duration::from_secs(3))
            .header("User-Agent", "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)");
        let response = self.http.send(request).ok().filter(|r| r.ok())?;
        let root = response.json().ok()?;
        let formats = path(&root, &["streamingData", "adaptiveFormats"])?.as_array()?;
        if formats.is_empty() {
            return None;
        }
        Some(formats.iter().any(|f| is_descriptive_track_name(str_at(f, &["audioTrack", "displayName"]))))
    }

    /// A progressive (sound and picture in one) stream from the web player, when YouTube gives one.
    pub fn resolve(&self, video_id: &str) -> Result<ResolvedStream> {
        let root = self.post("player", json!({ "videoId": video_id }), None, false, false)?;
        let status = str_at(&root, &["playabilityStatus", "status"]).unwrap_or("");
        if !status.eq_ignore_ascii_case("OK") {
            let reason = str_at(&root, &["playabilityStatus", "reason"]).unwrap_or(if status.is_empty() { "Playability check not OK" } else { status });
            log::warn!("InnerTube player playability status '{status}' for {video_id}: {reason}");
            return Err(AppError::stream_unavailable(video_id, Some(reason)));
        }
        let formats = path(&root, &["streamingData", "formats"]).and_then(Value::as_array).cloned().unwrap_or_default();
        let progressive = formats.iter().find(|f| str_at(f, &["url"]).is_some_and(|u| !u.is_empty()) && str_at(f, &["mimeType"]).is_some_and(|m| m.contains("mp4")));
        let Some(progressive) = progressive else {
            return Err(AppError::stream_unavailable(video_id, Some("No direct progressive media URLs returned by InnerTube.")));
        };
        Ok(ResolvedStream {
            video_id: video_id.into(),
            url: str_at(progressive, &["url"]).unwrap_or_default().into(),
            captions: captions(&root),
            source: SourceKind::InnerTube,
            ..Default::default()
        })
    }
}

fn signed_out_error() -> AppError {
    AppError::new("SIGNED_OUT", t!("YouTube says you are signed out. Sign in again in Settings, YouTube account."))
}

fn captions(root: &Value) -> Vec<CaptionTrack> {
    path(root, &["captions", "playerCaptionsTracklistRenderer", "captionTracks"])
        .and_then(Value::as_array)
        .map(|tracks| {
            tracks
                .iter()
                .filter_map(|t| {
                    let url = str_at(t, &["baseUrl"]).filter(|u| !u.trim().is_empty())?;
                    let code = str_at(t, &["languageCode"]).filter(|c| !c.trim().is_empty())?;
                    Some(CaptionTrack {
                        url: url.into(),
                        language_code: code.into(),
                        display_name: text(path(t, &["name"])).unwrap_or_else(|| code.into()),
                        format: "vtt".into(),
                        auto_generated: str_at(t, &["kind"]) == Some("asr"),
                        is_default: false,
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn playback_stats_address_gets_version_and_nonce() {
        let nonce = client_playback_nonce();
        assert_eq!(nonce.len(), 16);
        assert_eq!(playback_stats_url("https://s.youtube.com/api/stats/playback?cl=1&docid=abc", "N"), "https://s.youtube.com/api/stats/playback?cl=1&docid=abc&ver=2&cpn=N");
        assert_eq!(playback_stats_url("https://s.youtube.com/api/stats/playback", "N"), "https://s.youtube.com/api/stats/playback?ver=2&cpn=N");
    }
    use crate::config::AppConfig;
    use crate::http::Response;

    fn innertube(answer: impl Fn(&str) -> String + Send + Sync + 'static, bodies: Arc<Mutex<Vec<String>>>, account: Option<Account>) -> InnerTube {
        let http = Http::with_transport(move |r: Request| {
            let body = r.body_text();
            bodies.lock().unwrap().push(body.clone());
            Ok(Response { status: 200, body: answer(&body).into_bytes(), ..Default::default() })
        });
        InnerTube::new(http, Settings::in_memory(AppConfig::default()), account)
    }

    fn video(id: &str, title: &str) -> String {
        format!(r#"{{"videoRenderer":{{"videoId":"{id}","title":{{"runs":[{{"text":"{title}"}}]}},"ownerText":{{"runs":[{{"text":"Someone"}}]}}}}}}"#)
    }

    fn signed_in_account() -> Account {
        let store = std::env::temp_dir().join(format!("tp2-it-{}.dat", uuid::Uuid::new_v4().simple()));
        let account = Account::open_at(Settings::in_memory(AppConfig::default()), store);
        account
            .accept(crate::account::parse(".youtube.com\tTRUE\t/\tTRUE\t0\tSAPISID\ta\n.youtube.com\tTRUE\t/\tTRUE\t0\tSID\tb\n"))
            .unwrap();
        account
    }

    #[test]
    fn filtered_search_sends_youtubes_filter() {
        let bodies = Arc::new(Mutex::new(Vec::new()));
        let it = innertube(|_| format!(r#"{{"items":[{}]}}"#, video("eeeeeeeeeee", "Found")), bodies.clone(), None);
        for filter in [LIVE_FILTER, NEWEST_FILTER, MOST_VIEWED_FILTER] {
            let page = it.search_filtered("news", filter, None).unwrap();
            assert_eq!(page.items[0].id, "eeeeeeeeeee");
            assert!(bodies.lock().unwrap().last().unwrap().contains(&format!("\"params\":\"{filter}\"")));
        }
    }

    #[test]
    fn channel_tabs_are_read_once_and_unescaped() {
        let bodies = Arc::new(Mutex::new(Vec::new()));
        let it = innertube(
            |_| r#"{"tabs":[{"tabRenderer":{"title":"Shorts","endpoint":{"browseEndpoint":{"params":"EgZzaG9ydHPyBgUKA5oBAA%3D%3D"}}}}]}"#.into(),
            bodies.clone(),
            None,
        );
        assert_eq!(it.channel_tabs("UC1").unwrap()["Shorts"], "EgZzaG9ydHPyBgUKA5oBAA==");
        it.channel_tabs("UC1").unwrap();
        assert_eq!(bodies.lock().unwrap().len(), 1);
    }

    #[test]
    fn reads_an_account_following_pages() {
        let bodies = Arc::new(Mutex::new(Vec::new()));
        let answer = |body: &str| -> String {
            if body.contains("\"continuation\":\"CHANNELS2\"") {
                r#"{"items":[{"channelRenderer":{"channelId":"UCbbbbbbbbbbbbbbbbbbbbbb","title":{"simpleText":"Second"}}}]}"#.into()
            } else if body.contains("FEchannels") {
                r#"{"items":[{"channelRenderer":{"channelId":"UCaaaaaaaaaaaaaaaaaaaaaa","title":{"simpleText":"First"}}}],"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"CHANNELS2"}}}}"#.into()
            } else if body.contains("FEhistory") {
                format!(r#"{{"items":[{},{}]}}"#, video("aaaaaaaaaaa", "Last"), video("bbbbbbbbbbb", "Before"))
            } else if body.contains("\"VLLL\"") {
                format!(r#"{{"items":[{}]}}"#, video("ccccccccccc", "Liked"))
            } else if body.contains("FEplaylist_aggregation") {
                r#"{"items":[{"gridPlaylistRenderer":{"playlistId":"PLmine","title":{"simpleText":"Road trip"}}},{"gridPlaylistRenderer":{"playlistId":"LL","title":{"simpleText":"Liked"}}}]}"#.into()
            } else if body.contains("VLPLmine") {
                format!(r#"{{"items":[{}]}}"#, video("ddddddddddd", "Song"))
            } else {
                "{}".into()
            }
        };
        let it = innertube(answer, bodies.clone(), Some(signed_in_account()));
        let steps = Mutex::new(Vec::new());
        let data = it.read_account(&|p| steps.lock().unwrap().push(p), &Cancel::new()).unwrap();
        assert_eq!(data.channels.iter().map(|c| c.name.as_str()).collect::<Vec<_>>(), ["First", "Second"]);
        assert_eq!(data.history.len(), 2);
        assert_eq!(data.liked[0].title, "Liked");
        assert_eq!(data.playlists.len(), 1);
        assert_eq!(data.playlists[0].0, "Road trip");
        let steps = steps.into_inner().unwrap();
        assert_eq!(steps.iter().filter(|s| s.new_step).map(|s| s.step).collect::<Vec<_>>(), [1, 2, 3, 4, 5]);
        assert!(steps.iter().any(|s| s.text.contains("2 channels")));
    }

    #[test]
    fn account_reading_needs_a_sign_in_and_can_be_canceled() {
        let bodies = Arc::new(Mutex::new(Vec::new()));
        assert!(innertube(|_| "{}".into(), bodies.clone(), None).read_account(&|_| {}, &Cancel::new()).is_err());
        assert!(bodies.lock().unwrap().is_empty());
        let cancel = Cancel::new();
        cancel.cancel();
        let err = innertube(|_| "{}".into(), bodies, Some(signed_in_account())).read_account(&|_| {}, &cancel).unwrap_err();
        assert!(err.is_canceled());
    }
}
