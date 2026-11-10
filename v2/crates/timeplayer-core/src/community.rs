//! SponsorBlock, Return YouTube Dislike, lyrics from LRCLIB, transcripts from captions, problem reports and
//! feature requests through TimePlayer's relay, and the run counter (1.x's Community folder).

use regex::Regex;
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::sync::OnceLock;
use std::time::Duration;

use crate::config::{SegmentAction, Settings, SponsorBlockConfig};
use crate::error::{AppError, Result};
use crate::http::{Http, Request, escape};
use crate::text::time_spoken_secs;
use crate::{plural_t, t};

macro_rules! re {
    ($pattern:expr) => {{
        static RE: OnceLock<Regex> = OnceLock::new();
        RE.get_or_init(|| Regex::new($pattern).unwrap())
    }};
}

// ----- SponsorBlock -----

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SegmentCategory {
    Sponsor,
    SelfPromotion,
    Interaction,
    Intro,
    Outro,
    Preview,
    Filler,
    NonMusic,
    Highlight,
    ExclusiveAccess,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SegmentKind {
    /// A stretch to skip.
    Skip,
    /// A label for the whole video ("sponsored", "exclusive access").
    WholeVideo,
    /// A single point: the highlight.
    Highlight,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Segment {
    pub start: f64,
    pub end: f64,
    pub category: SegmentCategory,
    pub kind: SegmentKind,
    pub uuid: String,
}

impl Segment {
    pub fn length(&self) -> f64 {
        (self.end - self.start).max(0.0)
    }

    /// "sponsor", "self-promotion", ...
    pub fn name(&self) -> String {
        t!(match self.category {
            SegmentCategory::Sponsor => "sponsor",
            SegmentCategory::SelfPromotion => "self-promotion",
            SegmentCategory::Interaction => "reminder to like or subscribe",
            SegmentCategory::Intro => "intro",
            SegmentCategory::Outro => "outro",
            SegmentCategory::Preview => "preview",
            SegmentCategory::Filler => "filler",
            SegmentCategory::NonMusic => "non-music part",
            SegmentCategory::Highlight => "highlight",
            SegmentCategory::ExclusiveAccess => "exclusive access",
        })
    }
}

/// Segments through the private endpoint: only the first four characters of the SHA-256 of the video ID
/// go out, and the video is picked out of the answer here.
pub fn sponsor_segments(http: &Http, settings: &Settings, video_id: &str) -> Vec<Segment> {
    let config = settings.with(|c| c.sponsor_block.clone());
    if !config.enabled || video_id.trim().is_empty() {
        return Vec::new();
    }
    let prefix: String = hex::encode(Sha256::digest(video_id.as_bytes())).chars().take(4).collect();
    let categories = r#"["sponsor","selfpromo","interaction","intro","outro","preview","filler","music_offtopic","poi_highlight","exclusive_access"]"#;
    let actions = r#"["skip","mute","full","poi"]"#;
    let url = format!(
        "{}/api/skipSegments/{prefix}?categories={}&actionTypes={}",
        config.server_url.trim_end_matches('/'),
        escape(categories),
        escape(actions)
    );
    match http.send(Request::get(url).timeout(Duration::from_secs(10))) {
        Ok(r) if r.ok() => parse_segments(&r.text(), video_id),
        Ok(r) => {
            if r.status != 404 {
                log::info!("SponsorBlock answered {}.", r.status);
            }
            Vec::new()
        }
        Err(e) => {
            log::info!("SponsorBlock lookup failed: {e}");
            Vec::new()
        }
    }
}

pub fn parse_segments(json: &str, video_id: &str) -> Vec<Segment> {
    let Ok(Value::Array(videos)) = serde_json::from_str::<Value>(json) else { return Vec::new() };
    let mut out: Vec<Segment> = videos
        .iter()
        .filter(|v| v.get("videoID").and_then(Value::as_str) == Some(video_id))
        .filter_map(|v| v.get("segments").and_then(Value::as_array))
        .flatten()
        .filter_map(|s| {
            let range = s.get("segment")?.as_array()?;
            let (start, end) = (range.first()?.as_f64()?, range.get(1)?.as_f64()?);
            let category = match s.get("category")?.as_str()? {
                "sponsor" => SegmentCategory::Sponsor,
                "selfpromo" => SegmentCategory::SelfPromotion,
                "interaction" => SegmentCategory::Interaction,
                "intro" => SegmentCategory::Intro,
                "outro" => SegmentCategory::Outro,
                "preview" => SegmentCategory::Preview,
                "filler" => SegmentCategory::Filler,
                "music_offtopic" => SegmentCategory::NonMusic,
                "poi_highlight" => SegmentCategory::Highlight,
                "exclusive_access" => SegmentCategory::ExclusiveAccess,
                _ => return None,
            };
            let kind = match s.get("actionType").and_then(Value::as_str).unwrap_or("skip") {
                "full" => SegmentKind::WholeVideo,
                "poi" => SegmentKind::Highlight,
                _ => SegmentKind::Skip,
            };
            Some(Segment { start, end, category, kind, uuid: s.get("UUID").and_then(Value::as_str).unwrap_or("").to_owned() })
        })
        .collect();
    out.sort_by(|a, b| a.start.total_cmp(&b.start));
    out
}

#[derive(Debug, Clone, PartialEq)]
pub enum SegmentEvent {
    Skip(Segment),
    Announce(Segment),
    WholeVideo(Segment),
    HighlightAvailable(Segment),
}

/// Decides, as playback moves, when to skip or announce a segment. Each is handled once per video:
/// seeking back into one that was skipped plays it, which is what someone seeking there wants.
#[derive(Default)]
pub struct SegmentTracker {
    pub video_id: Option<String>,
    segments: Vec<Segment>,
    handled: HashSet<String>,
    labels_reported: bool,
}

/// Segments ending this close to the position are left alone; skipping would gain nothing.
const END_MARGIN: f64 = 0.5;

pub fn action_for(settings: &SponsorBlockConfig, category: SegmentCategory) -> SegmentAction {
    match category {
        SegmentCategory::Sponsor => settings.sponsor,
        SegmentCategory::SelfPromotion => settings.self_promotion,
        SegmentCategory::Interaction => settings.interaction,
        SegmentCategory::Intro => settings.intro,
        SegmentCategory::Outro => settings.outro,
        SegmentCategory::Preview => settings.preview,
        SegmentCategory::Filler => settings.filler,
        SegmentCategory::NonMusic => settings.non_music,
        _ => SegmentAction::Off,
    }
}

impl SegmentTracker {
    pub fn reset(&mut self, video_id: Option<&str>, segments: Vec<Segment>) {
        self.video_id = video_id.map(str::to_owned);
        self.segments = segments;
        self.handled.clear();
        self.labels_reported = false;
    }

    /// The same video again: every segment is handled afresh.
    pub fn rewind(&mut self) {
        self.handled.clear();
        self.labels_reported = false;
    }

    pub fn segments(&self) -> &[Segment] {
        &self.segments
    }

    fn key(s: &Segment) -> String {
        format!("{}|{}|{}", s.uuid, s.start, s.end)
    }

    pub fn update(&mut self, position: f64, settings: &SponsorBlockConfig) -> Vec<SegmentEvent> {
        let mut events = Vec::new();
        if !settings.enabled || self.segments.is_empty() {
            return events;
        }
        if !self.labels_reported {
            self.labels_reported = true;
            if settings.announce_whole_video_labels {
                let mut seen = HashSet::new();
                for s in self.segments.iter().filter(|s| s.kind == SegmentKind::WholeVideo && seen.insert(s.category)) {
                    events.push(SegmentEvent::WholeVideo(s.clone()));
                }
            }
            if settings.announce_highlight
                && let Some(h) = self.segments.iter().find(|s| s.kind == SegmentKind::Highlight) {
                    events.push(SegmentEvent::HighlightAvailable(h.clone()));
                }
        }
        for s in &self.segments {
            if s.kind != SegmentKind::Skip || self.handled.contains(&Self::key(s)) || position < s.start || position >= s.end - END_MARGIN {
                continue;
            }
            let action = action_for(settings, s.category);
            if action == SegmentAction::Off {
                continue;
            }
            self.handled.insert(Self::key(s));
            events.push(if action == SegmentAction::Skip { SegmentEvent::Skip(s.clone()) } else { SegmentEvent::Announce(s.clone()) });
            // One at a time; a skip moves the position anyway.
            break;
        }
        events
    }

    /// The segment playing now that isn't switched off (the S key).
    pub fn segment_at(&self, position: f64, settings: &SponsorBlockConfig) -> Option<&Segment> {
        self.segments.iter().find(|s| {
            s.kind == SegmentKind::Skip && position >= s.start && position < s.end - END_MARGIN && action_for(settings, s.category) != SegmentAction::Off
        })
    }

    pub fn highlight(&self) -> Option<&Segment> {
        self.segments.iter().find(|s| s.kind == SegmentKind::Highlight)
    }
}

/// "Skipped sponsor, 40 seconds".
pub fn skipped_text(segment: &Segment) -> String {
    t!("Skipped {0}, {1}", segment.name(), time_spoken_secs(segment.length()))
}

// ----- Return YouTube Dislike -----

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct Votes {
    pub likes: i64,
    pub dislikes: i64,
}

impl Votes {
    /// "12,345 likes, 678 dislikes".
    pub fn spoken(&self) -> String {
        format!("{}, {}", plural_t!(self.likes, "{0:N0} like", "{0:N0} likes"), plural_t!(self.dislikes, "{0:N0} dislike", "{0:N0} dislikes"))
    }
}

/// Estimated likes and dislikes; only the video ID is sent.
pub fn votes(http: &Http, settings: &Settings, video_id: &str) -> Option<Votes> {
    let config = settings.with(|c| c.dislikes.clone());
    if !config.enabled || video_id.trim().is_empty() {
        return None;
    }
    let url = format!("{}/votes?videoId={}", config.api_url.trim_end_matches('/'), escape(video_id));
    let json = http.get_json(&url, Duration::from_secs(10)).ok()?;
    Some(Votes { likes: json.get("likes")?.as_i64()?, dislikes: json.get("dislikes")?.as_i64()? })
}

// ----- Lyrics (LRCLIB) -----

#[derive(Debug, Clone, PartialEq)]
pub struct LyricLine {
    pub start: Option<f64>,
    pub text: String,
}

impl LyricLine {
    pub fn label(&self) -> String {
        match self.start {
            Some(s) => format!("{}, {}", self.text, time_spoken_secs(s)),
            None => self.text.clone(),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub struct SongLyrics {
    pub artist: String,
    pub track: String,
    pub lines: Vec<LyricLine>,
}

impl SongLyrics {
    pub fn is_synced(&self) -> bool {
        !self.lines.is_empty() && self.lines.iter().all(|l| l.start.is_some())
    }
}

pub fn find_lyrics(http: &Http, title: &str, channel: &str, duration: f64) -> Result<SongLyrics> {
    let (artist, track) = guess_song(title, channel);
    let seconds = duration.round() as i64;
    let get = |path: &str| -> Option<Value> {
        let r = http
            .send(Request::get(format!("https://lrclib.net/api/{path}")).timeout(Duration::from_secs(12)).header("User-Agent", "TimePlayer (https://github.com/ernie96/TimePlayer)"))
            .ok()?;
        r.ok().then(|| r.json().ok()).flatten()
    };
    let has_lyrics = |e: &Value| {
        e.get("instrumental") != Some(&Value::Bool(true))
            && ["syncedLyrics", "plainLyrics"].iter().any(|k| e.get(*k).and_then(Value::as_str).is_some_and(|s| !s.trim().is_empty()))
    };
    let mut query = format!("get?artist_name={}&track_name={}", escape(&artist), escape(&track));
    if seconds > 0 {
        query.push_str(&format!("&duration={seconds}"));
    }
    let mut entry = get(&query).filter(|e| has_lyrics(e));
    if entry.is_none() {
        let found = get(&format!("search?q={}", escape(&format!("{artist} {track}"))));
        let mut candidates: Vec<Value> = found.and_then(|v| v.as_array().cloned()).unwrap_or_default().into_iter().filter(|e| has_lyrics(e)).collect();
        let distance = |e: &Value| if seconds > 0 { e.get("duration").and_then(Value::as_f64).map(|d| (d - seconds as f64).abs()).unwrap_or(0.0) } else { 0.0 };
        candidates.sort_by(|a, b| {
            let synced = |e: &Value| u8::from(e.get("syncedLyrics").and_then(Value::as_str).is_none());
            synced(a).cmp(&synced(b)).then(distance(a).total_cmp(&distance(b)))
        });
        entry = candidates.into_iter().find(|e| seconds <= 0 || e.get("duration").and_then(Value::as_f64).is_none_or(|d| (d - seconds as f64).abs() <= 8.0));
    }
    let Some(entry) = entry else { return Err(AppError::new("NO_LYRICS", t!("No lyrics were found for this song."))) };
    let lines = match entry.get("syncedLyrics").and_then(Value::as_str).filter(|s| !s.is_empty()) {
        Some(synced) => parse_lrc(synced),
        None => parse_plain(entry.get("plainLyrics").and_then(Value::as_str).unwrap_or("")),
    };
    if lines.is_empty() {
        return Err(AppError::new("NO_LYRICS", t!("No lyrics were found for this song.")));
    }
    Ok(SongLyrics {
        artist: entry.get("artistName").and_then(Value::as_str).unwrap_or(&artist).to_owned(),
        track: entry.get("trackName").and_then(Value::as_str).unwrap_or(&track).to_owned(),
        lines,
    })
}

/// "Artist - Song (Official Video)" by "Artist - Topic" → ("Artist", "Song").
pub fn guess_song(title: &str, channel: &str) -> (String, String) {
    let noise = re!(r"(?i)\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|lyric video|visuali[sz]er|music video|hd|4k|remaster(ed)?|mv|clip)\b[^\)\]]*[\)\]]");
    let featuring = re!(r"(?i)\s*[\(\[]?\s*\b(ft\.?|feat\.?|featuring)\s+[^\)\]\-]+[\)\]]?");
    let clean_title = featuring.replace_all(&noise.replace_all(title, ""), "").trim().to_owned();
    let clean_channel = re!(r"(?i)\s*-\s*Topic$|VEVO$|\s*Official$").replace_all(channel, "").trim().to_owned();
    for separator in [" - ", " – ", " — ", " | "] {
        if let Some(at) = clean_title.find(separator).filter(|a| *a > 0) {
            return (clean_title[..at].trim().to_owned(), clean_title[at + separator.len()..].trim().trim_matches('"').to_owned());
        }
    }
    (clean_channel, clean_title.trim_matches('"').to_owned())
}

pub fn parse_lrc(lrc: &str) -> Vec<LyricLine> {
    let stamp = re!(r"\[(?P<m>\d{1,3}):(?P<s>\d{1,2}(?:[.,]\d{1,3})?)\]");
    let mut lines = Vec::new();
    for raw in lrc.replace("\r\n", "\n").split('\n') {
        let stamps: Vec<_> = stamp.captures_iter(raw).collect();
        if stamps.is_empty() {
            continue;
        }
        let text = stamp.replace_all(raw, "").trim().to_owned();
        if text.is_empty() {
            continue;
        }
        for s in stamps {
            let minutes: f64 = s["m"].parse().unwrap_or(0.0);
            let seconds: f64 = s["s"].replace(',', ".").parse().unwrap_or(0.0);
            lines.push(LyricLine { start: Some(minutes * 60.0 + seconds), text: text.clone() });
        }
    }
    lines.sort_by(|a, b| a.start.unwrap_or(0.0).total_cmp(&b.start.unwrap_or(0.0)));
    lines
}

fn parse_plain(text: &str) -> Vec<LyricLine> {
    text.replace("\r\n", "\n").split('\n').map(str::trim).filter(|l| !l.is_empty()).map(|l| LyricLine { start: None, text: l.to_owned() }).collect()
}

// ----- Transcripts -----

#[derive(Debug, Clone, PartialEq)]
pub struct TranscriptLine {
    pub start: f64,
    pub text: String,
}

impl TranscriptLine {
    pub fn label(&self) -> String {
        format!("{}, {}", self.text, time_spoken_secs(self.start))
    }
}

pub fn load_transcript(http: &Http, url: &str) -> Result<Vec<TranscriptLine>> {
    let text = http.get_text(url, Duration::from_secs(20)).map_err(|_| AppError::network(t!("The captions could not be loaded.")))?;
    let lines = parse_transcript(&text);
    if lines.is_empty() { Err(AppError::new("EMPTY_TRANSCRIPT", t!("These captions are empty."))) } else { Ok(lines) }
}

/// WebVTT, YouTube's json3 or its XML timed text.
pub fn parse_transcript(content: &str) -> Vec<TranscriptLine> {
    let trimmed = content.trim_start_matches(['\u{feff}', ' ', '\r', '\n', '\t']);
    let lines = if trimmed.starts_with("WEBVTT") {
        parse_vtt(trimmed)
    } else if trimmed.starts_with('{') {
        parse_json3(trimmed)
    } else if trimmed.starts_with('<') {
        parse_timed_text(trimmed)
    } else {
        Vec::new()
    };
    tidy(lines)
}

fn html_decode(s: &str) -> String {
    let mut out = s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&#39;", "'").replace("&nbsp;", " ");
    out = re!(r"&#(\d+);")
        .replace_all(&out, |c: &regex::Captures| c[1].parse::<u32>().ok().and_then(char::from_u32).map(String::from).unwrap_or_default())
        .into_owned();
    out
}

fn tidy(mut lines: Vec<TranscriptLine>) -> Vec<TranscriptLine> {
    lines.sort_by(|a, b| a.start.total_cmp(&b.start));
    let mut out: Vec<TranscriptLine> = Vec::new();
    for line in lines {
        let text = re!(r"\s+").replace_all(&html_decode(&line.text), " ").trim().to_owned();
        if text.is_empty() || out.last().is_some_and(|l| l.text == text) {
            continue;
        }
        out.push(TranscriptLine { start: line.start, text });
    }
    out
}

fn clock(text: &str) -> f64 {
    let parts: Vec<f64> = text.replace(',', ".").split(':').map(|p| p.parse().unwrap_or(0.0)).collect();
    match parts.as_slice() {
        [h, m, s] => h * 3600.0 + m * 60.0 + s,
        [m, s] => m * 60.0 + s,
        _ => 0.0,
    }
}

fn parse_vtt(vtt: &str) -> Vec<TranscriptLine> {
    let time = re!(r"^(?P<start>(?:\d+:)?\d{2}:\d{2}[.,]\d{3})\s+-->");
    let tags = re!(r"<[^>]+>");
    vtt.replace("\r\n", "\n")
        .split("\n\n")
        .filter_map(|block| {
            let rows: Vec<&str> = block.split('\n').collect();
            let at = rows.iter().position(|r| time.is_match(r))?;
            let start = clock(&time.captures(rows[at])?["start"]);
            let raw: Vec<&str> = rows[at + 1..].iter().copied().filter(|r| !r.trim().is_empty()).collect();
            let text: Vec<String> = raw.iter().map(|r| tags.replace_all(r, "").trim().to_owned()).collect();
            if text.is_empty() {
                return None;
            }
            // YouTube's rolling auto-captions repeat the previous line; the last row is the new one.
            let rolling = raw.iter().any(|r| r.contains("<c>"));
            Some(TranscriptLine { start, text: if rolling { text.last()?.clone() } else { text.join(" ") } })
        })
        .collect()
}

fn parse_json3(json: &str) -> Vec<TranscriptLine> {
    let Ok(v) = serde_json::from_str::<Value>(json) else { return Vec::new() };
    v.get("events")
        .and_then(Value::as_array)
        .map(|events| {
            events
                .iter()
                .filter_map(|e| {
                    let segs = e.get("segs")?.as_array()?;
                    let start = e.get("tStartMs")?.as_f64()? / 1000.0;
                    Some(TranscriptLine { start, text: segs.iter().filter_map(|s| s.get("utf8").and_then(Value::as_str)).collect() })
                })
                .collect()
        })
        .unwrap_or_default()
}

fn parse_timed_text(xml_text: &str) -> Vec<TranscriptLine> {
    let Some(root) = crate::xml::parse(xml_text) else { return Vec::new() };
    let mut out = Vec::new();
    let mut ps = Vec::new();
    root.descendants("p", &mut ps);
    for p in ps {
        if let Some(ms) = p.attr("t").and_then(|t| t.parse::<f64>().ok()) {
            let text = if p.text.trim().is_empty() { p.children.iter().map(|c| c.text.clone()).collect::<Vec<_>>().join("") } else { p.text.clone() };
            out.push(TranscriptLine { start: ms / 1000.0, text });
        }
    }
    let mut texts = Vec::new();
    root.descendants("text", &mut texts);
    for t in texts {
        if let Some(s) = t.attr("start").and_then(|v| v.parse::<f64>().ok()) {
            out.push(TranscriptLine { start: s, text: t.text.clone() });
        }
    }
    out
}

// ----- Problem reports and feature requests -----

pub const RELAY_URL: &str = "https://timeplayer-reports.ernie96.workers.dev/report";
pub const MAX_DESCRIPTION: usize = 5000;
pub const MAX_CONTACT: usize = 200;
pub const MAX_LOG: usize = 200_000;

#[derive(Debug, Clone)]
pub struct Report {
    /// "bug" or "feature".
    pub kind: &'static str,
    pub description: String,
    pub contact: Option<String>,
    pub log: Option<String>,
}

fn clip(text: &str, max: usize) -> String {
    text.chars().take(max).collect()
}

fn os_description() -> String {
    format!("{} {}", std::env::consts::OS, std::env::consts::ARCH)
}

/// The last two log files, with the user name taken out.
pub fn read_recent_log() -> String {
    let folder = crate::paths::logs_folder();
    let Ok(entries) = std::fs::read_dir(&folder) else { return String::new() };
    let mut files: Vec<_> = entries.flatten().filter(|e| e.path().extension().is_some_and(|x| x == "log")).collect();
    files.sort_by_key(|e| e.metadata().and_then(|m| m.modified()).ok());
    let mut text = String::new();
    for file in files.iter().rev().take(2).rev() {
        text.push_str(&format!("===== {} =====\n", file.file_name().to_string_lossy()));
        text.push_str(&std::fs::read_to_string(file.path()).unwrap_or_default());
        text.push('\n');
    }
    let chars: Vec<char> = text.chars().collect();
    let tail: String = if chars.len() > MAX_LOG { chars[chars.len() - MAX_LOG..].iter().collect() } else { text };
    anonymize(&tail)
}

/// Takes the user name out of paths and wherever else it appears.
pub fn anonymize(log: &str) -> String {
    let mut out = re!(r#"(?i)([\\/](?:Users|home)[\\/])[^\\/:*?"<>|\r\n]+"#).replace_all(log, "${1}<user>").into_owned();
    let user = std::env::var("USERNAME").or_else(|_| std::env::var("USER")).unwrap_or_default();
    if user.chars().count() >= 3
        && let Ok(re) = Regex::new(&format!(r"(?i)\b{}\b", regex::escape(&user))) {
            out = re.replace_all(&out, "<user>").into_owned();
        }
    out
}

/// Sends a report; the issue number the relay gave it.
pub fn send_report(http: &Http, report: &Report) -> Result<i64> {
    if report.description.trim().is_empty() {
        return Err(AppError::new("INVALID_REPORT", t!("Please describe the problem.")));
    }
    let body = json!({
        "kind": report.kind,
        "description": clip(report.description.trim(), MAX_DESCRIPTION),
        "contact": report.contact.as_ref().map(|c| c.trim()).filter(|c| !c.is_empty()).map(|c| clip(c, MAX_CONTACT)),
        "version": crate::paths::version(),
        "language": crate::i18n::current(),
        "windows": os_description(),
        "portable": crate::paths::is_portable(),
        "log": report.log.as_ref().map(|l| { let c: Vec<char> = l.chars().collect(); c[c.len().saturating_sub(MAX_LOG)..].iter().collect::<String>() }),
    });
    let response = http
        .send(Request::post_json(RELAY_URL, &body).timeout(Duration::from_secs(60)).header("X-TimePlayer-Report", "1"))
        .map_err(|_| AppError::network(t!("Could not reach the report service. Check your internet connection and try again.")))?;
    if response.ok()
        && let Some(number) = response.json().ok().and_then(|j| j.get("number").and_then(Value::as_i64)) {
            log::info!("Report sent as number {number}.");
            return Ok(number);
        }
    log::warn!("The report relay answered {}: {}", response.status, clip(&response.text(), 300));
    Err(AppError::network(if response.status == 429 {
        t!("Too many reports were sent from this connection just now. Wait a minute and try again.")
    } else {
        t!("The report service answered with an error ({0}).", response.status)
    }))
}

// ----- Run counter -----

pub const SHARED_COUNTER_URL: &str = "https://abacus.jasoncameron.dev/hit/timeplayer-ernie96/runs";

/// Counts this start; the new count.
pub fn record_run(settings: &Settings) -> i64 {
    let mut count = 0;
    settings.update(|c| {
        c.runs.count += 1;
        count = c.runs.count;
    });
    count
}

/// Adds 1 to the shared total when the user agreed; the total.
pub fn send_run(http: &Http, settings: &Settings) -> Option<i64> {
    if settings.with(|c| c.runs.share) != Some(true) {
        return None;
    }
    http.get_json(SHARED_COUNTER_URL, Duration::from_secs(10)).ok()?.get("value")?.as_i64().filter(|v| *v > 0)
}

pub fn runs_spoken(mine: i64, everyone: Option<i64>) -> String {
    let mut text = plural_t!(mine, "TimePlayer has been run {0:N0} time on this computer.", "TimePlayer has been run {0:N0} times on this computer.");
    if let Some(total) = everyone {
        text.push(' ');
        text.push_str(&plural_t!(total, "Everyone together: {0:N0} time.", "Everyone together: {0:N0} times."));
    }
    text
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn segments_are_skipped_once_and_labels_said_once() {
        let json = r#"[{"videoID":"abc","segments":[{"segment":[10,50],"category":"sponsor","actionType":"skip","UUID":"u1"},
            {"segment":[0,0],"category":"exclusive_access","actionType":"full","UUID":"u2"},
            {"segment":[90,90],"category":"poi_highlight","actionType":"poi","UUID":"u3"}]},{"videoID":"other","segments":[]}]"#;
        let mut tracker = SegmentTracker::default();
        tracker.reset(Some("abc"), parse_segments(json, "abc"));
        let config = SponsorBlockConfig::default();
        let first = tracker.update(5.0, &config);
        assert!(matches!(first[0], SegmentEvent::WholeVideo(_)));
        assert!(matches!(first[1], SegmentEvent::HighlightAvailable(_)));
        assert!(matches!(tracker.update(12.0, &config)[..], [SegmentEvent::Skip(_)]));
        assert!(tracker.update(13.0, &config).is_empty());
        assert_eq!(tracker.highlight().unwrap().start, 90.0);
    }

    #[test]
    fn songs_and_lyrics() {
        assert_eq!(guess_song("Adele - Someone Like You (Official Music Video)", "AdeleVEVO"), ("Adele".into(), "Someone Like You".into()));
        assert_eq!(guess_song("Hello", "Adele - Topic"), ("Adele".into(), "Hello".into()));
        let lines = parse_lrc("[00:12.50]First\n[00:05.00][01:00.00]Chorus\n[00:20.00]\n");
        assert_eq!(lines.iter().map(|l| l.start.unwrap()).collect::<Vec<_>>(), [5.0, 12.5, 60.0]);
    }

    #[test]
    fn transcripts_from_each_format() {
        let vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nHello &amp; welcome\n\n00:00:03.000 --> 00:00:04.000\nHello &amp; welcome\n\n00:01:05.500 --> 00:01:06.000\n<c>Next</c>\n";
        let lines = parse_transcript(vtt);
        assert_eq!(lines.len(), 2);
        assert_eq!(lines[0].text, "Hello & welcome");
        assert_eq!(lines[1].start, 65.5);
        let json3 = r#"{"events":[{"tStartMs":1500,"segs":[{"utf8":"Hi "},{"utf8":"there"}]}]}"#;
        assert_eq!(parse_transcript(json3)[0].text, "Hi there");
        let xml = r#"<transcript><text start="2.5" dur="1">A &amp;#39;quote&amp;#39;</text></transcript>"#;
        assert_eq!(parse_transcript(xml)[0].start, 2.5);
    }

    #[test]
    fn logs_lose_the_user_name() {
        assert_eq!(anonymize(r"C:\Users\Ernesto\AppData\x"), r"C:\Users\<user>\AppData\x");
        assert_eq!(anonymize("/home/ernesto/.local"), "/home/<user>/.local");
    }

    #[test]
    fn counts_use_separators() {
        assert_eq!(runs_spoken(1234, Some(1)), "TimePlayer has been run 1,234 times on this computer. Everyone together: 1 time.");
    }
}
