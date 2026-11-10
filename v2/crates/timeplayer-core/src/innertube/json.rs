//! Shape-tolerant readers for InnerTube responses (1.x's `InnerTubeJson`). YouTube moves renderers around
//! often, so these search the tree for what they need instead of following one exact path.

use regex::Regex;
use serde_json::Value;
use std::collections::{HashMap, HashSet};
use std::sync::OnceLock;
use std::time::Duration;

use crate::models::{Comment, CommentPage, CommentSource, ItemKind, PlaylistPage, SearchResult, WatchNext};

macro_rules! re {
    ($pattern:expr) => {{
        static RE: OnceLock<Regex> = OnceLock::new();
        RE.get_or_init(|| Regex::new($pattern).unwrap())
    }};
}

/// Every value named `name`, depth first, in document order.
pub fn find_all<'a>(value: &'a Value, name: &str) -> Vec<&'a Value> {
    let mut found = Vec::new();
    walk(value, name, &mut found);
    found
}

fn walk<'a>(value: &'a Value, name: &str, found: &mut Vec<&'a Value>) {
    match value {
        Value::Object(map) => {
            for (key, child) in map {
                if key == name {
                    found.push(child);
                }
                walk(child, name, found);
            }
        }
        Value::Array(items) => {
            for item in items {
                walk(item, name, found);
            }
        }
        _ => {}
    }
}

pub fn path<'a>(value: &'a Value, keys: &[&str]) -> Option<&'a Value> {
    let mut current = value;
    for key in keys {
        current = current.as_object()?.get(*key)?;
    }
    Some(current)
}

pub fn str_at<'a>(value: &'a Value, keys: &[&str]) -> Option<&'a str> {
    path(value, keys)?.as_str()
}

pub fn string_at(value: &Value, keys: &[&str]) -> Option<String> {
    str_at(value, keys).map(str::to_owned)
}

/// Text from {"simpleText"}, {"content"} or {"runs": [{"text"}]}.
pub fn text(value: Option<&Value>) -> Option<String> {
    let obj = value?.as_object()?;
    if let Some(s) = obj.get("simpleText").and_then(Value::as_str) {
        return Some(s.to_owned());
    }
    if let Some(s) = obj.get("content").and_then(Value::as_str) {
        return Some(s.to_owned());
    }
    obj.get("runs")?.as_array().map(|runs| runs.iter().filter_map(|r| r.get("text").and_then(Value::as_str)).collect())
}

pub fn first_continuation_token(value: &Value) -> Option<String> {
    find_all(value, "continuationCommand").into_iter().filter_map(|c| str_at(c, &["token"])).find(|t| !t.is_empty()).map(str::to_owned)
}

pub fn last_continuation_token(value: &Value) -> Option<String> {
    find_all(value, "continuationCommand").into_iter().filter_map(|c| str_at(c, &["token"])).filter(|t| !t.is_empty()).next_back().map(str::to_owned)
}

/// The token for the next page of a list: the one in its own "continuation item" at the end. Search
/// pages also carry tokens on their filter chips, which load a differently filtered list.
pub fn list_continuation_token(value: &Value) -> Option<String> {
    find_all(value, "continuationItemRenderer")
        .into_iter()
        .chain(find_all(value, "continuationItemViewModel"))
        .find_map(first_continuation_token)
        .or_else(|| last_continuation_token(value))
}

/// "4:13" or "1:02:03".
pub fn parse_clock(text: Option<&str>) -> Option<Duration> {
    let parts: Vec<u64> = text?.trim().split(':').map(|p| p.parse().ok()).collect::<Option<_>>()?;
    match parts.as_slice() {
        [m, s] => Some(Duration::from_secs(m * 60 + s)),
        [h, m, s] => Some(Duration::from_secs(h * 3600 + m * 60 + s)),
        _ => None,
    }
}

/// "1.2M views", "4M", "889,722 views" → a count.
pub fn parse_count(text: Option<&str>) -> Option<i64> {
    let cleaned = text?.replace(',', "");
    let caps = re!(r"(?i)(\d+(?:\.\d+)?)\s*([KMB])?\b").captures(&cleaned)?;
    let value: f64 = caps[1].parse().ok()?;
    let multiplier = match caps.get(2).map(|m| m.as_str().to_uppercase()) {
        Some(ref k) if k == "K" => 1e3,
        Some(ref m) if m == "M" => 1e6,
        Some(ref b) if b == "B" => 1e9,
        _ => 1.0,
    };
    Some((value * multiplier) as i64)
}

/// "104K likes" → "104 thousand likes"; screen readers spell out "K" otherwise.
pub fn expand_counts(text: &str) -> String {
    re!(r"(\d+(?:\.\d+)?)([KMB])([^A-Za-z]|$)")
        .replace_all(text, |c: &regex::Captures| {
            let word = match &c[2] {
                "K" => " thousand",
                "M" => " million",
                _ => " billion",
            };
            format!("{}{word}{}", &c[1], &c[3])
        })
        .into_owned()
}

/// "2w ago" → "2 weeks ago".
pub fn expand_age(text: Option<String>) -> Option<String> {
    let text = text?;
    if text.trim().is_empty() {
        return Some(text);
    }
    Some(
        re!(r"(?i)\b(\d+)\s*(y|mo|w|d|h|m|s)\s+ago\b")
            .replace_all(&text, |c: &regex::Captures| {
                let n: i64 = c[1].parse().unwrap_or(0);
                let unit = match c[2].to_lowercase().as_str() {
                    "y" => "year",
                    "mo" => "month",
                    "w" => "week",
                    "d" => "day",
                    "h" => "hour",
                    "m" => "minute",
                    _ => "second",
                };
                if n == 1 { format!("1 {unit} ago") } else { format!("{n} {unit}s ago") }
            })
            .into_owned(),
    )
}

fn first_channel_id(value: &Value) -> Option<String> {
    find_all(value, "browseId").into_iter().filter_map(Value::as_str).find(|id| id.starts_with("UC")).map(str::to_owned)
}

fn thumbnail(id: &str) -> String {
    format!("https://i.ytimg.com/vi/{id}/hqdefault.jpg")
}

/// videoRenderer, compactVideoRenderer, gridVideoRenderer, playlistVideoRenderer.
pub fn parse_video_renderer(vr: &Value) -> Option<SearchResult> {
    let id = str_at(vr, &["videoId"]).filter(|s| !s.trim().is_empty())?.to_owned();
    let title = text(path(vr, &["title"])).unwrap_or_else(|| "Untitled video".into());
    let owner = path(vr, &["ownerText"]).or_else(|| path(vr, &["shortBylineText"])).or_else(|| path(vr, &["longBylineText"]));
    let channel = text(owner).unwrap_or_else(|| "Unknown channel".into());
    let channel_id = owner.and_then(first_channel_id);
    let length = text(path(vr, &["lengthText"]));
    let views = text(path(vr, &["viewCountText"]));
    let is_live = find_all(vr, "style").into_iter().any(|s| matches!(s.as_str(), Some("BADGE_STYLE_TYPE_LIVE_NOW" | "LIVE")))
        || views.as_deref().is_some_and(|v| v.to_lowercase().contains("watching"));
    let thumb = path(vr, &["thumbnail", "thumbnails"])
        .and_then(Value::as_array)
        .and_then(|t| t.last())
        .and_then(|t| str_at(t, &["url"]))
        .map(str::to_owned)
        .unwrap_or_else(|| thumbnail(&id));
    let snippet = path(vr, &["detailedMetadataSnippets"])
        .and_then(Value::as_array)
        .and_then(|s| s.first())
        .and_then(|s| text(path(s, &["snippetText"])));
    Some(SearchResult {
        duration: if is_live { None } else { parse_clock(length.as_deref()) },
        view_count: if is_live { None } else { parse_count(views.as_deref()) },
        is_live,
        upload_date_text: expand_age(text(path(vr, &["publishedTimeText"]))),
        thumbnail_url: Some(thumb),
        description_snippet: snippet,
        channel_id,
        ..SearchResult::video(&id, &title, &channel)
    })
}

fn metadata_rows(meta: &Value) -> Vec<Vec<String>> {
    path(meta, &["metadata", "contentMetadataViewModel", "metadataRows"])
        .and_then(Value::as_array)
        .map(|rows| {
            rows.iter()
                .map(|row| {
                    path(row, &["metadataParts"])
                        .and_then(Value::as_array)
                        .map(|parts| parts.iter().filter_map(|p| str_at(p, &["text", "content"])).filter(|t| !t.is_empty()).map(str::to_owned).collect())
                        .unwrap_or_default()
                })
                .collect()
        })
        .unwrap_or_default()
}

fn badge_text(lockup: &Value) -> Option<String> {
    find_all(lockup, "thumbnailBadgeViewModel").into_iter().find_map(|b| string_at(b, &["text"]))
}

/// The 2024+ "lockup" format (related videos, search, channel pages).
pub fn parse_lockup(lockup: &Value) -> Option<SearchResult> {
    if str_at(lockup, &["contentType"]) != Some("LOCKUP_CONTENT_TYPE_VIDEO") {
        return None;
    }
    let id = str_at(lockup, &["contentId"]).filter(|s| !s.trim().is_empty())?.to_owned();
    let meta = path(lockup, &["metadata", "lockupMetadataViewModel"])?;
    let title = str_at(meta, &["title", "content"]).unwrap_or("Untitled video").to_owned();
    let rows = metadata_rows(meta);
    let channel = rows.first().and_then(|r| r.first()).cloned().unwrap_or_else(|| "Unknown channel".into());
    let stats = rows.get(1).cloned().unwrap_or_default();
    let badge = badge_text(lockup);
    let is_live = badge.as_deref().is_some_and(|b| b.eq_ignore_ascii_case("LIVE")) || stats.iter().any(|s| s.to_lowercase().contains("watching"));
    Some(SearchResult {
        duration: if is_live { None } else { parse_clock(badge.as_deref()) },
        view_count: if is_live { None } else { parse_count(stats.first().map(String::as_str)) },
        is_live,
        upload_date_text: expand_age(stats.get(1).cloned()),
        thumbnail_url: Some(thumbnail(&id)),
        channel_id: first_channel_id(meta),
        ..SearchResult::video(&id, &title, &channel)
    })
}

/// A playlist lockup in search results or a channel's Playlists tab.
pub fn parse_playlist_lockup(lockup: &Value) -> Option<SearchResult> {
    if str_at(lockup, &["contentType"]) != Some("LOCKUP_CONTENT_TYPE_PLAYLIST") {
        return None;
    }
    let id = str_at(lockup, &["contentId"]).filter(|s| !s.trim().is_empty())?.to_owned();
    // Radio mixes (RD…) are made per viewer and can't be browsed as a list.
    if id.starts_with("RD") && !id.starts_with("RDCLAK") {
        return None;
    }
    let meta = path(lockup, &["metadata", "lockupMetadataViewModel"])?;
    let owner = metadata_rows(meta).first().and_then(|r| r.first()).cloned();
    let count = badge_text(lockup).and_then(|t| parse_count(Some(&t)));
    Some(SearchResult {
        kind: ItemKind::Playlist,
        id,
        title: str_at(meta, &["title", "content"]).unwrap_or("Untitled playlist").to_owned(),
        channel_title: match owner.as_deref() {
            None | Some("Playlist") => "YouTube".into(),
            Some(o) => o.to_owned(),
        },
        channel_id: first_channel_id(meta),
        playlist_video_count: count,
        ..Default::default()
    })
}

/// A Short in a Shorts tab or Shorts search: the ID in its tap command, title and views in the overlay.
pub fn parse_shorts_lockup(lockup: &Value) -> Option<SearchResult> {
    let id = find_all(lockup, "reelWatchEndpoint")
        .into_iter()
        .find_map(|e| str_at(e, &["videoId"]).filter(|s| !s.is_empty()))
        .map(str::to_owned)
        .or_else(|| str_at(lockup, &["entityId"]).and_then(|e| e.strip_prefix("shorts-shelf-item-")).map(str::to_owned))
        .filter(|s| !s.is_empty())?;
    let mut title = string_at(lockup, &["overlayMetadata", "primaryText", "content"]).unwrap_or_default();
    let views = str_at(lockup, &["overlayMetadata", "secondaryText", "content"]);
    if title.trim().is_empty() {
        // "Title, 45 thousand views - play Short"
        let spoken = str_at(lockup, &["accessibilityText"]).unwrap_or("");
        title = match spoken.rfind(", ") {
            Some(cut) if cut > 0 => spoken[..cut].to_owned(),
            _ => spoken.to_owned(),
        };
    }
    Some(SearchResult {
        is_short: true,
        view_count: parse_count(views),
        thumbnail_url: Some(thumbnail(&id)),
        ..SearchResult::video(&id, if title.trim().is_empty() { "Untitled Short" } else { title.trim() }, "")
    })
}

/// Videos, Shorts and playlists on a page, in order, with the token for the next page.
pub fn parse_items_page(root: &Value) -> (Vec<SearchResult>, Option<String>) {
    let mut items = Vec::new();
    let mut seen = HashSet::new();
    for short in find_all(root, "shortsLockupViewModel") {
        if let Some(s) = parse_shorts_lockup(short).filter(|s| seen.insert(s.id.clone())) {
            items.push(s);
        }
    }
    for lockup in find_all(root, "lockupViewModel") {
        if let Some(item) = parse_lockup(lockup).or_else(|| parse_playlist_lockup(lockup)).filter(|i| seen.insert(i.id.clone())) {
            items.push(item);
        }
    }
    for renderer in find_all(root, "videoRenderer") {
        if let Some(v) = parse_video_renderer(renderer).filter(|v| seen.insert(v.id.clone())) {
            items.push(v);
        }
    }
    let next = if items.is_empty() { None } else { list_continuation_token(root) };
    (items, next)
}

/// Every video on a signed-in account page.
pub fn parse_account_videos(root: &Value) -> Vec<SearchResult> {
    let mut out: Vec<SearchResult> = find_all(root, "lockupViewModel").into_iter().filter_map(parse_lockup).collect();
    for name in ["videoRenderer", "gridVideoRenderer", "playlistVideoRenderer"] {
        out.extend(find_all(root, name).into_iter().filter_map(parse_video_renderer));
    }
    out
}

/// A page of a playlist from browse "VL{id}" or its continuation.
pub fn parse_playlist_page(root: &Value, playlist_id: &str) -> PlaylistPage {
    let mut videos = Vec::new();
    let mut seen = HashSet::new();
    for lockup in find_all(root, "lockupViewModel") {
        if let Some(v) = parse_lockup(lockup).filter(|v| seen.insert(v.id.clone())) {
            videos.push(v);
        }
    }
    for renderer in find_all(root, "playlistVideoRenderer") {
        if let Some(v) = parse_video_renderer(renderer).filter(|v| seen.insert(v.id.clone())) {
            videos.push(v);
        }
    }
    let title = find_all(root, "playlistMetadataRenderer")
        .into_iter()
        .find_map(|p| string_at(p, &["title"]))
        .or_else(|| find_all(root, "pageHeaderViewModel").into_iter().find_map(|p| string_at(p, &["title", "dynamicTextViewModel", "text", "content"])))
        .unwrap_or_default();
    let owner = find_all(root, "pageHeaderViewModel")
        .into_iter()
        .find_map(|h| find_all(h, "avatarStackViewModel").into_iter().find_map(|a| string_at(a, &["text", "content"])))
        .map(|o| if o.to_lowercase().starts_with("by ") { o[3..].trim().to_owned() } else { o });
    let next_token = if videos.is_empty() { None } else { list_continuation_token(root) };
    PlaylistPage { playlist_id: playlist_id.into(), title, owner, videos, next_token }
}

/// Related videos, the comments token and the description from a "next" response.
pub fn parse_watch_next(root: &Value) -> WatchNext {
    let secondary = path(root, &["contents", "twoColumnWatchNextResults", "secondaryResults"]).unwrap_or(root);
    let mut related = Vec::new();
    let mut seen = HashSet::new();
    for lockup in find_all(secondary, "lockupViewModel") {
        if let Some(v) = parse_lockup(lockup).filter(|v| seen.insert(v.id.clone())) {
            related.push(v);
        }
    }
    for compact in find_all(secondary, "compactVideoRenderer") {
        if let Some(v) = parse_video_renderer(compact).filter(|v| seen.insert(v.id.clone())) {
            related.push(v);
        }
    }
    let comments_token = find_all(root, "itemSectionRenderer")
        .into_iter()
        .find(|s| str_at(s, &["sectionIdentifier"]) == Some("comment-item-section"))
        .and_then(first_continuation_token);
    let description = find_all(root, "attributedDescription")
        .into_iter()
        .find_map(|d| string_at(d, &["content"]))
        .or_else(|| find_all(root, "videoSecondaryInfoRenderer").into_iter().find_map(|v| text(path(v, &["description"]))));
    WatchNext { related, comments_token, description }
}

/// Comments come as view models (order, reply tokens) pointing at entity payloads (text, author, likes).
pub fn parse_comments(root: &Value, is_reply_page: bool) -> CommentPage {
    let entities: HashMap<String, &Value> =
        find_all(root, "commentEntityPayload").into_iter().filter_map(|p| Some((string_at(p, &["key"])?, p))).collect();
    let mut comments = Vec::new();
    let mut next_token = None;
    for list in find_all(root, "continuationItems") {
        let Some(items) = list.as_array() else { continue };
        for item in items {
            if let Some(thread) = path(item, &["commentThreadRenderer"]) {
                let replies = path(thread, &["replies"]).and_then(first_continuation_token);
                if let Some(c) = path(thread, &["commentViewModel", "commentViewModel"]).and_then(|vm| to_comment(vm, &entities, replies, false)) {
                    comments.push(c);
                }
            } else if let Some(vm) = path(item, &["commentViewModel"]) {
                if let Some(c) = to_comment(vm, &entities, None, is_reply_page) {
                    comments.push(c);
                }
            } else if let Some(more) = path(item, &["continuationItemRenderer"]) {
                next_token = first_continuation_token(more).or(next_token);
            }
        }
    }
    CommentPage { comments, next_token, source: CommentSource::InnerTube }
}

fn to_comment(vm: &Value, entities: &HashMap<String, &Value>, replies_token: Option<String>, is_reply: bool) -> Option<Comment> {
    let key = str_at(vm, &["commentKey"])?;
    let entity = *entities.get(key)?;
    let text = str_at(entity, &["properties", "content", "content"]).filter(|t| !t.trim().is_empty())?;
    let likes = string_at(entity, &["toolbar", "likeCountA11y"]);
    Some(Comment {
        id: string_at(entity, &["properties", "commentId"]).unwrap_or_else(|| key.to_owned()),
        author: string_at(entity, &["author", "displayName"]).unwrap_or_else(|| "Unknown".into()),
        author_channel_id: string_at(entity, &["author", "channelId"]),
        text: text.trim().to_owned(),
        published_text: expand_age(string_at(entity, &["properties", "publishedTime"])),
        likes_text: likes.filter(|l| !l.trim().is_empty() && !l.starts_with("0 ")).map(|l| expand_counts(&l)),
        reply_count: parse_count(str_at(entity, &["toolbar", "replyCount"])).unwrap_or(0),
        creator: path(entity, &["author", "isCreator"]).and_then(Value::as_bool).unwrap_or(false),
        pinned: path(vm, &["pinnedText"]).is_some() || str_at(entity, &["properties", "pinnedText"]).is_some(),
        is_reply: is_reply || path(entity, &["properties", "replyLevel"]).and_then(Value::as_i64).is_some_and(|l| l > 0),
        replies_token,
        source: CommentSource::InnerTube,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn reads_counts_ages_and_clocks() {
        assert_eq!(parse_count(Some("1.2M views")), Some(1_200_000));
        assert_eq!(parse_count(Some("889,722 views")), Some(889_722));
        assert_eq!(parse_clock(Some("1:02:03")), Some(Duration::from_secs(3723)));
        assert_eq!(expand_counts("104K likes"), "104 thousand likes");
        assert_eq!(expand_age(Some("2w ago".into())).unwrap(), "2 weeks ago");
    }

    #[test]
    fn reads_a_short() {
        let short = json!({"shortsLockupViewModel":{"entityId":"shorts-shelf-item-E_MGy41IYVw",
            "onTap":{"innertubeCommand":{"reelWatchEndpoint":{"videoId":"E_MGy41IYVw"}}},
            "overlayMetadata":{"primaryText":{"content":"Still hits"},"secondaryText":{"content":"45K views"}}}});
        let (items, _) = parse_items_page(&short);
        assert_eq!(items[0].title, "Still hits");
        assert_eq!(items[0].view_count, Some(45_000));
        assert!(items[0].is_short);
    }

    #[test]
    fn reads_a_video_renderer() {
        let vr = json!({"videoId":"dQw4w9WgXcQ","title":{"runs":[{"text":"Song"}]},
            "ownerText":{"runs":[{"text":"Rick","navigationEndpoint":{"browseEndpoint":{"browseId":"UCuAXFkgsw1L7xaCfnd5JJOw"}}}]},
            "lengthText":{"simpleText":"3:33"},"viewCountText":{"simpleText":"1,000 views"},"publishedTimeText":{"simpleText":"1y ago"}});
        let r = parse_video_renderer(&vr).unwrap();
        assert_eq!(r.channel_id.as_deref(), Some("UCuAXFkgsw1L7xaCfnd5JJOw"));
        assert_eq!(r.duration, Some(Duration::from_secs(213)));
        assert_eq!(r.upload_date_text.as_deref(), Some("1 year ago"));
    }
}
