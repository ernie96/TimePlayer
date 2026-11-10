//! Invidious, the second source (1.x's `InvidiousBackendProvider`, `PublicInstanceDirectory`,
//! `InstanceHealthService` and `FileInstanceCache`): public instances from api.invidious.io and the seeds
//! in Settings, ranked by health and speed, probed before use, cooled down after failures, and cached in
//! cache/instances_invidious.json for a day.

use chrono::{DateTime, Duration as ChronoDuration, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::config::{QualityPreference, Settings};
use crate::error::{AppError, Result};
use crate::http::{Http, Request, escape};
use crate::models::{
    CaptionTrack, ChannelInfo, Comment, CommentPage, CommentSource, Page, PlaylistPage, ResolvedStream, SearchResult, SourceKind,
    StreamCandidate, VideoDetails,
};
use crate::paths;

/// A public instance and how it has been doing.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PublicInstance {
    pub base_uri: String,
    pub provider_type: i32,
    pub is_working: bool,
    pub latency_ms: i64,
    pub health_score: f64,
    pub consecutive_failures: i32,
    pub software_version: Option<String>,
    pub error_message: Option<String>,
}

impl Default for PublicInstance {
    fn default() -> Self {
        Self {
            base_uri: String::new(),
            provider_type: 2,
            is_working: true,
            latency_ms: -1,
            health_score: 100.0,
            consecutive_failures: 0,
            software_version: None,
            error_message: None,
        }
    }
}

impl PublicInstance {
    /// Lower is better: healthy, fast instances first.
    pub fn priority(&self) -> f64 {
        if !self.is_working {
            return f64::MAX;
        }
        let latency = if self.latency_ms > 0 { self.latency_ms as f64 } else { 1000.0 };
        latency + (100.0 - self.health_score).max(0.0) * 50.0 + f64::from(self.consecutive_failures) * 5000.0
    }
}

/// An https address with a host, no user name and not this computer, reduced to scheme://host[:port]/.
pub fn sanitize_base_uri(input: &str) -> Option<String> {
    let trimmed = input.trim();
    let rest = trimmed.strip_prefix("https://")?;
    let authority = rest.split(['/', '?', '#']).next()?;
    if authority.is_empty() || authority.contains('@') {
        return None;
    }
    let host = authority.split(':').next().unwrap_or("").to_lowercase();
    if host.is_empty() || host == "localhost" || host.starts_with("127.") || host == "[::1]" {
        return None;
    }
    Some(format!("https://{}/", authority.to_lowercase()))
}

/// api.invidious.io's list: [name, {api, type, uri, monitor{down, uptime}, stats{software{version}}}].
pub fn parse_directory(json: &str) -> Vec<PublicInstance> {
    let Ok(Value::Array(items)) = serde_json::from_str::<Value>(json) else { return Vec::new() };
    let mut out: Vec<PublicInstance> = Vec::new();
    for item in items {
        let Some(details) = item.get(1).filter(|d| d.is_object()) else { continue };
        if details.get("api") != Some(&Value::Bool(true)) || !details.get("type").and_then(Value::as_str).is_some_and(|t| t.eq_ignore_ascii_case("https")) {
            continue;
        }
        let Some(uri) = details.get("uri").and_then(Value::as_str).and_then(sanitize_base_uri) else { continue };
        let mut health = 90.0;
        if let Some(monitor) = details.get("monitor").filter(|m| m.is_object()) {
            if monitor.get("down") == Some(&Value::Bool(true)) {
                continue;
            }
            if let Some(uptime) = monitor.get("uptime").and_then(Value::as_f64) {
                health = uptime;
            }
        }
        let version = details.pointer("/stats/software/version").and_then(Value::as_str).map(str::to_owned);
        if !out.iter().any(|o| o.base_uri == uri) {
            out.push(PublicInstance { base_uri: uri, health_score: health, software_version: version, ..Default::default() });
        }
    }
    out
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
#[serde(rename_all = "PascalCase", default)]
struct Snapshot {
    provider_type: i32,
    #[serde(with = "crate::models::dateoffset")]
    cached_at_utc: DateTime<Utc>,
    instances: Vec<PublicInstance>,
}

struct RuntimeState {
    instance: PublicInstance,
    cooldown_until: Option<DateTime<Utc>>,
    last_probed: Option<DateTime<Utc>>,
    last_probe_success: bool,
}

#[derive(Clone)]
pub struct Invidious {
    http: Http,
    settings: Settings,
    cache_file: std::path::PathBuf,
    states: Arc<Mutex<HashMap<String, RuntimeState>>>,
}

impl Invidious {
    pub fn new(http: Http, settings: Settings) -> Self {
        Self::with_cache(http, settings, paths::data_folder().join("cache").join("instances_invidious.json"))
    }

    pub fn with_cache(http: Http, settings: Settings, cache_file: std::path::PathBuf) -> Self {
        Self { http, settings, cache_file, states: Arc::default() }
    }

    fn seeds(&self) -> Vec<PublicInstance> {
        self.settings
            .with(|c| c.backend.invidious_seed_instances.clone())
            .iter()
            .filter_map(|s| sanitize_base_uri(s))
            .map(|uri| PublicInstance { base_uri: uri, latency_ms: 200, ..Default::default() })
            .collect()
    }

    fn load_cache(&self) -> Option<Snapshot> {
        let text = std::fs::read_to_string(&self.cache_file).ok()?;
        let mut snapshot: Snapshot = serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()?;
        snapshot.instances.retain(|i| sanitize_base_uri(&i.base_uri).is_some());
        (!snapshot.instances.is_empty()).then_some(snapshot)
    }

    fn save_cache(&self, instances: &[PublicInstance]) {
        let snapshot = Snapshot { provider_type: 2, cached_at_utc: Utc::now(), instances: instances.to_vec() };
        if let Ok(json) = serde_json::to_vec_pretty(&snapshot) {
            let _ = paths::write_atomic(&self.cache_file, &json);
        }
    }

    fn merged(seeds: Vec<PublicInstance>, more: Vec<PublicInstance>) -> Vec<PublicInstance> {
        let mut all = seeds;
        for m in more {
            if !all.iter().any(|a| a.base_uri == m.base_uri) {
                all.push(m);
            }
        }
        all
    }

    /// Seeds plus the live directory (or the cache while it's fresh, or a stale cache when offline).
    pub fn instances(&self) -> Vec<PublicInstance> {
        let ttl = self.settings.with(|c| c.backend.instance_cache_ttl_hours) as i64;
        if let Some(snapshot) = self.load_cache()
            && Utc::now() - snapshot.cached_at_utc < ChronoDuration::hours(ttl) {
                return snapshot.instances;
            }
        let directory = self.settings.with(|c| c.backend.invidious_instances_directory_url.clone());
        match self.http.get_text(&directory, Duration::from_secs(15)) {
            Ok(json) => {
                let merged = Self::merged(self.seeds(), parse_directory(&json));
                if !merged.is_empty() {
                    self.save_cache(&merged);
                    return merged;
                }
            }
            Err(e) => log::warn!("The Invidious directory could not be read: {e}"),
        }
        match self.load_cache() {
            Some(stale) => Self::merged(self.seeds(), stale.instances),
            None => self.seeds(),
        }
    }

    fn probe(&self, instance: &PublicInstance) -> bool {
        let started = Instant::now();
        let response = self
            .http
            .send(Request::get(format!("{}api/v1/stats", instance.base_uri)).timeout(Duration::from_secs(3)).header("Accept", "application/json"));
        match response {
            Ok(r) if r.status == 429 => {
                self.record_failure(instance, "Rate limited (HTTP 429)");
                false
            }
            Ok(r) if r.ok() && r.json().is_ok_and(|j| j.get("software").is_some() || j.get("version").is_some()) => {
                self.record_success(instance, started.elapsed());
                true
            }
            Ok(r) => {
                self.record_failure(instance, &format!("HTTP {}", r.status));
                false
            }
            Err(e) => {
                self.record_failure(instance, &e.message);
                false
            }
        }
    }

    /// The best instance not cooling down, probed unless it answered well in the last five minutes.
    pub fn select(&self) -> Option<PublicInstance> {
        let instances = self.instances();
        if instances.is_empty() {
            return None;
        }
        let now = Utc::now();
        let mut candidates: Vec<PublicInstance> = {
            let mut states = self.states.lock().unwrap();
            for i in &instances {
                states.entry(i.base_uri.clone()).or_insert_with(|| RuntimeState {
                    instance: i.clone(),
                    cooldown_until: None,
                    last_probed: None,
                    last_probe_success: false,
                });
            }
            instances
                .iter()
                .filter_map(|i| states.get(&i.base_uri))
                .filter(|s| s.cooldown_until.is_none_or(|c| c <= now))
                .map(|s| s.instance.clone())
                .collect()
        };
        candidates.sort_by(|a, b| a.priority().total_cmp(&b.priority()));
        if candidates.is_empty() {
            log::warn!("Every Invidious instance is cooling down; trying the one that's nearest to ready.");
            let mut states = self.states.lock().unwrap();
            let earliest = states.values_mut().min_by_key(|s| s.cooldown_until)?;
            earliest.cooldown_until = None;
            return Some(earliest.instance.clone());
        }
        for candidate in candidates.iter().take(3) {
            let fresh = {
                let states = self.states.lock().unwrap();
                states
                    .get(&candidate.base_uri)
                    .is_some_and(|s| s.last_probe_success && s.last_probed.is_some_and(|p| now - p < ChronoDuration::minutes(5)))
            };
            if fresh || self.probe(candidate) {
                return self.states.lock().unwrap().get(&candidate.base_uri).map(|s| s.instance.clone());
            }
        }
        candidates.into_iter().next()
    }

    pub fn record_success(&self, instance: &PublicInstance, latency: Duration) {
        let mut states = self.states.lock().unwrap();
        let state = states.entry(instance.base_uri.clone()).or_insert_with(|| RuntimeState {
            instance: instance.clone(),
            cooldown_until: None,
            last_probed: None,
            last_probe_success: false,
        });
        state.cooldown_until = None;
        state.last_probed = Some(Utc::now());
        state.last_probe_success = true;
        state.instance.is_working = true;
        state.instance.latency_ms = latency.as_millis() as i64;
        state.instance.consecutive_failures = 0;
        state.instance.health_score = (state.instance.health_score + 2.0).min(100.0);
        state.instance.error_message = None;
    }

    pub fn record_failure(&self, instance: &PublicInstance, reason: &str) {
        let mut states = self.states.lock().unwrap();
        let state = states.entry(instance.base_uri.clone()).or_insert_with(|| RuntimeState {
            instance: instance.clone(),
            cooldown_until: None,
            last_probed: None,
            last_probe_success: false,
        });
        state.instance.consecutive_failures += 1;
        state.last_probed = Some(Utc::now());
        state.last_probe_success = false;
        let minutes = if reason.contains("429") { 15 } else { (2 * state.instance.consecutive_failures).min(30) as i64 };
        state.cooldown_until = Some(Utc::now() + ChronoDuration::minutes(minutes));
        state.instance.is_working = state.instance.consecutive_failures < 4;
        state.instance.health_score = (state.instance.health_score - 15.0).max(0.0);
        state.instance.error_message = Some(reason.to_owned());
        log::warn!("Invidious instance {} failed: {reason}. Cooling down for {minutes} minutes.", instance.base_uri);
    }

    /// GET on the best instances in turn; a 404 is final, a 429, an error or a timeout tries another.
    fn get(&self, path: &str, target_video: Option<&str>) -> Result<(Value, String)> {
        let backend = self.settings.with(|c| c.backend.clone());
        let max_attempts = (backend.max_retries_per_provider + 1).max(1);
        let mut tried = HashSet::new();
        let mut attempts = 0;
        while attempts < max_attempts {
            attempts += 1;
            let Some(instance) = self.select() else { break };
            if !tried.insert(instance.base_uri.clone()) {
                break;
            }
            let timeout = if path.starts_with("api/v1/search") { backend.request_timeout_seconds.min(7) } else { backend.request_timeout_seconds };
            let started = Instant::now();
            let response = self.http.send(
                Request::get(format!("{}{path}", instance.base_uri)).timeout(Duration::from_secs(timeout)).header("Accept", "application/json"),
            );
            match response {
                Ok(r) if r.status == 404 => return Err(AppError::video_not_found(target_video.unwrap_or(path))),
                Ok(r) if r.status == 429 => self.record_failure(&instance, "Rate limited (HTTP 429)"),
                Ok(r) if !r.ok() => self.record_failure(&instance, &format!("HTTP {}", r.status)),
                Ok(r) => match r.json() {
                    Ok(json) => {
                        self.record_success(&instance, started.elapsed());
                        return Ok((json, instance.base_uri));
                    }
                    Err(_) => self.record_failure(&instance, "Empty or invalid JSON body returned"),
                },
                Err(e) => self.record_failure(&instance, &e.message),
            }
        }
        Err(AppError::new(
            "ALL_INSTANCES_FAILED",
            format!("All {attempts} public Invidious instances failed to respond. Failing over to next provider."),
        ))
    }

    pub fn search(&self, query: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        if query.trim().is_empty() {
            return Err(AppError::new("EMPTY_QUERY", "Search query cannot be empty."));
        }
        let page: usize = page_token.and_then(|t| t.parse().ok()).filter(|p| *p > 0).unwrap_or(1);
        let (json, _) = self.get(&format!("api/v1/search?q={}&page={page}&type=video", escape(query)), None)?;
        let items = map_videos(&json);
        let next = (!items.is_empty()).then(|| (page + 1).to_string());
        Ok(Page { items, next_token: next })
    }

    pub fn related(&self, video_id: &str) -> Result<Vec<SearchResult>> {
        let (json, _) = self.get(&format!("api/v1/videos/{}?fields=recommendedVideos", escape(video_id)), Some(video_id))?;
        Ok(json
            .get("recommendedVideos")
            .and_then(Value::as_array)
            .map(|list| {
                list.iter()
                    .filter_map(|v| {
                        let id = v.get("videoId")?.as_str().filter(|s| !s.trim().is_empty())?;
                        Some(SearchResult {
                            channel_id: str_of(v, "authorId"),
                            duration: v.get("lengthSeconds").and_then(Value::as_u64).filter(|s| *s > 0).map(Duration::from_secs),
                            upload_date_text: str_of(v, "publishedText"),
                            ..SearchResult::video(
                                id,
                                v.get("title").and_then(Value::as_str).unwrap_or("Untitled video"),
                                v.get("author").and_then(Value::as_str).unwrap_or("Unknown channel"),
                            )
                        })
                    })
                    .collect()
            })
            .unwrap_or_default())
    }

    pub fn comments(&self, video_id: &str, continuation: Option<&str>, is_reply_page: bool) -> Result<CommentPage> {
        let mut path = format!("api/v1/comments/{}", escape(video_id));
        if let Some(c) = continuation.filter(|c| !c.trim().is_empty()) {
            path.push_str(&format!("?continuation={}", escape(c)));
        }
        let (json, _) = self.get(&path, Some(video_id))?;
        let comments = json
            .get("comments")
            .and_then(Value::as_array)
            .map(|list| {
                list.iter()
                    .filter_map(|c| {
                        let text = c.get("content")?.as_str().filter(|t| !t.trim().is_empty())?;
                        let likes = c.get("likeCount").and_then(Value::as_i64).filter(|l| *l > 0);
                        Some(Comment {
                            id: str_of(c, "commentId").unwrap_or_else(|| uuid::Uuid::new_v4().simple().to_string()),
                            author: str_of(c, "author").unwrap_or_else(|| "Unknown".into()),
                            author_channel_id: str_of(c, "authorId"),
                            text: text.trim().into(),
                            published_text: str_of(c, "publishedText"),
                            likes_text: likes.map(format_likes),
                            reply_count: c.pointer("/replies/replyCount").and_then(Value::as_i64).unwrap_or(0),
                            replies_token: c.pointer("/replies/continuation").and_then(Value::as_str).map(str::to_owned),
                            pinned: c.get("isPinned") == Some(&Value::Bool(true)),
                            creator: c.get("authorIsChannelOwner") == Some(&Value::Bool(true)),
                            is_reply: is_reply_page,
                            source: CommentSource::Invidious,
                        })
                    })
                    .collect()
            })
            .unwrap_or_default();
        Ok(CommentPage { comments, next_token: str_of(&json, "continuation"), source: CommentSource::Invidious })
    }

    /// A channel's uploads, newest first.
    pub fn channel_videos(&self, channel_id: &str, continuation: Option<&str>) -> Result<Page<SearchResult>> {
        let mut path = format!("api/v1/channels/{}/videos", escape(channel_id));
        if let Some(c) = continuation.filter(|c| !c.trim().is_empty()) {
            path.push_str(&format!("?continuation={}", escape(c)));
        }
        let (json, _) = self.get(&path, None)?;
        let items = map_videos(json.get("videos").unwrap_or(&Value::Null));
        let next = if items.is_empty() { None } else { str_of(&json, "continuation").filter(|c| !c.is_empty()) };
        Ok(Page { items, next_token: next })
    }

    /// One page (about 100 videos) of a playlist; pages start at 1.
    pub fn playlist(&self, playlist_id: &str, page: usize) -> Result<PlaylistPage> {
        let (json, _) = self.get(&format!("api/v1/playlists/{}?page={page}", escape(playlist_id)), None)?;
        let videos = map_videos(json.get("videos").unwrap_or(&Value::Null));
        let total = json.get("videoCount").and_then(Value::as_u64).unwrap_or(0) as usize;
        let next = (videos.len() >= 100 && page * videos.len() < total).then(|| (page + 1).to_string());
        Ok(PlaylistPage {
            playlist_id: playlist_id.into(),
            title: str_of(&json, "title").unwrap_or_default(),
            owner: str_of(&json, "author"),
            videos,
            next_token: next,
        })
    }

    pub fn popular(&self) -> Result<Vec<SearchResult>> {
        let (json, _) = self.get("api/v1/popular", None)?;
        let items = map_videos(&json);
        if items.is_empty() { Err(AppError::new("EMPTY_FEED", "The featured list is empty right now.")) } else { Ok(items) }
    }

    pub fn details(&self, video_id: &str) -> Result<VideoDetails> {
        let (v, base) = self.get(&format!("api/v1/videos/{video_id}"), Some(video_id))?;
        Ok(VideoDetails {
            id: video_id.into(),
            title: str_of(&v, "title").unwrap_or_else(|| "Untitled Video".into()),
            description: str_of(&v, "description"),
            channel: ChannelInfo {
                id: str_of(&v, "authorId").unwrap_or_else(|| video_id.into()),
                name: str_of(&v, "author").unwrap_or_else(|| "Unknown Channel".into()),
            },
            duration: Duration::from_secs(v.get("lengthSeconds").and_then(Value::as_u64).unwrap_or(0)),
            view_count: v.get("viewCount").and_then(Value::as_i64),
            like_count: v.get("likeCount").and_then(Value::as_i64),
            thumbnail_url: best_thumbnail(&v, "videoThumbnails"),
            streams: map_streams(&v),
            captions: map_captions(&v, &base),
            is_live: v.get("liveNow") == Some(&Value::Bool(true)),
            ..Default::default()
        })
    }

    /// The best stream for the chosen quality, with a separate audio track when the video is DASH.
    pub fn resolve(&self, video_id: &str, quality: QualityPreference, audio_only: bool) -> Result<ResolvedStream> {
        let details = self.details(video_id)?;
        pick_streams(video_id, &details, quality, audio_only)
    }
}

/// Chooses among a video's streams (shared with tests).
pub fn pick_streams(video_id: &str, details: &VideoDetails, quality: QualityPreference, audio_only: bool) -> Result<ResolvedStream> {
    let streams = &details.streams;
    if streams.is_empty() {
        return Err(AppError::stream_unavailable(video_id, Some("Invidious response contained no playable streams.")));
    }
    let base = ResolvedStream {
        video_id: video_id.into(),
        captions: details.captions.clone(),
        alternatives: streams.clone(),
        source: SourceKind::Invidious,
        ..Default::default()
    };
    if details.is_live {
        let live = streams.iter().find(|s| s.format.eq_ignore_ascii_case("hls")).unwrap_or(&streams[0]);
        return Ok(ResolvedStream { url: live.url.clone(), is_live: true, ..base });
    }
    let best_audio = streams.iter().filter(|s| s.audio_only).max_by_key(|s| s.bitrate.unwrap_or(0));
    if audio_only || quality == QualityPreference::AudioOnly {
        let audio = best_audio.unwrap_or(&streams[0]);
        return Ok(ResolvedStream { url: audio.url.clone(), ..base });
    }
    let mut candidates: Vec<&StreamCandidate> = streams.iter().filter(|s| !s.audio_only && (s.progressive || best_audio.is_some())).collect();
    if candidates.is_empty() {
        candidates = streams.iter().filter(|s| !s.audio_only).collect();
    }
    if candidates.is_empty() {
        candidates = streams.iter().collect();
    }
    let mut selected: Option<&StreamCandidate> = None;
    if let Some(target) = quality.max_height() {
        selected = candidates
            .iter()
            .filter(|s| s.height == Some(target) && s.progressive)
            .max_by_key(|s| s.fps.unwrap_or(0))
            .or_else(|| candidates.iter().filter(|s| s.height == Some(target)).max_by_key(|s| s.fps.unwrap_or(0)))
            .or_else(|| candidates.iter().filter(|s| s.height.is_some_and(|h| h <= target)).max_by_key(|s| s.height.unwrap_or(0)))
            .copied();
    }
    let selected = selected
        .or_else(|| candidates.iter().max_by_key(|s| (s.height.unwrap_or(0), s.fps.unwrap_or(0), s.bitrate.unwrap_or(0))).copied())
        .unwrap_or(&streams[0]);
    let audio_url = if selected.progressive { None } else { best_audio.map(|a| a.url.clone()) };
    Ok(ResolvedStream { url: selected.url.clone(), audio_url, ..base })
}

fn str_of(v: &Value, key: &str) -> Option<String> {
    v.get(key).and_then(Value::as_str).map(str::to_owned)
}

fn best_thumbnail(v: &Value, key: &str) -> Option<String> {
    v.get(key)?.as_array()?.iter().max_by_key(|t| t.get("width").and_then(Value::as_i64).unwrap_or(0)).and_then(|t| str_of(t, "url"))
}

fn format_likes(count: i64) -> String {
    match count {
        1 => "1 like".into(),
        c if c >= 1_000_000 => format!("{} million likes", crate::text::trim_number(c as f64 / 1e6, 1).replace(',', ".")),
        c if c >= 1_000 => format!("{} thousand likes", crate::text::trim_number(c as f64 / 1e3, 1).replace(',', ".")),
        c => format!("{c} likes"),
    }
}

/// Videos from a search, popular or channel list; playlists and channels are left out.
pub fn map_videos(list: &Value) -> Vec<SearchResult> {
    let mut seen = HashSet::new();
    list.as_array()
        .map(|items| {
            items
                .iter()
                .filter_map(|item| {
                    let id = item.get("videoId")?.as_str().filter(|s| !s.trim().is_empty())?;
                    let kind = item.get("type").and_then(Value::as_str).unwrap_or("");
                    if !kind.is_empty() && !kind.eq_ignore_ascii_case("video") && !kind.eq_ignore_ascii_case("shortVideo") {
                        return None;
                    }
                    if !seen.insert(id.to_owned()) {
                        return None;
                    }
                    let live = item.get("liveNow") == Some(&Value::Bool(true));
                    Some(SearchResult {
                        channel_id: str_of(item, "authorId"),
                        duration: if live { None } else { item.get("lengthSeconds").and_then(Value::as_u64).filter(|s| *s > 0).map(Duration::from_secs) },
                        // Live streams say 0 views and "0 seconds ago", which is noise.
                        view_count: item.get("viewCount").and_then(Value::as_i64).filter(|v| *v > 0),
                        upload_date_text: if live { None } else { str_of(item, "publishedText") },
                        is_live: live,
                        is_short: kind.eq_ignore_ascii_case("shortVideo"),
                        thumbnail_url: best_thumbnail(item, "videoThumbnails"),
                        description_snippet: str_of(item, "description"),
                        ..SearchResult::video(
                            id,
                            item.get("title").and_then(Value::as_str).unwrap_or("Untitled Video"),
                            item.get("author").and_then(Value::as_str).unwrap_or("Unknown Channel"),
                        )
                    })
                })
                .collect()
        })
        .unwrap_or_default()
}

fn resolution(text: Option<&str>) -> (Option<u32>, Option<u32>) {
    let Some(text) = text.filter(|t| !t.trim().is_empty()) else { return (None, None) };
    if let Some((w, h)) = text.split_once('x')
        && let (Ok(w), Ok(h)) = (w.parse(), h.parse()) {
            return (Some(h), Some(w));
        }
    let digits: String = text.chars().filter(char::is_ascii_digit).collect();
    (digits.parse().ok(), None)
}

fn bitrate(v: Option<&Value>) -> Option<i64> {
    match v? {
        Value::Number(n) => n.as_i64(),
        Value::String(s) => s.parse().ok(),
        _ => None,
    }
}

fn map_streams(v: &Value) -> Vec<StreamCandidate> {
    let mut list = Vec::new();
    for f in v.get("formatStreams").and_then(Value::as_array).into_iter().flatten() {
        let Some(url) = str_of(f, "url").filter(|u| !u.trim().is_empty()) else { continue };
        let (height, width) = resolution(f.get("resolution").and_then(Value::as_str));
        list.push(StreamCandidate {
            url,
            format: str_of(f, "container").map(|c| c.to_lowercase()).unwrap_or_else(|| "mp4".into()),
            quality_label: str_of(f, "qualityLabel").or_else(|| height.map(|h| format!("{h}p"))).or_else(|| str_of(f, "quality")),
            height,
            width,
            bitrate: bitrate(f.get("bitrate")),
            video_codec: str_of(f, "encoding"),
            progressive: true,
            ..Default::default()
        });
    }
    for a in v.get("adaptiveFormats").and_then(Value::as_array).into_iter().flatten() {
        let Some(url) = str_of(a, "url").filter(|u| !u.trim().is_empty()) else { continue };
        let is_audio = a.get("type").and_then(Value::as_str).is_some_and(|t| t.to_lowercase().starts_with("audio/"))
            || a.get("audioQuality").and_then(Value::as_str).is_some_and(|q| !q.is_empty());
        let (height, width) = resolution(a.get("resolution").and_then(Value::as_str));
        let rate = bitrate(a.get("bitrate"));
        if is_audio {
            list.push(StreamCandidate {
                url,
                format: str_of(a, "container").map(|c| c.to_lowercase()).unwrap_or_else(|| "m4a".into()),
                quality_label: str_of(a, "audioQuality"),
                bitrate: rate,
                audio_bitrate: rate,
                audio_codec: str_of(a, "encoding"),
                audio_only: true,
                ..Default::default()
            });
        } else {
            list.push(StreamCandidate {
                url,
                format: str_of(a, "container").map(|c| c.to_lowercase()).unwrap_or_else(|| "mp4".into()),
                quality_label: str_of(a, "qualityLabel").or_else(|| height.map(|h| format!("{h}p"))),
                height,
                width,
                fps: a.get("fps").and_then(Value::as_u64).map(|f| f as u32),
                bitrate: rate,
                video_codec: str_of(a, "encoding"),
                video_only: true,
                ..Default::default()
            });
        }
    }
    if let Some(hls) = str_of(v, "hlsUrl").filter(|u| !u.trim().is_empty()) {
        list.push(StreamCandidate { url: hls, format: "hls".into(), quality_label: Some("Live HLS".into()), progressive: true, ..Default::default() });
    }
    list
}

fn map_captions(v: &Value, base: &str) -> Vec<CaptionTrack> {
    v.get("captions")
        .and_then(Value::as_array)
        .map(|list| {
            list.iter()
                .filter_map(|c| {
                    let url = str_of(c, "url").filter(|u| !u.trim().is_empty())?;
                    let code = str_of(c, "languageCode").or_else(|| str_of(c, "language_code")).filter(|l| !l.trim().is_empty())?;
                    // Invidious gives its caption addresses relative to the instance.
                    let url = if url.starts_with('/') { format!("{}{}", base.trim_end_matches('/'), url) } else { url };
                    Some(CaptionTrack {
                        display_name: str_of(c, "label").unwrap_or_else(|| code.clone()),
                        language_code: code,
                        url,
                        format: "vtt".into(),
                        auto_generated: str_of(c, "label").is_some_and(|l| l.to_lowercase().contains("auto-generated")),
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
    use crate::config::AppConfig;
    use crate::http::Response;

    #[test]
    fn directory_keeps_working_https_api_instances() {
        let json = r#"[["a",{"api":true,"type":"https","uri":"https://good.example","monitor":{"down":false,"uptime":97.5}}],
            ["b",{"api":false,"type":"https","uri":"https://noapi.example"}],
            ["c",{"api":true,"type":"onion","uri":"http://x.onion"}],
            ["d",{"api":true,"type":"https","uri":"https://down.example","monitor":{"down":true}}]]"#;
        let found = parse_directory(json);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].base_uri, "https://good.example/");
        assert_eq!(found[0].health_score, 97.5);
        assert_eq!(sanitize_base_uri("https://user@evil.example"), None);
        assert_eq!(sanitize_base_uri("https://localhost:3000"), None);
    }

    #[test]
    fn a_failing_instance_cools_down_and_another_is_tried() {
        let cache = std::env::temp_dir().join(format!("tp2-inv-{}.json", uuid::Uuid::new_v4().simple()));
        let mut config = AppConfig::default();
        config.backend.invidious_seed_instances = vec!["https://one.example".into(), "https://two.example".into()];
        config.backend.invidious_instances_directory_url = "https://directory.example/".into();
        let http = Http::with_transport(|r: Request| {
            let body = if r.url.contains("directory") {
                "[]".to_string()
            } else if r.url.contains("one.example") {
                return Ok(Response { status: 500, ..Default::default() });
            } else if r.url.contains("stats") {
                r#"{"software":{"version":"2"}}"#.to_string()
            } else {
                r#"[{"type":"video","videoId":"dQw4w9WgXcQ","title":"Song","author":"Rick","lengthSeconds":213}]"#.to_string()
            };
            Ok(Response { status: 200, body: body.into_bytes(), ..Default::default() })
        });
        let inv = Invidious::with_cache(http, Settings::in_memory(config), cache.clone());
        let page = inv.search("song", None).unwrap();
        assert_eq!(page.items[0].duration, Some(Duration::from_secs(213)));
        assert_eq!(page.next_token.as_deref(), Some("2"));
        let _ = std::fs::remove_file(cache);
    }

    #[test]
    fn picks_dash_video_with_its_audio() {
        let v: Value = serde_json::from_str(
            r#"{"lengthSeconds":10,"formatStreams":[{"url":"p360","resolution":"640x360","container":"mp4"}],
            "adaptiveFormats":[{"url":"v720","resolution":"1280x720","type":"video/mp4","bitrate":"900"},
                               {"url":"a1","type":"audio/mp4","audioQuality":"AUDIO_QUALITY_MEDIUM","bitrate":128000}]}"#,
        )
        .unwrap();
        let details = VideoDetails { streams: map_streams(&v), ..Default::default() };
        let r = pick_streams("x", &details, QualityPreference::P720, false).unwrap();
        assert_eq!((r.url.as_str(), r.audio_url.as_deref()), ("v720", Some("a1")));
        let audio = pick_streams("x", &details, QualityPreference::Auto, true).unwrap();
        assert_eq!(audio.url, "a1");
    }
}
