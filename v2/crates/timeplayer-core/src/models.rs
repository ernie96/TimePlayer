//! What TimePlayer lists and plays: search results, video details, captions, comments, streams, pages.
//! Saved in the library files with 1.x's names (camelCase, enums as numbers, times as "00:26:28").

use chrono::{DateTime, Utc};
use serde::{Deserialize, Deserializer, Serialize, Serializer};
use std::time::Duration;

use crate::i18n::plural;
use crate::t;
use crate::text;

/// .NET's TimeSpan text: "00:26:28", "00:09:53.9600330" or "1.02:03:04".
pub mod timespan {
    use super::*;

    pub fn to_text(d: Duration) -> String {
        let total = d.as_secs();
        let (days, h, m, s) = (total / 86400, (total % 86400) / 3600, (total % 3600) / 60, total % 60);
        let ticks = d.subsec_nanos() / 100;
        let mut out = if days > 0 { format!("{days}.{h:02}:{m:02}:{s:02}") } else { format!("{h:02}:{m:02}:{s:02}") };
        if ticks > 0 {
            out.push_str(&format!(".{ticks:07}"));
        }
        out
    }

    pub fn from_text(text: &str) -> Option<Duration> {
        let text = text.trim();
        let (days, rest) = match text.split_once(':') {
            Some((first, _)) if first.contains('.') => {
                let (d, h) = first.split_once('.')?;
                (d.parse::<u64>().ok()?, format!("{h}{}", &text[first.len()..]))
            }
            _ => (0, text.to_owned()),
        };
        let parts: Vec<&str> = rest.split(':').collect();
        if parts.len() != 3 {
            return None;
        }
        let h: u64 = parts[0].parse().ok()?;
        let m: u64 = parts[1].parse().ok()?;
        let s: f64 = parts[2].parse().ok()?;
        Some(Duration::from_secs(days * 86400 + h * 3600 + m * 60) + Duration::from_secs_f64(s))
    }

    pub fn serialize<S: Serializer>(value: &Option<Duration>, s: S) -> Result<S::Ok, S::Error> {
        match value {
            Some(d) => s.serialize_str(&to_text(*d)),
            None => s.serialize_none(),
        }
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<Duration>, D::Error> {
        let value: Option<String> = Option::deserialize(d)?;
        Ok(value.as_deref().and_then(from_text))
    }
}

/// .NET's DateTimeOffset text, "2026-11-10T11:47:22.9678017+00:00".
pub mod dateoffset {
    use super::*;

    pub fn to_text(when: &DateTime<Utc>) -> String {
        // chrono has no seven-digit fraction; .NET writes ticks (100 ns).
        use chrono::Timelike;
        format!("{}.{:07}+00:00", when.format("%Y-%m-%dT%H:%M:%S"), when.nanosecond() % 1_000_000_000 / 100)
    }

    pub fn parse(text: &str) -> Option<DateTime<Utc>> {
        DateTime::parse_from_rfc3339(text.trim()).ok().map(|d| d.with_timezone(&Utc))
    }

    pub fn serialize<S: Serializer>(value: &DateTime<Utc>, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_str(&to_text(value))
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<DateTime<Utc>, D::Error> {
        let value: Option<String> = Option::deserialize(d)?;
        Ok(value.as_deref().and_then(parse).unwrap_or(DateTime::<Utc>::MIN_UTC))
    }

    pub mod option {
        use super::*;

        pub fn serialize<S: Serializer>(value: &Option<DateTime<Utc>>, s: S) -> Result<S::Ok, S::Error> {
            match value {
                Some(when) => s.serialize_str(&to_text(when)),
                None => s.serialize_none(),
            }
        }

        pub fn deserialize<'de, D: Deserializer<'de>>(d: D) -> Result<Option<DateTime<Utc>>, D::Error> {
            let value: Option<String> = Option::deserialize(d)?;
            Ok(value.as_deref().and_then(parse))
        }
    }
}

/// 1.x's `SearchResultKind`, saved as a number.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ItemKind {
    #[default]
    Video,
    /// A channel; `id` holds the channel ID.
    Channel,
    /// A playlist (local or YouTube); `id` holds its library or YouTube ID.
    Playlist,
}

impl Serialize for ItemKind {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        s.serialize_u8(*self as u8)
    }
}

impl<'de> Deserialize<'de> for ItemKind {
    fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
        Ok(match u8::deserialize(d)? {
            1 => Self::Channel,
            2 => Self::Playlist,
            _ => Self::Video,
        })
    }
}

/// A video, channel or playlist in a list (1.x's `SearchResult`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize, Default)]
#[serde(rename_all = "camelCase", default)]
pub struct SearchResult {
    pub kind: ItemKind,
    #[serde(with = "dateoffset::option")]
    pub published_at: Option<DateTime<Utc>>,
    pub is_short: bool,
    pub is_live: bool,
    /// Library status said first, such as "New", "Watched" or "Resume at 3 minutes".
    pub status_prefix: Option<String>,
    /// Number of videos, for playlist rows.
    pub playlist_video_count: Option<i64>,
    /// Set on playlist rows that are in the user's library.
    pub library_playlist_id: Option<String>,
    pub id: String,
    pub title: String,
    pub channel_title: String,
    pub channel_id: Option<String>,
    #[serde(with = "timespan")]
    pub duration: Option<Duration>,
    pub view_count: Option<i64>,
    pub upload_date_text: Option<String>,
    pub thumbnail_url: Option<String>,
    pub description_snippet: Option<String>,
}

impl SearchResult {
    pub fn video(id: &str, title: &str, channel: &str) -> Self {
        Self { id: id.into(), title: title.into(), channel_title: channel.into(), ..Default::default() }
    }

    pub fn is_video(&self) -> bool {
        self.kind == ItemKind::Video
    }

    /// What a screen reader says on the row: title, channel, duration in words, views, age.
    pub fn accessible_summary(&self) -> String {
        match self.kind {
            ItemKind::Playlist => {
                let count = self.playlist_video_count.map(|n| format!(", {}", plural(n, "{0} video", "{0} videos"))).unwrap_or_default();
                let owner = if self.channel_title.trim().is_empty() { String::new() } else { format!(", {}", t!("by {0}", self.channel_title)) };
                format!("{}{count}{owner}", t!("Playlist: {0}", self.title))
            }
            ItemKind::Channel => {
                let mut parts = vec![t!("Channel: {0}", self.title)];
                if let Some(prefix) = self.status_prefix.as_ref().filter(|p| !p.trim().is_empty()) {
                    parts.push(prefix.clone());
                }
                parts.join(". ")
            }
            ItemKind::Video => {
                let mut parts = Vec::new();
                if let Some(prefix) = self.status_prefix.as_ref().filter(|p| !p.trim().is_empty()) {
                    parts.push(prefix.clone());
                }
                parts.push(self.title.clone());
                parts.push(t!("by {0}", self.channel_title));
                if self.is_live {
                    parts.push(t!("Live now"));
                } else if self.is_short {
                    parts.push(t!("Short"));
                }
                if let Some(d) = self.duration.filter(|d| !d.is_zero()) {
                    parts.push(text::time_spoken(d));
                }
                if let Some(views) = self.view_count {
                    parts.push(text::view_count(views));
                }
                if let Some(when) = self.published_at {
                    parts.push(text::age_spoken(when, Utc::now()));
                } else if let Some(age) = self.upload_date_text.as_ref().filter(|a| !a.trim().is_empty()) {
                    parts.push(text::translate_age(age));
                }
                parts.join(". ")
            }
        }
    }

    pub fn watch_url(&self) -> String {
        crate::links::watch_url(&self.id)
    }
}

/// A page of results and the token for the next one.
#[derive(Debug, Clone, Default)]
pub struct Page<T> {
    pub items: Vec<T>,
    pub next_token: Option<String>,
}

impl<T> Page<T> {
    pub fn single(items: Vec<T>) -> Self {
        Self { items, next_token: None }
    }

    pub fn has_next(&self) -> bool {
        self.next_token.as_ref().is_some_and(|t| !t.is_empty())
    }
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct ChannelInfo {
    pub id: String,
    pub name: String,
}

/// A subtitle or caption track.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CaptionTrack {
    pub language_code: String,
    pub display_name: String,
    pub url: String,
    pub format: String,
    pub auto_generated: bool,
    pub is_default: bool,
}

impl CaptionTrack {
    pub fn label(&self) -> String {
        let mut label = self.display_name.clone();
        if self.auto_generated {
            label.push_str(" (auto-generated)");
        }
        if self.is_default {
            label.push_str(" [Default]");
        }
        label
    }
}

/// One media stream a source offers.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct StreamCandidate {
    pub url: String,
    pub format: String,
    pub quality_label: Option<String>,
    pub height: Option<u32>,
    pub width: Option<u32>,
    pub fps: Option<u32>,
    pub bitrate: Option<i64>,
    pub audio_bitrate: Option<i64>,
    pub video_codec: Option<String>,
    pub audio_codec: Option<String>,
    pub audio_only: bool,
    pub video_only: bool,
    pub progressive: bool,
    pub content_length: Option<i64>,
    /// The audio track's name when a video has several (dubbed languages, audio description).
    pub audio_track_name: Option<String>,
    pub audio_description: bool,
}

/// YouTube names its audio-described tracks "... descriptive".
pub fn is_descriptive_track_name(name: Option<&str>) -> bool {
    name.is_some_and(|n| n.to_lowercase().contains("descriptive"))
}

/// Which source found a stream.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SourceKind {
    #[default]
    TubeRepair,
    Invidious,
    InnerTube,
    YtDlp,
}

impl SourceKind {
    pub fn name(self) -> &'static str {
        match self {
            Self::TubeRepair => "TubeRepair",
            Self::Invidious => "Invidious",
            Self::InnerTube => "InnerTube",
            Self::YtDlp => "yt-dlp",
        }
    }
}

/// What the player needs: a video (or combined) address, maybe a separate audio track, and captions.
#[derive(Debug, Clone, Default)]
pub struct ResolvedStream {
    pub video_id: String,
    pub url: String,
    pub audio_url: Option<String>,
    pub captions: Vec<CaptionTrack>,
    pub is_live: bool,
    pub source: SourceKind,
    pub has_audio_description: bool,
    pub uses_audio_description: bool,
    pub alternatives: Vec<StreamCandidate>,
}

/// Everything about a video.
#[derive(Debug, Clone, Default)]
pub struct VideoDetails {
    pub id: String,
    pub title: String,
    pub description: Option<String>,
    pub channel: ChannelInfo,
    pub duration: Duration,
    pub view_count: Option<i64>,
    pub like_count: Option<i64>,
    pub upload_date: Option<DateTime<Utc>>,
    pub thumbnail_url: Option<String>,
    pub streams: Vec<StreamCandidate>,
    pub captions: Vec<CaptionTrack>,
    pub is_live: bool,
}

impl VideoDetails {
    pub fn to_search_result(&self) -> SearchResult {
        SearchResult {
            id: self.id.clone(),
            title: self.title.clone(),
            channel_title: self.channel.name.clone(),
            channel_id: Some(self.channel.id.clone()).filter(|c| !c.is_empty()),
            duration: Some(self.duration).filter(|d| !d.is_zero()),
            view_count: self.view_count,
            thumbnail_url: self.thumbnail_url.clone(),
            description_snippet: self.description.as_ref().map(|d| d.chars().take(200).collect()),
            is_live: self.is_live,
            ..Default::default()
        }
    }
}

/// Where a comment came from; reply tokens only work with the source that gave them.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum CommentSource {
    #[default]
    InnerTube,
    Invidious,
}

#[derive(Debug, Clone, PartialEq, Default)]
pub struct Comment {
    pub id: String,
    pub author: String,
    pub text: String,
    pub author_channel_id: Option<String>,
    pub published_text: Option<String>,
    /// Spoken form, such as "104K likes".
    pub likes_text: Option<String>,
    pub reply_count: i64,
    pub pinned: bool,
    /// Written by the video's own channel.
    pub creator: bool,
    pub is_reply: bool,
    pub replies_token: Option<String>,
    pub source: CommentSource,
}

impl Comment {
    pub fn has_replies(&self) -> bool {
        self.reply_count > 0 && self.replies_token.as_ref().is_some_and(|t| !t.is_empty())
    }

    /// Who, what they said, then the details in one parenthesis, so arrowing through stays quick.
    pub fn accessible_summary(&self) -> String {
        let mut who = self.author.clone();
        if self.creator {
            who.push_str(", channel owner");
        }
        if self.is_reply {
            who = format!("Reply from {who}");
        }
        if self.pinned {
            who = format!("Pinned. {who}");
        }
        let mut details: Vec<String> = Vec::new();
        if let Some(p) = self.published_text.as_ref().filter(|p| !p.trim().is_empty()) {
            details.push(p.clone());
        }
        if let Some(l) = self.likes_text.as_ref().filter(|l| !l.trim().is_empty()) {
            details.push(l.clone());
        }
        if self.reply_count > 0 {
            details.push(if self.reply_count == 1 { "1 reply".into() } else { format!("{} replies", self.reply_count) });
        }
        if details.is_empty() { format!("{who}: {}", self.text) } else { format!("{who}: {} ({})", self.text, details.join(", ")) }
    }
}

#[derive(Debug, Clone, Default)]
pub struct CommentPage {
    pub comments: Vec<Comment>,
    pub next_token: Option<String>,
    pub source: CommentSource,
}

/// Related videos for the watch page, plus what's needed to load its comments.
#[derive(Debug, Clone, Default)]
pub struct WatchNext {
    pub related: Vec<SearchResult>,
    pub comments_token: Option<String>,
    pub description: Option<String>,
}

/// A YouTube playlist's title and one page of its videos.
#[derive(Debug, Clone, Default)]
pub struct PlaylistPage {
    pub playlist_id: String,
    pub title: String,
    pub owner: Option<String>,
    pub videos: Vec<SearchResult>,
    pub next_token: Option<String>,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_1x_feed_rows() {
        let json = r#"{"kind":0,"publishedAt":"2026-11-10T11:00:39+00:00","isShort":false,"isLive":false,
            "statusPrefix":null,"isVideo":true,"id":"w3VthEUSQvM","title":"T","channelTitle":"C",
            "channelId":"UC4","duration":"00:26:28","viewCount":7691,"viewCountText":"7.7 thousand views",
            "accessibleSummary":"ignored"}"#;
        let r: SearchResult = serde_json::from_str(json).unwrap();
        assert_eq!(r.duration, Some(Duration::from_secs(1588)));
        assert!(r.published_at.is_some());
        let back = serde_json::to_value(&r).unwrap();
        assert_eq!(back["duration"], "00:26:28");
        assert_eq!(back["kind"], 0);
    }

    #[test]
    fn timespans_round_trip() {
        assert_eq!(timespan::from_text("00:09:53.9600330").unwrap().as_millis(), 593_960);
        assert_eq!(timespan::from_text("1.02:03:04").unwrap().as_secs(), 93_784);
        assert_eq!(timespan::to_text(Duration::from_secs(93_784)), "1.02:03:04");
    }
}
