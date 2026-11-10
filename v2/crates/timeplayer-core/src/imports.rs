//! Subscription lists in the formats other YouTube clients use (NewPipe, Tubular, LibreTube and Invidious
//! JSON, Google Takeout CSV, OPML, FreeTube's profiles.db), and Google Takeout zips with playlists, likes,
//! Watch later and history (1.x's `SubscriptionFormats` and `TakeoutImport`).

use quick_xml::events::Event;
use regex::Regex;
use serde_json::{Value, json};
use std::io::{Read, Seek};
use std::sync::OnceLock;

use crate::error::{AppError, Result};
use crate::library::{ChannelSubscription, HistoryEntry};
use crate::models::{SearchResult, dateoffset};

const NEWPIPE_APP_VERSION: &str = "0.27.6";
const NEWPIPE_APP_VERSION_INT: i64 = 999;

#[derive(Debug, Clone)]
pub struct SubscriptionImport {
    pub format_name: String,
    pub channels: Vec<ChannelSubscription>,
    pub skipped: usize,
}

pub fn extract_channel_id(url_or_id: &str) -> Option<String> {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(r"(UC[0-9A-Za-z_-]{22})").unwrap()).captures(url_or_id).map(|c| c[1].to_owned())
}

fn format_error(message: &str) -> AppError {
    AppError::new("IMPORT_FORMAT", message)
}

/// Reads any of the supported subscription files.
pub fn parse_subscriptions(content: &str) -> Result<SubscriptionImport> {
    let text = content.trim_start_matches(['\u{feff}', ' ', '\t', '\r', '\n']);
    if text.starts_with('<') {
        return parse_opml(text);
    }
    if text.starts_with('{') || text.starts_with('[') {
        // FreeTube's profiles.db is one JSON object per line.
        let multi_line = text.split('\n').filter(|l| !l.trim().is_empty()).count() > 1;
        if text.contains("\"subscriptions\"") && multi_line && serde_json::from_str::<Value>(text).is_err() {
            return Ok(parse_freetube_db(text));
        }
        return parse_json(text);
    }
    parse_takeout_csv(text)
}

/// A Takeout (or other) zip: every CSV, JSON and OPML file inside is tried and the one with the most
/// channels wins, since Takeout translates its folder and file names.
pub fn parse_subscriptions_zip<R: Read + Seek>(reader: R) -> Result<SubscriptionImport> {
    let mut zip = zip::ZipArchive::new(reader).map_err(|e| format_error(&e.to_string()))?;
    let mut best: Option<SubscriptionImport> = None;
    for index in 0..zip.len() {
        let Ok(mut entry) = zip.by_index(index) else { continue };
        let name = entry.name().to_lowercase();
        if !(name.ends_with(".csv") || name.ends_with(".json") || name.ends_with(".opml") || name.ends_with(".xml"))
            || entry.size() == 0
            || entry.size() > 20_000_000
        {
            continue;
        }
        let mut text = String::new();
        if entry.read_to_string(&mut text).is_err() {
            continue;
        }
        if let Ok(mut parsed) = parse_subscriptions(&text)
            && best.as_ref().is_none_or(|b| parsed.channels.len() > b.channels.len()) {
                if parsed.format_name == "Google Takeout CSV" {
                    parsed.format_name = "Google Takeout".into();
                }
                best = Some(parsed);
            }
    }
    best.filter(|b| !b.channels.is_empty()).ok_or_else(|| {
        format_error("No YouTube subscriptions were found in this zip. In Google Takeout, make sure \"subscriptions\" is selected under YouTube and YouTube Music.")
    })
}

fn str_field<'a>(obj: &'a Value, key: &str) -> Option<&'a str> {
    obj.get(key).and_then(Value::as_str)
}

fn parse_json(text: &str) -> Result<SubscriptionImport> {
    let root: Value = serde_json::from_str(text).map_err(|e| format_error(&e.to_string()))?;
    let array = match &root {
        Value::Array(a) => a,
        Value::Object(o) => match o.get("subscriptions") {
            Some(Value::Array(a)) => a,
            _ => return Err(format_error("This JSON file has no \"subscriptions\" list.")),
        },
        _ => return Err(format_error("This JSON file has no \"subscriptions\" list.")),
    };
    let mut channels = Vec::new();
    let mut skipped = 0;
    for node in array {
        let (id, name) = match node {
            Value::String(raw) => (extract_channel_id(raw), None),
            Value::Object(_) => {
                // NewPipe also exports SoundCloud, PeerTube, ... subscriptions; only YouTube (0) applies.
                if node.get("service_id").and_then(Value::as_i64).is_some_and(|s| s != 0) {
                    skipped += 1;
                    continue;
                }
                let id = ["url", "id", "channel_id"].iter().find_map(|k| str_field(node, k).and_then(extract_channel_id));
                let name = ["name", "title", "author"].iter().find_map(|k| str_field(node, k));
                (id, name)
            }
            _ => (None, None),
        };
        match id {
            Some(id) => {
                let name = name.map(str::trim).filter(|n| !n.is_empty()).unwrap_or(&id).to_owned();
                channels.push(ChannelSubscription::new(&id, &name));
            }
            None => skipped += 1,
        }
    }
    let format = if root.get("app_version").and_then(Value::as_str).is_some() { "NewPipe" } else { "JSON" };
    Ok(SubscriptionImport { format_name: format.into(), channels: dedupe(channels), skipped })
}

fn parse_freetube_db(text: &str) -> SubscriptionImport {
    let mut channels = Vec::new();
    let mut skipped = 0;
    for line in text.split('\n').map(str::trim).filter(|l| !l.is_empty()) {
        match serde_json::from_str::<Value>(line) {
            Ok(obj) => {
                if let Some(subs) = obj.get("subscriptions").filter(|s| s.is_array())
                    && let Ok(parsed) = parse_json(&json!({ "subscriptions": subs }).to_string()) {
                        channels.extend(parsed.channels);
                        skipped += parsed.skipped;
                    }
            }
            Err(_) => skipped += 1,
        }
    }
    SubscriptionImport { format_name: "FreeTube".into(), channels: dedupe(channels), skipped }
}

fn parse_opml(text: &str) -> Result<SubscriptionImport> {
    let mut reader = quick_xml::Reader::from_str(text);
    let mut channels = Vec::new();
    let mut skipped = 0;
    loop {
        match reader.read_event() {
            Ok(Event::Start(e) | Event::Empty(e)) if e.name().as_ref() == b"outline" => {
                let mut attrs = std::collections::HashMap::new();
                for a in e.attributes().flatten() {
                    let key = String::from_utf8_lossy(a.key.as_ref()).into_owned();
                    let value = a.unescape_value().map(|v| v.into_owned()).unwrap_or_default();
                    attrs.insert(key, value);
                }
                let Some(url) = attrs.get("xmlUrl").or_else(|| attrs.get("htmlUrl")) else { continue };
                match extract_channel_id(url) {
                    Some(id) => {
                        let name = attrs.get("title").or_else(|| attrs.get("text")).cloned().unwrap_or_else(|| id.clone());
                        channels.push(ChannelSubscription::new(&id, name.trim()));
                    }
                    None => skipped += 1,
                }
            }
            Ok(Event::Eof) => break,
            Err(e) => return Err(format_error(&e.to_string())),
            _ => {}
        }
    }
    Ok(SubscriptionImport { format_name: "OPML".into(), channels: dedupe(channels), skipped })
}

fn parse_takeout_csv(text: &str) -> Result<SubscriptionImport> {
    // "Channel Id,Channel Url,Channel Title": the header is translated, the ID column isn't.
    let mut channels = Vec::new();
    let mut skipped = 0;
    for raw in text.split('\n') {
        let line = raw.trim();
        if line.is_empty() {
            continue;
        }
        let fields = split_csv_line(line);
        let Some(id) = fields.iter().find_map(|f| extract_channel_id(f)) else {
            if !channels.is_empty() {
                skipped += 1;
            }
            continue;
        };
        let name = fields
            .iter()
            .rev()
            .find(|f| !f.trim().is_empty() && extract_channel_id(f).is_none() && !f.to_lowercase().starts_with("http"))
            .map(|f| f.trim().to_owned())
            .unwrap_or_else(|| id.clone());
        channels.push(ChannelSubscription::new(&id, &name));
    }
    if channels.is_empty() {
        return Err(format_error(
            "No YouTube channels were found. Supported files: NewPipe or LibreTube JSON, Google Takeout CSV, OPML and FreeTube profiles.",
        ));
    }
    Ok(SubscriptionImport { format_name: "Google Takeout CSV".into(), channels: dedupe(channels), skipped })
}

fn split_csv_line(line: &str) -> Vec<String> {
    let mut fields = Vec::new();
    let mut current = String::new();
    let mut quoted = false;
    let mut chars = line.chars().peekable();
    while let Some(c) = chars.next() {
        if quoted {
            if c == '"' && chars.peek() == Some(&'"') {
                current.push('"');
                chars.next();
            } else if c == '"' {
                quoted = false;
            } else {
                current.push(c);
            }
        } else if c == '"' {
            quoted = true;
        } else if c == ',' {
            fields.push(std::mem::take(&mut current));
        } else {
            current.push(c);
        }
    }
    fields.push(current);
    fields
}

fn dedupe(channels: Vec<ChannelSubscription>) -> Vec<ChannelSubscription> {
    let mut seen = std::collections::HashSet::new();
    channels.into_iter().filter(|c| seen.insert(c.channel_id.clone())).collect()
}

/// NewPipe's export format, importable by NewPipe, Tubular, LibreTube and TimePlayer.
pub fn to_newpipe_json(channels: &[ChannelSubscription]) -> String {
    let subs: Vec<Value> = channels.iter().map(|c| json!({ "service_id": 0, "url": c.url(), "name": c.name })).collect();
    serde_json::to_string_pretty(&json!({
        "app_version": NEWPIPE_APP_VERSION,
        "app_version_int": NEWPIPE_APP_VERSION_INT,
        "subscriptions": subs,
    }))
    .unwrap_or_default()
}

fn xml_escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

pub fn to_opml(channels: &[ChannelSubscription]) -> String {
    let mut out = String::from("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<opml version=\"1.1\">\n  <head>\n    <title>TimePlayer subscriptions</title>\n  </head>\n  <body>\n    <outline text=\"YouTube Subscriptions\" title=\"YouTube Subscriptions\">\n");
    for c in channels {
        let name = xml_escape(&c.name);
        out.push_str(&format!(
            "      <outline text=\"{name}\" title=\"{name}\" type=\"rss\" xmlUrl=\"https://www.youtube.com/feeds/videos.xml?channel_id={}\" htmlUrl=\"{}\" />\n",
            c.channel_id,
            c.url()
        ));
    }
    out.push_str("    </outline>\n  </body>\n</opml>");
    out
}

/// A playlist in a Takeout export: its name and video IDs (Takeout has no titles).
#[derive(Debug, Clone, PartialEq)]
pub struct TakeoutPlaylist {
    pub name: String,
    pub video_ids: Vec<String>,
}

#[derive(Debug, Clone, Default)]
pub struct TakeoutContents {
    pub subscriptions: Option<SubscriptionImport>,
    pub playlists: Vec<TakeoutPlaylist>,
    pub liked_video_ids: Vec<String>,
    pub watch_later_ids: Vec<String>,
    pub history: Vec<HistoryEntry>,
}

impl TakeoutContents {
    pub fn is_empty(&self) -> bool {
        self.subscriptions.as_ref().is_none_or(|s| s.channels.is_empty())
            && self.playlists.is_empty()
            && self.liked_video_ids.is_empty()
            && self.watch_later_ids.is_empty()
            && self.history.is_empty()
    }
}

/// Everything account-like in a Takeout zip, read in place (they can hold gigabytes of uploads).
pub fn parse_takeout_zip(path: &std::path::Path) -> Result<TakeoutContents> {
    let subscriptions = std::fs::File::open(path).ok().and_then(|f| parse_subscriptions_zip(std::io::BufReader::new(f)).ok());
    let file = std::fs::File::open(path)?;
    let mut zip = zip::ZipArchive::new(std::io::BufReader::new(file)).map_err(|e| format_error(&e.to_string()))?;
    let mut contents = TakeoutContents { subscriptions, ..Default::default() };
    for index in 0..zip.len() {
        let Ok(mut entry) = zip.by_index(index) else { continue };
        if entry.size() == 0 || entry.size() > 100_000_000 {
            continue;
        }
        let full = entry.name().to_owned();
        let lower = full.to_lowercase();
        let file_name = full.rsplit('/').next().unwrap_or(&full).to_owned();
        if lower.ends_with(".csv") {
            let mut text = String::new();
            if entry.read_to_string(&mut text).is_err() {
                continue;
            }
            let ids = parse_playlist_csv(&text);
            if ids.is_empty() {
                continue;
            }
            let name = playlist_name_from_file(&file_name);
            if name.eq_ignore_ascii_case("Liked videos") || name.eq_ignore_ascii_case("Liked music") {
                contents.liked_video_ids.extend(ids);
            } else if name.eq_ignore_ascii_case("Watch later") {
                contents.watch_later_ids.extend(ids);
            } else {
                contents.playlists.push(TakeoutPlaylist { name, video_ids: ids });
            }
        } else if lower.ends_with(".json") && entry.size() > 2 {
            let mut text = String::new();
            if entry.read_to_string(&mut text).is_err() {
                continue;
            }
            let entries = parse_watch_history_json(&text);
            if entries.len() > contents.history.len() {
                contents.history = entries;
            }
        }
    }
    dedupe_strings(&mut contents.liked_video_ids);
    dedupe_strings(&mut contents.watch_later_ids);
    Ok(contents)
}

fn dedupe_strings(list: &mut Vec<String>) {
    let mut seen = std::collections::HashSet::new();
    list.retain(|s| seen.insert(s.clone()));
}

/// "Road trip-videos.csv" → "Road trip"; older exports use "Road trip.csv".
pub fn playlist_name_from_file(file_name: &str) -> String {
    let stem = file_name.rsplit_once('.').map(|(s, _)| s).unwrap_or(file_name);
    let stem = if stem.to_lowercase().ends_with("-videos") { &stem[..stem.len() - "-videos".len()] } else { stem };
    if stem.trim().is_empty() { "Imported playlist".into() } else { stem.trim().into() }
}

/// Video IDs from a Takeout playlist CSV ("Video ID,…" header, or details then "Video Id,Time Added").
pub fn parse_playlist_csv(content: &str) -> Vec<String> {
    let lines: Vec<&str> = content.trim_start_matches('\u{feff}').split('\n').map(|l| l.trim_end_matches('\r')).collect();
    let Some(header) = lines.iter().position(|l| l.to_lowercase().starts_with("video id")) else { return Vec::new() };
    lines[header + 1..]
        .iter()
        .filter_map(|line| {
            let first = line.split(',').next().unwrap_or("").trim().trim_matches('"');
            crate::links::is_video_id(first).then(|| first.to_owned())
        })
        .collect()
}

/// history/watch-history.json, newest first; repeated views become a watch count.
pub fn parse_watch_history_json(text: &str) -> Vec<HistoryEntry> {
    let Ok(Value::Array(items)) = serde_json::from_str::<Value>(text) else { return Vec::new() };
    static RE: OnceLock<Regex> = OnceLock::new();
    let watch = RE.get_or_init(|| Regex::new(r"[?&]v=([A-Za-z0-9_-]{11})").unwrap());
    let mut by_id: std::collections::HashMap<String, HistoryEntry> = std::collections::HashMap::new();
    let mut order = Vec::new();
    for item in &items {
        let Some(url) = item.get("titleUrl").and_then(Value::as_str) else { continue };
        let Some(id) = watch.captures(url).map(|c| c[1].to_owned()) else { continue };
        if let Some(existing) = by_id.get_mut(&id) {
            existing.watch_count += 1;
            continue;
        }
        let mut title = item.get("title").and_then(Value::as_str).unwrap_or(&id).to_owned();
        if let Some(rest) = title.strip_prefix("Watched ") {
            title = rest.to_owned();
        }
        let mut entry = HistoryEntry::new(&id, &title);
        if let Some(sub) = item.get("subtitles").and_then(Value::as_array).and_then(|s| s.first()) {
            entry.channel_title = sub.get("name").and_then(Value::as_str).unwrap_or("").to_owned();
            entry.channel_id = sub.get("url").and_then(Value::as_str).and_then(extract_channel_id);
        }
        if let Some(when) = item.get("time").and_then(Value::as_str).and_then(dateoffset::parse) {
            entry.last_watched_utc = when;
        }
        by_id.insert(id.clone(), entry);
        order.push(id);
    }
    order.into_iter().filter_map(|id| by_id.remove(&id)).collect()
}

/// Takeout gives only IDs; this stands in until titles are looked up.
pub fn placeholder_video(video_id: &str) -> SearchResult {
    SearchResult::video(video_id, &format!("Video {video_id}"), "")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_each_format() {
        let newpipe = r#"{"app_version":"0.27.6","subscriptions":[{"service_id":0,"url":"https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa","name":"A"},{"service_id":1,"url":"x","name":"SoundCloud"}]}"#;
        let r = parse_subscriptions(newpipe).unwrap();
        assert_eq!((r.format_name.as_str(), r.channels.len(), r.skipped), ("NewPipe", 1, 1));

        let csv = "Channel Id,Channel Url,Channel Title\nUCaaaaaaaaaaaaaaaaaaaaaa,http://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa,\"Name, with comma\"\n";
        let r = parse_subscriptions(csv).unwrap();
        assert_eq!(r.channels[0].name, "Name, with comma");

        let opml = r#"<opml><body><outline text="F"><outline title="B &amp; C" xmlUrl="https://www.youtube.com/feeds/videos.xml?channel_id=UCbbbbbbbbbbbbbbbbbbbbbb"/></outline></body></opml>"#;
        let r = parse_subscriptions(opml).unwrap();
        assert_eq!(r.channels[0].name, "B & C");

        let freetube = "{\"name\":\"All\",\"subscriptions\":[{\"id\":\"UCaaaaaaaaaaaaaaaaaaaaaa\",\"name\":\"A\"}]}\n{\"name\":\"Other\",\"subscriptions\":[{\"id\":\"UCbbbbbbbbbbbbbbbbbbbbbb\",\"name\":\"B\"}]}";
        assert_eq!(parse_subscriptions(freetube).unwrap().channels.len(), 2);

        let round = parse_subscriptions(&to_opml(&r.channels)).unwrap();
        assert_eq!(round.channels, r.channels);
    }

    #[test]
    fn takeout_pieces() {
        assert_eq!(playlist_name_from_file("Road trip-videos.csv"), "Road trip");
        assert_eq!(parse_playlist_csv("Video ID,Time Added\ndQw4w9WgXcQ,2024\nbad,1\n"), ["dQw4w9WgXcQ"]);
        let history = parse_watch_history_json(
            r#"[{"title":"Watched Song","titleUrl":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","subtitles":[{"name":"Rick","url":"https://www.youtube.com/channel/UCaaaaaaaaaaaaaaaaaaaaaa"}],"time":"2024-01-02T03:04:05.000Z"},
                {"title":"Watched Song","titleUrl":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","time":"2023-01-02T03:04:05.000Z"}]"#,
        );
        assert_eq!(history.len(), 1);
        assert_eq!(history[0].title, "Song");
        assert_eq!(history[0].watch_count, 2);
        assert_eq!(history[0].channel_id.as_deref(), Some("UCaaaaaaaaaaaaaaaaaaaaaa"));
    }
}
