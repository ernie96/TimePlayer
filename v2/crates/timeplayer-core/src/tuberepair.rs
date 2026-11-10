//! TubeRepair, the old YouTube API that legacy iOS YouTube apps use, as served by
//! tuberepair.uptimetrackers.com (1.x's `TubeRepairBackendProvider` and `TubeRepairFeedParser`):
//! search, details, a playable stream through its /getvideo redirect, related videos and comments.

use regex::Regex;
use std::sync::OnceLock;
use std::time::Duration;

use crate::config::Settings;
use crate::error::{AppError, Result};
use crate::http::{Http, Request, escape};
use crate::models::{ChannelInfo, Page, ResolvedStream, SearchResult, SourceKind, VideoDetails};
use crate::xml::{self, Element};

const USER_AGENT: &str = "TimePlayer/1.0 (Windows NT 10.0; Win64; x64)";
const PAGE_SIZE: usize = 20;

#[derive(Clone)]
pub struct TubeRepair {
    http: Http,
    settings: Settings,
}

impl TubeRepair {
    pub fn new(http: Http, settings: Settings) -> Self {
        Self { http, settings }
    }

    fn base_url(&self) -> String {
        let url = self.settings.with(|c| c.backend.tube_repair_base_url.clone());
        let url = if url.trim().is_empty() { "https://tuberepair.uptimetrackers.com/".to_owned() } else { url };
        format!("{}/", url.trim_end_matches('/'))
    }

    fn timeout(&self) -> Duration {
        Duration::from_secs(self.settings.with(|c| c.backend.tube_repair_timeout_seconds).max(5))
    }

    fn feed(&self, url: &str) -> Result<Vec<SearchResult>> {
        let response = self.http.send(
            Request::get(url)
                .timeout(self.timeout())
                .header("User-Agent", USER_AGENT)
                .header("Accept", "application/atom+xml, application/xml, text/xml"),
        )?;
        if !response.ok() {
            return Err(AppError::new("TUBEREPAIR_HTTP_ERROR", format!("TubeRepair answered {}.", response.status)));
        }
        Ok(parse_video_feed(&response.text()))
    }

    /// Pages are numbered; the token is the next page number.
    pub fn search(&self, query: &str, page_token: Option<&str>) -> Result<Page<SearchResult>> {
        if query.trim().is_empty() {
            return Err(AppError::new("EMPTY_QUERY", "Search query cannot be empty."));
        }
        let page: usize = page_token.and_then(|t| t.parse().ok()).filter(|p| *p > 0).unwrap_or(1);
        let start = (page - 1) * PAGE_SIZE + 1;
        let items = self.feed(&format!("{}feeds/api/videos?q={}&start-index={start}&max-results={PAGE_SIZE}", self.base_url(), escape(query)))?;
        let next = (items.len() >= PAGE_SIZE).then(|| (page + 1).to_string());
        Ok(Page { items, next_token: next })
    }

    pub fn related(&self, video_id: &str) -> Result<Vec<SearchResult>> {
        self.feed(&format!("{}feeds/api/videos/{}/related", self.base_url(), escape(video_id)))
    }

    pub fn details(&self, video_id: &str) -> Result<VideoDetails> {
        let found = self.search(video_id, None).ok().and_then(|p| {
            let exact = p.items.iter().find(|i| i.id == video_id).cloned();
            exact.or_else(|| p.items.into_iter().next())
        });
        let found = found.or_else(|| self.related(video_id).ok().and_then(|r| r.into_iter().next()));
        let Some(m) = found else {
            return Err(AppError::new("VIDEO_DETAILS_UNAVAILABLE", format!("Video details not found on TubeRepair for {video_id}.")));
        };
        Ok(VideoDetails {
            id: video_id.into(),
            title: m.title.clone(),
            description: m.description_snippet.clone(),
            duration: m.duration.unwrap_or_default(),
            view_count: m.view_count,
            channel: ChannelInfo { id: m.channel_id.clone().unwrap_or_default(), name: m.channel_title.clone() },
            thumbnail_url: m.thumbnail_url.clone(),
            ..Default::default()
        })
    }

    /// /getvideo/{id} redirects to a playable progressive file.
    pub fn resolve(&self, video_id: &str) -> Result<ResolvedStream> {
        let url = format!("{}getvideo/{video_id}", self.base_url());
        let response = self.http.send(Request::get(&url).timeout(self.timeout()).header("User-Agent", USER_AGENT).redirect_only())?;
        let target = match response.status {
            301 | 302 | 307 | 308 => response.headers.get("location").map(|l| absolute(&url, l)),
            200..=299 => Some(url.clone()),
            _ => None,
        };
        let Some(target) = target.filter(|t| !t.trim().is_empty()) else {
            return Err(AppError::new(
                "STREAM_UNAVAILABLE",
                format!("TubeRepair did not return a playable redirect stream (Status: {}).", response.status),
            ));
        };
        log::info!("TubeRepair resolved {video_id} to {}.", crate::error::redact_url(&target));
        Ok(ResolvedStream { video_id: video_id.into(), url: target, source: SourceKind::TubeRepair, ..Default::default() })
    }

    /// Comments as "author: text".
    pub fn comments(&self, video_id: &str) -> Result<Vec<String>> {
        let response = self.http.send(
            Request::get(format!("{}feeds/api/videos/{}/comments", self.base_url(), escape(video_id)))
                .timeout(self.timeout())
                .header("User-Agent", USER_AGENT),
        )?;
        let Some(root) = xml::parse(&sanitize(&response.text())) else { return Ok(Vec::new()) };
        let mut entries = Vec::new();
        root.descendants("entry", &mut entries);
        Ok(entries
            .into_iter()
            .filter_map(|e| {
                let content = e.child_text("content")?;
                Some(match e.child("author").and_then(|a| a.child_text("name")) {
                    Some(author) => format!("{author}: {content}"),
                    None => content,
                })
            })
            .collect())
    }
}

fn absolute(base: &str, location: &str) -> String {
    if location.starts_with("http://") || location.starts_with("https://") {
        location.to_owned()
    } else if let Some(rest) = location.strip_prefix('/') {
        let origin: String = base.splitn(4, '/').take(3).collect::<Vec<_>>().join("/");
        format!("{origin}/{rest}")
    } else {
        format!("{}/{location}", base.rsplit_once('/').map(|(a, _)| a).unwrap_or(base))
    }
}

/// Unescaped ampersands, doubled type attributes and a missing </feed>, which TubeRepair sometimes sends.
pub fn sanitize(xml: &str) -> String {
    static AMP: OnceLock<Regex> = OnceLock::new();
    static TYPE: OnceLock<Regex> = OnceLock::new();
    let amp = AMP.get_or_init(|| Regex::new(r"&(amp;|lt;|gt;|quot;|apos;|#\d+;|#x[0-9a-fA-F]+;)?").unwrap());
    let mut out = amp
        .replace_all(xml, |c: &regex::Captures| if c.get(1).is_some() { c[0].to_owned() } else { "&amp;".to_owned() })
        .into_owned();
    let doubled = TYPE.get_or_init(|| Regex::new(r#"(?i)(type=('[^']*'|"[^"]*"))\s+type=('[^']*'|"[^"]*")"#).unwrap());
    out = doubled.replace_all(&out, "$1").into_owned();
    if out.to_lowercase().contains("<feed") && !out.to_lowercase().contains("</feed>") {
        out = format!("{}\n</feed>", out.trim_end());
    }
    out
}

/// Every video entry in a GData-style Atom feed; entries are read one by one if the whole feed won't parse.
pub fn parse_video_feed(text: &str) -> Vec<SearchResult> {
    if text.trim().is_empty() {
        return Vec::new();
    }
    if let Some(root) = xml::parse(&sanitize(text)) {
        let entries: Vec<SearchResult> = root.children_named("entry").filter_map(parse_entry).collect();
        if !entries.is_empty() || root.name.eq_ignore_ascii_case("feed") {
            return entries;
        }
    }
    static ENTRY: OnceLock<Regex> = OnceLock::new();
    ENTRY
        .get_or_init(|| Regex::new(r"(?s)<entry\b.*?</entry>").unwrap())
        .find_iter(text)
        .filter_map(|m| xml::parse(&sanitize(m.as_str())))
        .filter_map(|e| parse_entry(&e))
        .collect()
}

fn parse_entry(entry: &Element) -> Option<SearchResult> {
    let group = entry.child("group");
    let mut id = entry.child_text("videoid").or_else(|| group.and_then(|g| g.child_text("videoid")));
    if id.is_none() {
        id = entry.child_text("id").and_then(|v| v.rsplit(':').next().map(str::trim).filter(|s| !s.is_empty()).map(str::to_owned));
    }
    if id.is_none() {
        id = group.and_then(|g| {
            g.children_named("content")
                .filter_map(|c| c.attr("url"))
                .filter(|u| u.contains("/getvideo/"))
                .find_map(|u| u.rsplit('/').next().filter(|c| c.len() == 11).map(str::to_owned))
        });
    }
    let id = id.filter(|i| !i.trim().is_empty())?;
    let title = group.and_then(|g| g.child_text("title")).or_else(|| entry.child_text("title")).unwrap_or_else(|| "Untitled Video".into());
    let author = entry.child("author");
    let channel = author
        .and_then(|a| a.child_text("name"))
        .or_else(|| group.and_then(|g| g.child_text("credit")))
        .unwrap_or_else(|| "Unknown Channel".into());
    let channel_id = author
        .and_then(|a| a.child_text("userId"))
        .or_else(|| entry.child_text("channelId"))
        .or_else(|| group.and_then(|g| g.child_text("uploaderId")))
        .or_else(|| group.and_then(|g| g.child_text("credit")));
    let duration = group
        .and_then(|g| g.child("duration"))
        .or_else(|| entry.child("duration"))
        .and_then(|d| d.attr("seconds")?.parse::<u64>().ok())
        .filter(|s| *s > 0)
        .map(Duration::from_secs);
    let views = entry
        .child("statistics")
        .or_else(|| group.and_then(|g| g.child("statistics")))
        .and_then(|s| s.attr("viewCount")?.parse::<i64>().ok());
    let published = entry
        .child_text("published")
        .or_else(|| group.and_then(|g| g.child_text("uploaded")))
        .and_then(|p| chrono::DateTime::parse_from_rfc3339(&p).ok())
        .map(|d| d.format("%Y-%m-%d").to_string());
    let thumbnail = group
        .and_then(|g| {
            let thumbs: Vec<&Element> = g.children_named("thumbnail").collect();
            thumbs
                .iter()
                .find(|t| t.attr("name") == Some("hqdefault"))
                .or_else(|| thumbs.iter().find(|t| t.attr("name") == Some("mqdefault")))
                .or_else(|| thumbs.first())
                .and_then(|t| t.attr("url"))
                .map(str::to_owned)
        })
        .filter(|u| !u.trim().is_empty())
        .unwrap_or_else(|| format!("https://i.ytimg.com/vi/{id}/hqdefault.jpg"));
    let description = group.and_then(|g| g.child_text("description")).or_else(|| entry.child_text("content"));
    Some(SearchResult {
        channel_id,
        duration,
        view_count: views,
        upload_date_text: published,
        thumbnail_url: Some(thumbnail),
        description_snippet: description,
        ..SearchResult::video(&id, &title, &channel)
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_a_gdata_feed_even_when_broken() {
        let feed = "<feed xmlns='http://www.w3.org/2005/Atom' xmlns:yt='x' xmlns:media='y'>\
            <entry><id>tag:youtube.com,2008:video:dQw4w9WgXcQ</id><title>Rick & Roll</title>\
            <author><name>Rick</name><yt:userId>UCuAXFkgsw1L7xaCfnd5JJOw</yt:userId></author>\
            <media:group><yt:duration seconds='213'/><media:thumbnail url='u' name='hqdefault'/></media:group>\
            <yt:statistics viewCount='42'/></entry>";
        let items = parse_video_feed(feed);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].id, "dQw4w9WgXcQ");
        assert_eq!(items[0].title, "Rick & Roll");
        assert_eq!(items[0].duration, Some(Duration::from_secs(213)));
        assert_eq!(items[0].view_count, Some(42));
        assert_eq!(items[0].channel_id.as_deref(), Some("UCuAXFkgsw1L7xaCfnd5JJOw"));
    }

    #[test]
    fn redirects_become_absolute() {
        assert_eq!(absolute("https://a.b/getvideo/x", "/stream/y"), "https://a.b/stream/y");
        assert_eq!(absolute("https://a.b/getvideo/x", "https://c.d/e"), "https://c.d/e");
    }
}
