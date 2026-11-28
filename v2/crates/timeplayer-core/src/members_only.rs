//! Videos YouTube keeps for a channel's paying members. Lists mark them when YouTube's badge says so, and
//! TimePlayer remembers the ones YouTube refused as members-only, so lists without the badge (the channel
//! feeds) can say so too and playing one doesn't run through every source first. Kept in the library folder.

use std::collections::VecDeque;
use std::sync::{Mutex, OnceLock};

/// Enough for anyone's subscriptions; the oldest are forgotten first.
const MAX_REMEMBERED: usize = 2000;
const FILE_NAME: &str = "members-only.json";

fn known() -> &'static Mutex<VecDeque<String>> {
    static KNOWN: OnceLock<Mutex<VecDeque<String>>> = OnceLock::new();
    KNOWN.get_or_init(|| {
        let path = crate::paths::library_folder().join(FILE_NAME);
        let ids: Vec<String> = std::fs::read(&path).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default();
        Mutex::new(ids.into_iter().filter(|id| crate::links::is_video_id(id)).collect())
    })
}

/// Whether the video is known to be for members only.
pub fn is_members_only(video_id: &str) -> bool {
    known().lock().unwrap().iter().any(|id| id == video_id)
}

/// Remembers a members-only video (from YouTube's badge, or YouTube refusing it).
pub fn remember(video_id: &str) {
    if !crate::links::is_video_id(video_id) {
        return;
    }
    let mut ids = known().lock().unwrap();
    if ids.iter().any(|id| id == video_id) {
        return;
    }
    ids.push_back(video_id.to_owned());
    while ids.len() > MAX_REMEMBERED {
        ids.pop_front();
    }
    let list: Vec<&String> = ids.iter().collect();
    let path = crate::paths::library_folder().join(FILE_NAME);
    if let Err(e) = serde_json::to_vec(&list).map_err(std::io::Error::other).and_then(|json| crate::paths::write_atomic(&path, &json)) {
        log::warn!("Could not save the list of members-only videos: {e}");
    }
}

/// A YouTube refusal that means "members only" ("Join this channel to get access to members-only content").
pub fn is_members_only_reason(reason: &str) -> bool {
    let reason = reason.to_lowercase();
    reason.contains("members-only") || reason.contains("members only") || reason.contains("join this channel")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn recognises_youtubes_members_only_refusals() {
        assert!(is_members_only_reason("Join this channel from your computer or mobile app to get access to members-only content like this video."));
        assert!(is_members_only_reason("Join this channel to get access to members-only content like this video, and other exclusive perks."));
        assert!(!is_members_only_reason("Sign in to confirm you're not a bot"));
        assert!(!is_members_only_reason("Video unavailable"));
    }
}
