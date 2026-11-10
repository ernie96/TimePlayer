//! YouTube video IDs and links (1.x's `YouTubeVideoId`, `YouTubeUrl` and `IncomingLink`).

use percent_encoding::{NON_ALPHANUMERIC, percent_decode_str, utf8_percent_encode};
use regex::Regex;
use std::sync::OnceLock;

use crate::error::AppError;

macro_rules! re {
    ($pattern:expr) => {{
        static RE: OnceLock<Regex> = OnceLock::new();
        RE.get_or_init(|| Regex::new($pattern).unwrap())
    }};
}

/// An 11-character YouTube video ID: `[a-zA-Z0-9_-]{11}`.
pub fn is_video_id(text: &str) -> bool {
    re!(r"^[a-zA-Z0-9_-]{11}$").is_match(text.trim())
}

pub fn watch_url(id: &str) -> String {
    format!("https://www.youtube.com/watch?v={id}")
}

/// A parsed YouTube video link: its ID and the start time, if it has one.
#[derive(Debug, Clone, PartialEq)]
pub struct YouTubeUrl {
    pub video_id: String,
    pub start_seconds: Option<f64>,
}

impl YouTubeUrl {
    /// Watch, mobile, youtu.be, embed, v, shorts and live links, with "t=1m30s" or "t=90" start times.
    pub fn parse(input: &str) -> Option<Self> {
        let trimmed = input.trim();
        if trimmed.is_empty() {
            return None;
        }
        if is_video_id(trimmed) {
            return Some(Self { video_id: trimmed.to_owned(), start_seconds: None });
        }
        let id = re!(r"youtu\.be/([a-zA-Z0-9_-]{11})")
            .captures(trimmed)
            .or_else(|| re!(r"[?&]v=([a-zA-Z0-9_-]{11})").captures(trimmed))
            .or_else(|| re!(r"/(?:embed|v|shorts|live)/([a-zA-Z0-9_-]{11})").captures(trimmed))
            .map(|c| c[1].to_owned())?;
        Some(Self { video_id: id, start_seconds: timestamp(trimmed) })
    }

    pub fn normalized(&self) -> String {
        match self.start_seconds {
            Some(s) if s > 0.0 => format!("{}&t={}s", watch_url(&self.video_id), s as i64),
            _ => watch_url(&self.video_id),
        }
    }

    /// For playing typed text: a video link, or a message saying why it can't be played.
    pub fn validate(input: &str) -> Result<Self, AppError> {
        let trimmed = input.trim();
        if trimmed.is_empty() {
            return Err(AppError::invalid_url(
                "Input cannot be empty. Please enter a video search term or paste a YouTube URL.",
            ));
        }
        let lower = trimmed.to_lowercase();
        if lower.contains("playlist?list=") || lower.contains("&list=") {
            if re!(r"[?&]v=([a-zA-Z0-9_-]{11})").is_match(trimmed)
                && let Some(url) = Self::parse(trimmed) {
                    return Ok(url);
                }
            return Err(AppError::new(
                "UNSUPPORTED_URL",
                "Playlists are not supported for single-video playback. Please provide an individual video link or use Search to find videos.",
            ));
        }
        if ["youtube.com/@", "youtube.com/channel/", "youtube.com/c/", "youtube.com/user/"].iter().any(|p| lower.contains(p)) {
            return Err(AppError::new(
                "UNSUPPORTED_URL",
                "Channel pages cannot be played directly. Use the Search box to search for videos from this channel.",
            ));
        }
        if let Some(url) = Self::parse(trimmed) {
            return Ok(url);
        }
        if lower.starts_with("http://") || lower.starts_with("https://") {
            return Err(AppError::new("UNSUPPORTED_URL", "The link provided does not appear to be a supported YouTube video URL."));
        }
        Err(AppError::invalid_url(&format!("'{trimmed}' is not a valid 11-character YouTube video ID or supported URL.")))
    }
}

fn timestamp(url: &str) -> Option<f64> {
    let raw = re!(r"(?i)[?&](?:t|start)=([^&#]+)").captures(url)?[1].trim().to_owned();
    if let Ok(seconds) = raw.trim_end_matches('s').parse::<f64>()
        && seconds >= 0.0 {
            return Some(seconds);
        }
    let hms = re!(r"(?i)^(?:(\d+)h)?(?:(\d+)m)?(?:(\d+)s)?$").captures(&raw)?;
    let part = |i: usize| hms.get(i).and_then(|m| m.as_str().parse::<f64>().ok()).unwrap_or(0.0);
    Some(part(1) * 3600.0 + part(2) * 60.0 + part(3))
}

/// The ID after "list=" in a YouTube link.
pub fn playlist_id(input: &str) -> Option<String> {
    if !input.to_lowercase().contains("youtu") {
        return None;
    }
    re!(r"[?&]list=([A-Za-z0-9_-]{10,})").captures(input).map(|c| c[1].to_owned())
}

/// A link to a playlist itself (not a video that happens to be in one).
pub fn is_playlist_link(input: &str) -> bool {
    playlist_id(input).is_some() && !re!(r"[?&]v=([a-zA-Z0-9_-]{11})").is_match(input.trim())
}

/// Whether typed text should be opened as a link rather than searched for: any YouTube link, or a bare
/// video ID that doesn't read like a word ("taylorswift" is 11 letters, but a search).
pub fn looks_like_link(text: &str) -> bool {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return false;
    }
    if trimmed.contains(['/', '.', '?']) {
        return is_playlist_link(trimmed) || YouTubeUrl::parse(trimmed).is_some();
    }
    is_video_id(trimmed)
        && trimmed.chars().any(|c| c.is_uppercase())
        && trimmed.chars().any(|c| c.is_lowercase())
        && trimmed.chars().any(|c| c.is_ascii_digit() || c == '-' || c == '_')
}

pub const SCHEME: &str = "timeplayer";
const MUSIC_SITE: &str = "https://music.youtube.com";
const MAX_LINK_LENGTH: usize = 4096;

/// "timeplayer://open?url=..." for a YouTube link.
pub fn to_protocol_link(youtube_link: &str) -> String {
    format!("{SCHEME}://open?url={}", utf8_percent_encode(youtube_link, NON_ALPHANUMERIC))
}

/// A clean YouTube link to open (a watch link with its start time, or a playlist link), or None when
/// the argument isn't a YouTube video or playlist. Any web page can open a timeplayer:// link, so only
/// IDs get through, rebuilt; nothing else from the link is passed on.
pub fn normalize_incoming(argument: &str) -> Option<String> {
    if argument.trim().is_empty() || argument.len() > MAX_LINK_LENGTH {
        return None;
    }
    let mut text = argument.trim().trim_matches('"').to_owned();
    if text.len() > SCHEME.len() && text[..SCHEME.len() + 1].eq_ignore_ascii_case(&format!("{SCHEME}:")) {
        text = from_protocol_link(&text[SCHEME.len() + 1..]);
    }
    let site = if is_youtube_music(&text) { MUSIC_SITE } else { "https://www.youtube.com" };
    if is_playlist_link(&text)
        && let Some(list) = playlist_id(&text) {
            return Some(format!("{site}/playlist?list={list}"));
        }
    YouTubeUrl::parse(&text).map(|url| format!("{site}{}", &url.normalized()["https://www.youtube.com".len()..]))
}

/// A music.youtube.com link: TimePlayer opens it in Music.
pub fn is_youtube_music(link: &str) -> bool {
    let mut link = link.trim().to_lowercase();
    if link.starts_with("music.youtube.com/") {
        link = format!("https://{link}");
    }
    link.strip_prefix("https://").or_else(|| link.strip_prefix("http://")).is_some_and(|rest| {
        let host = rest.split(['/', '?', '#']).next().unwrap_or("");
        host == "music.youtube.com"
    })
}

fn from_protocol_link(rest: &str) -> String {
    let rest = rest.trim_start_matches('/');
    if let Some(q) = rest.find('?')
        && rest[..q].trim_end_matches('/').eq_ignore_ascii_case("open") {
            for part in rest[q + 1..].split('&') {
                if part.len() > 4 && part[..4].eq_ignore_ascii_case("url=") {
                    return percent_decode_str(&part[4..].replace('+', " ")).decode_utf8_lossy().into_owned();
                }
            }
            return String::new();
        }
    // timeplayer://https://youtu.be/... typed by hand. Browsers sometimes drop one slash of "https://".
    let mut decoded = percent_decode_str(rest).decode_utf8_lossy().trim_end_matches('/').to_owned();
    let lower = decoded.to_lowercase();
    if lower.starts_with("https:/") && !lower.starts_with("https://") {
        decoded = format!("https://{}", &decoded["https:/".len()..]);
    }
    decoded
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_every_link_shape() {
        for link in [
            "dQw4w9WgXcQ",
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "https://youtu.be/dQw4w9WgXcQ",
            "https://www.youtube.com/shorts/dQw4w9WgXcQ",
            "https://www.youtube.com/live/dQw4w9WgXcQ",
            "https://m.youtube.com/watch?feature=share&v=dQw4w9WgXcQ",
        ] {
            assert_eq!(YouTubeUrl::parse(link).unwrap().video_id, "dQw4w9WgXcQ", "{link}");
        }
        assert_eq!(YouTubeUrl::parse("https://youtu.be/dQw4w9WgXcQ?t=1m30s").unwrap().start_seconds, Some(90.0));
        assert_eq!(YouTubeUrl::parse("https://youtu.be/dQw4w9WgXcQ?t=42").unwrap().start_seconds, Some(42.0));
    }

    #[test]
    fn words_are_searches() {
        assert!(!looks_like_link("taylorswift"));
        assert!(!looks_like_link("minecraft12"));
        assert!(looks_like_link("dQw4w9WgXcQ"));
        assert!(looks_like_link("https://www.youtube.com/playlist?list=PL1234567890ab"));
    }

    #[test]
    fn incoming_links_are_rebuilt_from_ids() {
        assert_eq!(
            normalize_incoming("timeplayer://open?url=https%3A%2F%2Fyoutu.be%2FdQw4w9WgXcQ%3Ft%3D10").as_deref(),
            Some("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10s")
        );
        assert_eq!(
            normalize_incoming("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=x").as_deref(),
            Some("https://music.youtube.com/watch?v=dQw4w9WgXcQ")
        );
        assert_eq!(
            normalize_incoming("timeplayer:https:/youtu.be/dQw4w9WgXcQ").as_deref(),
            Some("https://www.youtube.com/watch?v=dQw4w9WgXcQ")
        );
        assert_eq!(normalize_incoming("https://example.com/"), None);
        assert_eq!(to_protocol_link("https://youtu.be/x"), "timeplayer://open?url=https%3A%2F%2Fyoutu%2Ebe%2Fx");
    }
}
