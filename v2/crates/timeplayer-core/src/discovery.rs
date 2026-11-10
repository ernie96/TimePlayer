//! Finding videos without an account (1.x's `DiscoveryService`, `WatchPageService`, `PlaylistService` and
//! `RecommendationService`): Featured from YouTube's own pages and charts, channels and What's New from
//! YouTube's public RSS feeds, Up next and comments, playlists, and For You worked out on this computer.

use chrono::{DateTime, Duration as ChronoDuration, Utc};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use crate::error::{AppError, Result};
use crate::http::{Cancel, Http, Request, escape};
use crate::innertube::InnerTube;
use crate::invidious::Invidious;
use crate::library::{ChannelSubscription, Library, SubscriptionFeed};
use crate::models::{Comment, CommentPage, CommentSource, Page, PlaylistPage, SearchResult, WatchNext};
use crate::xml;

// YouTube's "destination" channels, which replaced the Trending page in 2025.
const MUSIC_CHANNEL: &str = "UC-9-kyTW8ZkZNDHQJ6FgpwQ";
const GAMING_CHANNEL: &str = "UCOpNcN46UbXVtpKMrmU4Abg";
const NEWS_CHANNEL: &str = "UCYfdidRxbB8Qhf0Nx7ioOYw";
const SPORTS_CHANNEL: &str = "UCEgdi0XIXXZ-qJOFPf4JSKw";
const LIVE_CHANNEL: &str = "UC4R8DWoMoI7CAwX8_LjQHig";
/// "Top 100 Music Videos Global", when the region has no chart of its own.
const GLOBAL_MUSIC_CHART: &str = "PL4fGSI1pDJn5kI81J1fYWK5eZRl1zJ5kM";
/// Search filters: uploaded this week, videos only; and this week sorted by views.
const THIS_WEEK_VIDEOS: &str = "EgQIAxAB";
const THIS_WEEK_BY_VIEWS: &str = "CAMSAggD";
const MAX_PARALLEL_CHANNELS: usize = 8;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default)]
pub enum FeaturedCategory {
    /// A mix of what YouTube's Music, Sports, News and Gaming pages feature now.
    #[default]
    Trending,
    /// The region's Top 100 music videos.
    Music,
    /// Gaming videos from this week.
    Gaming,
    /// The most watched trailers this week.
    Movies,
    News,
    Sports,
    Live,
}

impl FeaturedCategory {
    pub const ALL: [FeaturedCategory; 7] = [Self::Trending, Self::Music, Self::Gaming, Self::Movies, Self::News, Self::Sports, Self::Live];
}

/// Runs `f` on each item with at most `limit` at a time; results keep the items' order.
pub fn parallel_map<T: Sync, R: Send>(items: &[T], limit: usize, f: impl Fn(&T) -> R + Sync) -> Vec<R> {
    let mut out: Vec<Option<R>> = (0..items.len()).map(|_| None).collect();
    for (chunk_items, chunk_out) in items.chunks(limit.max(1)).zip(out.chunks_mut(limit.max(1))) {
        std::thread::scope(|scope| {
            let handles: Vec<_> = chunk_items.iter().map(|item| scope.spawn(|| f(item))).collect();
            for (slot, handle) in chunk_out.iter_mut().zip(handles) {
                *slot = handle.join().ok();
            }
        });
    }
    out.into_iter().map(|r| r.expect("a worker finished")).collect()
}

/// A small random number source (no extra crate for a little shuffling).
pub fn random_unit() -> f64 {
    use std::sync::atomic::{AtomicU64, Ordering};
    static STATE: AtomicU64 = AtomicU64::new(0);
    let mut x = STATE.load(Ordering::Relaxed);
    if x == 0 {
        x = (std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_nanos() as u64) | 1;
    }
    x ^= x << 13;
    x ^= x >> 7;
    x ^= x << 17;
    STATE.store(x, Ordering::Relaxed);
    (x >> 11) as f64 / (1u64 << 53) as f64
}

pub fn shuffle<T>(items: &mut [T]) {
    for i in (1..items.len()).rev() {
        let j = (random_unit() * (i + 1) as f64) as usize;
        items.swap(i, j.min(i));
    }
}

#[derive(Clone)]
pub struct Discovery {
    http: Http,
    inner_tube: InnerTube,
    invidious: Invidious,
}

/// A channel's newest uploads from its RSS feed.
#[derive(Debug, Clone)]
pub struct ChannelUploads {
    pub channel_id: String,
    pub channel_name: String,
    pub videos: Vec<SearchResult>,
}

impl Discovery {
    pub fn new(http: Http, inner_tube: InnerTube, invidious: Invidious) -> Self {
        Self { http, inner_tube, invidious }
    }

    /// Featured videos for a category as seen from `region`; the token is None for the first page.
    pub fn featured(&self, category: FeaturedCategory, region: &str, token: Option<&str>) -> Result<Page<SearchResult>> {
        let region = if region.trim().is_empty() { "US".to_owned() } else { region.trim().to_uppercase() };
        let result = match category {
            FeaturedCategory::Music => self.music_chart(&region, token),
            FeaturedCategory::Gaming => self.inner_tube.search_with("gaming", Some(THIS_WEEK_VIDEOS), token, Some(&region)),
            FeaturedCategory::Movies => self.inner_tube.search_with("official movie trailer", Some(THIS_WEEK_BY_VIEWS), token, Some(&region)),
            FeaturedCategory::News => self.page_videos(NEWS_CHANNEL, &region),
            FeaturedCategory::Sports => self.page_videos(SPORTS_CHANNEL, &region),
            FeaturedCategory::Live => self.page_videos(LIVE_CHANNEL, &region),
            FeaturedCategory::Trending => self.trending_mix(&region),
        };
        match result {
            Ok(page) if !page.items.is_empty() || token.is_some() => Ok(page),
            other => {
                if category == FeaturedCategory::Trending
                    && let Ok(popular) = self.invidious.popular() {
                        return Ok(Page::single(popular));
                    }
                match other {
                    Ok(_) => Err(AppError::new("FEATURED_EMPTY", "YouTube returned no videos for this category.")),
                    Err(e) => Err(e),
                }
            }
        }
    }

    /// The region's "Top 100 Music Videos" chart; tokens carry the chart's ID: "PLAYLIST|continuation".
    fn music_chart(&self, region: &str, token: Option<&str>) -> Result<Page<SearchResult>> {
        let (playlist_id, continuation) = match token.and_then(|t| t.split_once('|')) {
            Some((id, c)) => (id.to_owned(), Some(c.to_owned())),
            None => {
                let id = self
                    .inner_tube
                    .browse(MUSIC_CHANNEL, Some(region))
                    .ok()
                    .and_then(|(_, lists)| lists.into_iter().find(|p| p.title.to_lowercase().starts_with("top 100 music videos")).map(|p| p.id))
                    .unwrap_or_else(|| GLOBAL_MUSIC_CHART.into());
                (id, None)
            }
        };
        let page = self.inner_tube.playlist(&playlist_id, continuation.as_deref())?;
        Ok(Page { items: page.videos, next_token: page.next_token.map(|n| format!("{playlist_id}|{n}")) })
    }

    fn page_videos(&self, browse_id: &str, region: &str) -> Result<Page<SearchResult>> {
        Ok(Page::single(self.inner_tube.browse(browse_id, Some(region))?.0))
    }

    /// What YouTube's Music, Sports, News and Gaming pages feature now, plus this week's gaming, taken in
    /// turn so no topic fills the list.
    fn trending_mix(&self, region: &str) -> Result<Page<SearchResult>> {
        let sources = [MUSIC_CHANNEL, SPORTS_CHANNEL, NEWS_CHANNEL, GAMING_CHANNEL, ""];
        let results = parallel_map(&sources, 5, |id| {
            if id.is_empty() {
                self.inner_tube.search_with("gaming", Some(THIS_WEEK_VIDEOS), None, Some(region)).map(|p| p.items)
            } else {
                self.inner_tube.browse(id, Some(region)).map(|(v, _)| v)
            }
        });
        let mut first_error = None;
        let lists: Vec<Vec<SearchResult>> = results
            .into_iter()
            .filter_map(|r| match r {
                Ok(v) => Some(v),
                Err(e) => {
                    first_error.get_or_insert(e);
                    None
                }
            })
            .collect();
        if lists.is_empty() {
            return Err(first_error.unwrap_or_else(|| AppError::network("No featured lists answered.")));
        }
        let mut mixed = Vec::new();
        let mut seen = HashSet::new();
        for i in 0..25 {
            for list in &lists {
                if let Some(v) = list.get(i).filter(|v| !v.is_live && seen.insert(v.id.clone())) {
                    mixed.push(v.clone());
                }
            }
        }
        Ok(Page::single(mixed))
    }

    /// A channel's 15 newest uploads, from its public RSS feed.
    pub fn channel_uploads(&self, channel_id: &str) -> Result<ChannelUploads> {
        let url = format!("https://www.youtube.com/feeds/videos.xml?channel_id={}", escape(channel_id));
        let response = self
            .http
            .send(Request::get(&url).timeout(Duration::from_secs(15)).header("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TimePlayer"))?;
        if !response.ok() {
            return Err(AppError::new("CHANNEL_FEED", format!("Channel feed returned HTTP {}.", response.status)));
        }
        parse_channel_feed(&response.text(), channel_id).ok_or_else(|| AppError::new("CHANNEL_FEED", "Could not read the channel feed."))
    }

    /// Older uploads beyond the feed's 15, a page at a time (Invidious).
    pub fn channel_uploads_page(&self, channel_id: &str, token: Option<&str>) -> Result<Page<SearchResult>> {
        self.invidious.channel_videos(channel_id, token)
    }

    /// Every subscription's newest uploads, eight channels at a time, newest first.
    pub fn subscription_feed(&self, subscriptions: &[ChannelSubscription], progress: &(dyn Fn(usize) + Sync), cancel: &Cancel) -> SubscriptionFeed {
        let done = std::sync::atomic::AtomicUsize::new(0);
        let results = parallel_map(subscriptions, MAX_PARALLEL_CHANNELS, |sub| {
            let r = if cancel.is_canceled() { Err(AppError::canceled()) } else { self.channel_uploads(&sub.channel_id) };
            progress(done.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1);
            r
        });
        let mut videos = Vec::new();
        let mut failed = Vec::new();
        for (sub, result) in subscriptions.iter().zip(results) {
            match result {
                Ok(uploads) => videos.extend(uploads.videos),
                Err(_) => failed.push(sub.clone()),
            }
        }
        let mut seen = HashSet::new();
        videos.retain(|v: &SearchResult| seen.insert(v.id.clone()));
        videos.sort_by(|a, b| b.published_at.cmp(&a.published_at));
        SubscriptionFeed { videos, failed_channels: failed, fetched_utc: Utc::now() }
    }
}

/// A channel's Atom feed (yt: and media: elements).
pub fn parse_channel_feed(text: &str, channel_id: &str) -> Option<ChannelUploads> {
    let feed = xml::parse(text)?;
    let channel_name = feed.child("author").and_then(|a| a.child_text("name")).or_else(|| feed.child_text("title")).unwrap_or_else(|| channel_id.into());
    let videos = feed
        .children_named("entry")
        .filter_map(|entry| {
            let id = entry.child_text("videoId")?;
            let link = entry.child("link").and_then(|l| l.attr("href")).unwrap_or("");
            let group = entry.child("group");
            let views = group.and_then(|g| g.child("community")).and_then(|c| c.child("statistics")).and_then(|s| s.attr("views")?.parse().ok());
            Some(SearchResult {
                channel_id: entry.child_text("channelId").or_else(|| Some(channel_id.into())),
                published_at: entry.child_text("published").and_then(|p| DateTime::parse_from_rfc3339(&p).ok()).map(|d| d.with_timezone(&Utc)),
                view_count: views,
                is_short: link.to_lowercase().contains("/shorts/"),
                thumbnail_url: group.and_then(|g| g.child("thumbnail")).and_then(|t| t.attr("url")).map(str::to_owned),
                description_snippet: group.and_then(|g| g.child_text("description")).map(|d| d.chars().take(200).collect()),
                ..SearchResult::video(
                    &id,
                    &entry.child_text("title").unwrap_or_else(|| "Untitled video".into()),
                    &entry.child("author").and_then(|a| a.child_text("name")).unwrap_or_else(|| channel_name.clone()),
                )
            })
        })
        .collect();
    Some(ChannelUploads { channel_id: channel_id.into(), channel_name, videos })
}

/// Up next and comments: InnerTube first, Invidious when it fails.
#[derive(Clone)]
pub struct WatchPage {
    inner_tube: InnerTube,
    invidious: Invidious,
    comment_tokens: Arc<Mutex<HashMap<String, String>>>,
}

impl WatchPage {
    pub fn new(inner_tube: InnerTube, invidious: Invidious) -> Self {
        Self { inner_tube, invidious, comment_tokens: Arc::default() }
    }

    pub fn watch_next(&self, video_id: &str) -> Result<WatchNext> {
        let inner = self.inner_tube.watch_next(video_id);
        if let Ok(next) = &inner {
            if let Some(token) = &next.comments_token {
                let mut tokens = self.comment_tokens.lock().unwrap();
                // One per video watched; a long session shouldn't keep them all.
                if tokens.len() > 200 {
                    tokens.clear();
                }
                tokens.insert(video_id.into(), token.clone());
            }
            if !next.related.is_empty() {
                return inner;
            }
        }
        match self.invidious.related(video_id) {
            Ok(related) => Ok(WatchNext {
                related,
                comments_token: inner.as_ref().ok().and_then(|n| n.comments_token.clone()),
                description: inner.as_ref().ok().and_then(|n| n.description.clone()),
            }),
            Err(e) => inner.or(Err(e)),
        }
    }

    /// The first page when `previous` is None, otherwise the page after it.
    pub fn comments(&self, video_id: &str, previous: Option<&CommentPage>) -> Result<CommentPage> {
        if let Some(prev) = previous {
            let Some(token) = prev.next_token.as_ref().filter(|t| !t.is_empty()) else {
                return Ok(CommentPage { source: prev.source, ..Default::default() });
            };
            return match prev.source {
                CommentSource::InnerTube => self.inner_tube.comments(token, false),
                CommentSource::Invidious => self.invidious.comments(video_id, Some(token), false),
            };
        }
        let mut token = self.comment_tokens.lock().unwrap().get(video_id).cloned();
        if token.is_none()
            && let Ok(next) = self.inner_tube.watch_next(video_id) {
                token = next.comments_token;
            }
        if let Some(t) = token
            && let Ok(page) = self.inner_tube.comments(&t, false)
                && !page.comments.is_empty() {
                    return Ok(page);
                }
        self.invidious.comments(video_id, None, false)
    }

    pub fn replies(&self, video_id: &str, parent: &Comment) -> Result<CommentPage> {
        let Some(token) = parent.replies_token.as_ref().filter(|t| !t.is_empty()) else {
            return Ok(CommentPage { source: parent.source, ..Default::default() });
        };
        match parent.source {
            CommentSource::InnerTube => self.inner_tube.comments(token, true),
            CommentSource::Invidious => self.invidious.comments(video_id, Some(token), true),
        }
    }
}

/// YouTube playlists without an account: InnerTube first, Invidious when it fails.
#[derive(Clone)]
pub struct Playlists {
    http: Http,
    inner_tube: InnerTube,
    invidious: Invidious,
}

const INVIDIOUS_TOKEN: &str = "inv:";

impl Playlists {
    pub fn new(http: Http, inner_tube: InnerTube, invidious: Invidious) -> Self {
        Self { http, inner_tube, invidious }
    }

    pub fn page(&self, playlist_id: &str, token: Option<&str>) -> Result<PlaylistPage> {
        if let Some(n) = token.and_then(|t| t.strip_prefix(INVIDIOUS_TOKEN)) {
            return self.invidious_page(playlist_id, n.parse().unwrap_or(1));
        }
        match self.inner_tube.playlist(playlist_id, token) {
            Ok(page) => Ok(page),
            Err(e) if token.is_none() => {
                log::info!("InnerTube playlist {playlist_id} failed ({e}); trying Invidious.");
                self.invidious_page(playlist_id, 1).or(Err(e))
            }
            Err(e) => Err(e),
        }
    }

    fn invidious_page(&self, playlist_id: &str, page: usize) -> Result<PlaylistPage> {
        let mut p = self.invidious.playlist(playlist_id, page)?;
        p.next_token = p.next_token.map(|n| format!("{INVIDIOUS_TOKEN}{n}"));
        Ok(p)
    }

    /// Every video of a playlist, up to `max`.
    pub fn all(&self, playlist_id: &str, max: usize, cancel: &Cancel) -> Result<PlaylistPage> {
        let mut first = self.page(playlist_id, None)?;
        let mut seen: HashSet<String> = first.videos.iter().map(|v| v.id.clone()).collect();
        let mut token = first.next_token.take();
        while let Some(t) = token {
            if first.videos.len() >= max || cancel.is_canceled() {
                break;
            }
            match self.page(playlist_id, Some(&t)) {
                Ok(next) => {
                    let before = first.videos.len();
                    first.videos.extend(next.videos.into_iter().filter(|v| seen.insert(v.id.clone())));
                    token = if first.videos.len() > before { next.next_token } else { None };
                }
                Err(e) => {
                    log::warn!("Playlist {playlist_id} stopped after {} videos: {e}", first.videos.len());
                    break;
                }
            }
        }
        first.videos.truncate(max);
        Ok(first)
    }

    pub fn search(&self, query: &str, token: Option<&str>) -> Result<Page<SearchResult>> {
        self.inner_tube.search_playlists(query, token)
    }

    /// Titles and channels for bare video IDs (Takeout gives only IDs), from YouTube's oEmbed.
    pub fn describe(&self, ids: &[String], progress: &(dyn Fn(usize) + Sync)) -> Vec<SearchResult> {
        let done = std::sync::atomic::AtomicUsize::new(0);
        parallel_map(ids, 8, |id| {
            let found = self.describe_one(id).unwrap_or_else(|| crate::imports::placeholder_video(id));
            progress(done.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1);
            found
        })
    }

    fn describe_one(&self, id: &str) -> Option<SearchResult> {
        let url = format!("https://www.youtube.com/oembed?format=json&url={}", escape(&crate::links::watch_url(id)));
        let json = self.http.get_json(&url, Duration::from_secs(15)).ok()?;
        let title = json.get("title").and_then(Value::as_str).filter(|t| !t.trim().is_empty())?;
        Some(SearchResult::video(id, title, json.get("author_name").and_then(Value::as_str).unwrap_or("")))
    }
}

/// "For You" without an account: what you watch, like and subscribe to are the seeds; each seed's Up
/// next is pooled and scored by how often and how high a video shows up. Watched, liked and Not
/// interested videos are left out, and no channel gets more than two places.
#[derive(Clone)]
pub struct Recommendations {
    library: Library,
    watch_page: WatchPage,
    discovery: Discovery,
    cache: Arc<Mutex<Option<RecommendationCache>>>,
    building: Arc<Mutex<()>>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct RecommendationCache {
    #[serde(with = "crate::models::dateoffset")]
    pub fetched_utc: DateTime<Utc>,
    /// False when there was nothing to go on and these are trending videos.
    pub is_personal: bool,
    pub videos: Vec<SearchResult>,
}

const CACHE_HOURS: i64 = 3;
const MAX_RESULTS: usize = 60;

impl Recommendations {
    pub fn new(library: Library, watch_page: WatchPage, discovery: Discovery) -> Self {
        Self { library, watch_page, discovery, cache: Arc::default(), building: Arc::default() }
    }

    fn cache_file(&self) -> std::path::PathBuf {
        self.library.folder().join("recommendations.json")
    }

    fn load(&self) -> Option<RecommendationCache> {
        let text = std::fs::read_to_string(self.cache_file()).ok()?;
        serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()
    }

    fn save(&self, cache: &RecommendationCache) {
        if let Ok(json) = serde_json::to_vec_pretty(cache) {
            let _ = crate::paths::write_atomic(&self.cache_file(), &json);
        }
    }

    pub fn is_cache_fresh(&self) -> bool {
        let mut cache = self.cache.lock().unwrap();
        if cache.is_none() {
            *cache = self.load();
        }
        cache.as_ref().is_some_and(|c| !c.videos.is_empty() && Utc::now() - c.fetched_utc < ChronoDuration::hours(CACHE_HOURS))
    }

    /// The cached list, or a fresh one when it's old or `refresh`.
    pub fn get(&self, region: &str, hide_shorts: bool, refresh: bool, progress: &(dyn Fn(usize) + Sync)) -> RecommendationCache {
        let _one_at_a_time = self.building.lock().unwrap();
        {
            let mut cache = self.cache.lock().unwrap();
            if cache.is_none() {
                *cache = self.load();
            }
            if let Some(c) = cache.as_ref().filter(|c| !refresh && !c.videos.is_empty() && Utc::now() - c.fetched_utc < ChronoDuration::hours(CACHE_HOURS)) {
                return RecommendationCache { videos: self.filter(c.videos.clone(), hide_shorts), ..c.clone() };
            }
        }
        let mut fresh = self.build(region, hide_shorts, progress);
        // Anything marked Not interested while this was built stays out.
        fresh.videos = self.filter(fresh.videos, hide_shorts);
        self.save(&fresh);
        *self.cache.lock().unwrap() = Some(fresh.clone());
        fresh
    }

    /// Drops videos from the cached list (Not interested, or watched).
    pub fn forget(&self, predicate: impl Fn(&SearchResult) -> bool) {
        let mut cache = self.cache.lock().unwrap();
        if cache.is_none() {
            *cache = self.load();
        }
        if let Some(c) = cache.as_mut() {
            let before = c.videos.len();
            c.videos.retain(|v| !predicate(v));
            if c.videos.len() != before {
                self.save(c);
            }
        }
    }

    fn filter(&self, videos: Vec<SearchResult>, hide_shorts: bool) -> Vec<SearchResult> {
        videos.into_iter().filter(|v| v.is_video() && !(hide_shorts && v.is_short) && !self.library.is_hidden(v)).collect()
    }

    fn build(&self, region: &str, hide_shorts: bool, progress: &(dyn Fn(usize) + Sync)) -> RecommendationCache {
        let mut seeds = self.pick_seeds();
        let subscriptions = self.library.subscriptions();
        let subscribed_ids: HashSet<String> = subscriptions.iter().map(|s| s.channel_id.clone()).collect();
        let subscribed_names: HashSet<String> = subscriptions.iter().map(|s| s.name.to_lowercase()).collect();
        if !subscribed_ids.is_empty() && !seeds.iter().any(|s| s.2) {
            let mut channels = subscriptions.clone();
            shuffle(&mut channels);
            channels.truncate(4);
            for uploads in parallel_map(&channels, 4, |c| self.discovery.channel_uploads(&c.channel_id)).into_iter().flatten() {
                if let Some(v) = uploads.videos.first() {
                    seeds.push((v.id.clone(), 1.0, true));
                }
            }
        }
        if seeds.is_empty() {
            return self.trending(region, hide_shorts);
        }
        let done = std::sync::atomic::AtomicUsize::new(0);
        let pages = parallel_map(&seeds, 4, |(id, weight, _)| {
            let r = self.watch_page.watch_next(id).map(|n| (n.related, *weight));
            progress(done.fetch_add(1, std::sync::atomic::Ordering::SeqCst) * 100 / seeds.len().max(1));
            r
        });
        let mut scores: HashMap<String, (SearchResult, f64)> = HashMap::new();
        for (related, weight) in pages.into_iter().flatten() {
            for (rank, video) in related.into_iter().filter(SearchResult::is_video).enumerate() {
                // Higher in Up next means closer to the seed.
                let points = weight / (1.0 + rank as f64 * 0.15);
                scores.entry(video.id.clone()).and_modify(|e| e.1 += points).or_insert((video, points));
            }
        }
        // Unwatched uploads from subscriptions belong here too.
        if let Some(feed) = self.library.cached_feed() {
            let cutoff = Utc::now() - ChronoDuration::days(7);
            for v in feed.videos.into_iter().filter(|v| v.published_at.is_none_or(|p| p >= cutoff)).take(30) {
                scores.entry(v.id.clone()).or_insert((v, 0.9));
            }
        }
        let history = self.library.history();
        let liked = self.library.liked_videos();
        let mut excluded: HashSet<String> = history.iter().map(|h| h.video_id.clone()).collect();
        excluded.extend(liked.iter().map(|v| v.video_id.clone()));
        excluded.extend(seeds.iter().map(|s| s.0.clone()));
        let familiar: HashSet<String> = history
            .iter()
            .map(|h| h.channel_title.to_lowercase())
            .chain(liked.iter().map(|v| v.channel_title.to_lowercase()))
            .filter(|n| !n.is_empty())
            .collect();
        let mut ranked: Vec<(SearchResult, f64)> = scores
            .into_values()
            .filter(|(v, _)| !excluded.contains(&v.id))
            .map(|(v, score)| {
                let channel = v.channel_title.to_lowercase();
                let boost = if v.channel_id.as_ref().is_some_and(|c| subscribed_ids.contains(c)) || subscribed_names.contains(&channel) {
                    1.3
                } else if familiar.contains(&channel) {
                    1.15
                } else {
                    1.0
                };
                // A little randomness so a refresh shows something new.
                let s = score * boost * (0.85 + random_unit() * 0.3);
                (v, s)
            })
            .collect();
        ranked.sort_by(|a, b| b.1.total_cmp(&a.1));
        let filtered = self.filter(ranked.into_iter().map(|(v, _)| v).collect(), hide_shorts);
        let videos = diversify(filtered).into_iter().take(MAX_RESULTS).collect::<Vec<_>>();
        log::info!("Recommendations: {} videos from {} seeds.", videos.len(), seeds.len());
        if videos.is_empty() {
            return self.trending(region, hide_shorts);
        }
        RecommendationCache { fetched_utc: Utc::now(), is_personal: true, videos }
    }

    /// (video, weight, from a subscription)
    fn pick_seeds(&self) -> Vec<(String, f64, bool)> {
        let mut seeds: Vec<(String, f64, bool)> = Vec::new();
        let mut recent: Vec<_> = self.library.history().into_iter().take(25).collect();
        // Finished or rewatched videos say more than ones left after a minute.
        let mut scored: Vec<(f64, crate::library::HistoryEntry)> = recent
            .drain(..)
            .map(|h| ((if h.is_finished() { 2.0 } else { 1.0 }) + (h.watch_count.min(3) as f64) * 0.5 + random_unit(), h))
            .collect();
        scored.sort_by(|a, b| b.0.total_cmp(&a.0));
        for (_, h) in scored.into_iter().take(6) {
            let weight = if h.is_finished() || h.watch_count > 1 { 1.2 } else { 0.8 };
            seeds.push((h.video_id, weight, false));
        }
        let mut likes: Vec<_> = self.library.liked_videos().into_iter().take(30).collect();
        shuffle(&mut likes);
        for like in likes.into_iter().take(4) {
            if !seeds.iter().any(|s| s.0 == like.video_id) {
                seeds.push((like.video_id, 1.5, false));
            }
        }
        if let Some(feed) = self.library.cached_feed() {
            let mut per_channel: Vec<SearchResult> = Vec::new();
            let mut seen = HashSet::new();
            for v in feed.videos {
                if seen.insert(v.channel_id.clone().unwrap_or_else(|| v.channel_title.clone())) {
                    per_channel.push(v);
                }
            }
            shuffle(&mut per_channel);
            for upload in per_channel.into_iter().take(4) {
                if !seeds.iter().any(|s| s.0 == upload.id) {
                    seeds.push((upload.id, 1.0, true));
                }
            }
        }
        seeds
    }

    fn trending(&self, region: &str, hide_shorts: bool) -> RecommendationCache {
        let videos = self.discovery.featured(FeaturedCategory::Trending, region, None).map(|p| p.items).unwrap_or_default();
        let mut videos = self.filter(videos, hide_shorts);
        videos.truncate(MAX_RESULTS);
        RecommendationCache { fetched_utc: Utc::now(), is_personal: false, videos }
    }
}

/// At most two per channel, so the list stays varied.
pub fn diversify(ranked: Vec<SearchResult>) -> Vec<SearchResult> {
    let mut per_channel: HashMap<String, usize> = HashMap::new();
    ranked
        .into_iter()
        .filter(|v| {
            let key = v.channel_id.clone().unwrap_or_else(|| v.channel_title.to_lowercase());
            let count = per_channel.entry(key).or_insert(0);
            *count += 1;
            *count <= 2
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_a_channel_feed() {
        let feed = r#"<feed xmlns:yt="http://www.youtube.com/xml/schemas/2015" xmlns:media="http://search.yahoo.com/mrss/" xmlns="http://www.w3.org/2005/Atom">
            <title>Chan</title><author><name>Chan</name></author>
            <entry><yt:videoId>dQw4w9WgXcQ</yt:videoId><yt:channelId>UC1</yt:channelId><title>Song</title>
            <link rel="alternate" href="https://www.youtube.com/shorts/dQw4w9WgXcQ"/><published>2026-11-10T11:00:39+00:00</published>
            <media:group><media:community><media:statistics views="7691"/></media:community></media:group></entry></feed>"#;
        let uploads = parse_channel_feed(feed, "UC1").unwrap();
        assert_eq!(uploads.channel_name, "Chan");
        let v = &uploads.videos[0];
        assert!(v.is_short);
        assert_eq!(v.view_count, Some(7691));
        assert!(v.published_at.is_some());
    }

    #[test]
    fn at_most_two_per_channel() {
        let v = |id: &str, c: &str| SearchResult { channel_id: Some(c.into()), ..SearchResult::video(id, id, c) };
        let kept = diversify(vec![v("a", "x"), v("b", "x"), v("c", "x"), v("d", "y")]);
        assert_eq!(kept.iter().map(|v| v.id.as_str()).collect::<Vec<_>>(), ["a", "b", "d"]);
    }

    #[test]
    fn parallel_map_keeps_order() {
        let items: Vec<usize> = (0..20).collect();
        assert_eq!(parallel_map(&items, 3, |i| i * 2), items.iter().map(|i| i * 2).collect::<Vec<_>>());
    }
}
