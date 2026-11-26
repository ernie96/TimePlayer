//! Stream addresses from YouTube's own apps (Apple Vision Pro, iPhone, Android VR). YouTube gives these
//! apps plain googlevideo addresses, so nothing has to be unscrambled and no JavaScript runs: one request,
//! usually well under a second. The approach, client details, track ranking and cache rules come from
//! lib_YTPYP by Aman Kumar Gupta (MIT License, https://github.com/amanKG777/lib_YTPYP).

use serde_json::{Value, json};
use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use crate::config::{AudioTrackPreference, QualityPreference};
use crate::error::{AppError, Result};
use crate::http::{Http, Request};
use crate::models::{AudioTrack, CaptionTrack, ResolvedStream, SourceKind, StreamCandidate, is_descriptive_track_name};

use super::json::{path, str_at};

const VISIONOS_UA: &str = "com.google.visionos.youtube/1.02(RealityDevice14,1; U; CPU visionOS 25_6_0 like Mac OS X; GB)";
const IOS_UA: &str = "com.google.ios.youtube/21.03.2 (iPhone16,2; U; CPU iOS 18_7_2 like Mac OS X; en_US)";
const DESKTOP_UA: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
const API_PLAYER: &str = "https://youtubei.googleapis.com/youtubei/v1/player?prettyPrint=false";
const ANDROID_VR_PLAYER: &str = "https://www.youtube.com/youtubei/v1/player?key=AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
const VISITOR_ID: &str = "https://www.youtube.com/youtubei/v1/visitor_id?prettyPrint=false";

/// lib_YTPYP's copyright and permission notice, which the MIT License asks copies to carry (shown last in
/// About TimePlayer).
pub const LIB_YTPYP_LICENSE: &str = include_str!("LICENSE-lib_YTPYP.txt");

/// Per request; three apps and the visitor session stay well under the time other sources get.
const REQUEST_TIMEOUT: Duration = Duration::from_secs(6);
/// The anonymous visitor session a freshly installed app gets, reused for a day.
const VISITOR_LIFETIME: Duration = Duration::from_secs(24 * 3600);
/// Never trust a cached address longer than this, whatever its expiry says.
const MAX_CACHE_AGE: Duration = Duration::from_secs(3 * 3600);
/// An address counts as expired this long before the time in its `expire=`.
const EXPIRY_MARGIN_SECONDS: i64 = 10 * 60;
const MAX_CACHE_ENTRIES: usize = 256;

/// What one of YouTube's apps was told about a video.
#[derive(Debug, Clone)]
pub struct PlayerInfo {
    pub video_id: String,
    pub title: String,
    pub channel: String,
    pub channel_id: String,
    pub duration_seconds: u64,
    pub is_live: bool,
    /// For live streams: the HLS playlist.
    pub hls_url: Option<String>,
    pub streams: Vec<StreamCandidate>,
    pub captions: Vec<CaptionTrack>,
    /// Which app's answer this is ("VISIONOS", "IOS", "ANDROID_VR").
    pub client: &'static str,
    fetched_at: Instant,
    expires_unix: Option<i64>,
}

impl PlayerInfo {
    fn is_fresh(&self, now_unix: i64) -> bool {
        self.fetched_at.elapsed() < MAX_CACHE_AGE && self.expires_unix.is_none_or(|e| now_unix < e - EXPIRY_MARGIN_SECONDS)
    }

    /// Each sound track once, the original first, then the others as YouTube lists them.
    pub fn audio_tracks(&self) -> Vec<AudioTrack> {
        let mut tracks: Vec<AudioTrack> = Vec::new();
        for s in self.streams.iter().filter(|s| s.audio_only) {
            let track = s.audio_track();
            if !tracks.iter().any(|t| t.key == track.key) {
                tracks.push(track);
            }
        }
        tracks.sort_by_key(|t| (t.audio_description, !t.original, t.stable_volume));
        tracks
    }

    pub fn has_audio_description(&self) -> bool {
        self.streams.iter().any(|s| s.audio_only && s.audio_description)
    }
}

/// How to choose among a video's streams.
#[derive(Debug, Clone, PartialEq)]
pub struct StreamChoice {
    pub quality: QualityPreference,
    pub audio_only: bool,
    pub audio_description: bool,
    pub track_preference: AudioTrackPreference,
    pub stable_volume: bool,
    /// TimePlayer's language ("es"), for [`AudioTrackPreference::MyLanguage`].
    pub language: String,
    /// A track picked from the Audio track menu for this video; it wins over everything else.
    pub track_key: Option<String>,
}

impl Default for StreamChoice {
    fn default() -> Self {
        Self {
            quality: QualityPreference::Auto,
            audio_only: false,
            audio_description: false,
            track_preference: AudioTrackPreference::Original,
            stable_volume: false,
            language: "en".into(),
            track_key: None,
        }
    }
}

/// The visitor session and the addresses found, shared by every copy of [`super::InnerTube`].
#[derive(Default)]
pub(crate) struct AppPlayerState {
    visitor: Mutex<Option<(String, Instant)>>,
    cache: Mutex<HashMap<String, Arc<PlayerInfo>>>,
    /// One lookup at a time, so the audio-description check and playing a video share one request.
    fetching: Mutex<()>,
}

pub(crate) fn cached(state: &AppPlayerState, video_id: &str) -> Option<Arc<PlayerInfo>> {
    state.cache.lock().unwrap().get(video_id).cloned()
}

pub(crate) fn forget(state: &AppPlayerState, video_id: &str) {
    state.cache.lock().unwrap().remove(video_id);
}

/// The video's player answer: from the cache while its addresses are still good, otherwise from the first
/// app that answers with playable streams.
pub(crate) fn player_info(http: &Http, state: &AppPlayerState, video_id: &str) -> Result<Arc<PlayerInfo>> {
    let now = chrono::Utc::now().timestamp();
    let fresh = |state: &AppPlayerState| cached(state, video_id).filter(|i| i.is_fresh(now));
    if let Some(info) = fresh(state) {
        return Ok(info);
    }
    let _one_at_a_time = state.fetching.lock().unwrap();
    if let Some(info) = fresh(state) {
        return Ok(info);
    }
    let mut reasons = Vec::new();
    for client in [Client::VisionOs, Client::Ios, Client::AndroidVr] {
        match fetch(http, state, client, video_id) {
            Ok(info) => {
                let info = Arc::new(info);
                store(state, info.clone(), now);
                log::info!("Found {video_id}'s streams with YouTube's {} app.", client.name());
                return Ok(info);
            }
            Err(e) => {
                log::warn!("YouTube's {} app could not get {video_id}: {}", client.name(), e.details.as_deref().unwrap_or(&e.message));
                reasons.push(e);
            }
        }
    }
    // A video YouTube calls unavailable says so to every app; keep that reason for the message.
    let unavailable = reasons.iter().find(|e| e.code == "VIDEO_UNAVAILABLE").cloned();
    Err(unavailable.unwrap_or_else(|| {
        let detail = reasons.iter().map(|e| e.details.clone().unwrap_or_else(|| e.message.clone())).collect::<Vec<_>>().join("; ");
        AppError::stream_unavailable(video_id, Some(&detail))
    }))
}

fn store(state: &AppPlayerState, info: Arc<PlayerInfo>, now_unix: i64) {
    let mut cache = state.cache.lock().unwrap();
    cache.retain(|_, i| i.is_fresh(now_unix));
    if cache.len() >= MAX_CACHE_ENTRIES
        && let Some(oldest) = cache.iter().min_by_key(|(_, i)| i.fetched_at).map(|(k, _)| k.clone())
    {
        cache.remove(&oldest);
    }
    cache.insert(info.video_id.clone(), info);
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Client {
    VisionOs,
    Ios,
    AndroidVr,
}

impl Client {
    fn name(self) -> &'static str {
        match self {
            Self::VisionOs => "VISIONOS",
            Self::Ios => "IOS",
            Self::AndroidVr => "ANDROID_VR",
        }
    }
}

fn visionos_context() -> Value {
    json!({
        "clientName": "VISIONOS", "clientVersion": "1.02", "clientScreen": "WATCH", "platform": "MOBILE",
        "deviceMake": "Apple", "deviceModel": "RealityDevice14,1", "osName": "visionOS", "osVersion": "25.6.0.23O471",
        "hl": "en-GB", "gl": "GB", "utcOffsetMinutes": 0
    })
}

/// The anonymous visitor session, as a new app install gets one. Without it the request is still tried.
fn visitor_data(http: &Http, state: &AppPlayerState) -> Option<String> {
    if let Some((data, at)) = state.visitor.lock().unwrap().as_ref()
        && at.elapsed() < VISITOR_LIFETIME
    {
        return Some(data.clone());
    }
    let request = Request::post_json(VISITOR_ID, &json!({ "context": { "client": visionos_context() } }))
        .timeout(REQUEST_TIMEOUT)
        .header("User-Agent", VISIONOS_UA);
    let data = http.send(request).ok().filter(|r| r.ok()).and_then(|r| r.json().ok()).and_then(|j| str_at(&j, &["responseContext", "visitorData"]).map(str::to_owned));
    match &data {
        Some(d) => *state.visitor.lock().unwrap() = Some((d.clone(), Instant::now())),
        None => log::warn!("YouTube gave no visitor session; asking without one."),
    }
    data
}

fn fetch(http: &Http, state: &AppPlayerState, client: Client, video_id: &str) -> Result<PlayerInfo> {
    let nonce = super::client_playback_nonce();
    let (url, body, user_agent, nonce) = match client {
        Client::VisionOs => {
            let mut context = visionos_context();
            if let Some(visitor) = visitor_data(http, state) {
                context["visitorData"] = Value::String(visitor);
            }
            let body = json!({
                "context": { "client": context, "request": { "internalExperimentFlags": [], "useSsl": true }, "user": { "lockedSafetyMode": false } },
                "videoId": video_id, "cpn": nonce, "contentCheckOk": true, "racyCheckOk": true
            });
            let url = format!("{API_PLAYER}&t={}&id={video_id}", &super::client_playback_nonce()[..12]);
            (url, body, VISIONOS_UA, Some(nonce))
        }
        Client::Ios => {
            let body = json!({
                "context": { "client": {
                    "clientName": "IOS", "clientVersion": "21.03.2", "deviceMake": "Apple", "deviceModel": "iPhone16,2",
                    "osName": "iOS", "osVersion": "18.7.2.22H124", "hl": "en", "gl": "US"
                } },
                "videoId": video_id, "cpn": nonce, "contentCheckOk": true, "racyCheckOk": true
            });
            (API_PLAYER.to_owned(), body, IOS_UA, Some(nonce))
        }
        Client::AndroidVr => {
            let body = json!({
                "context": { "client": { "clientName": "ANDROID_VR", "clientVersion": "1.56.21", "hl": "en", "gl": "US" } },
                "videoId": video_id, "contentCheckOk": true, "racyCheckOk": true
            });
            (ANDROID_VR_PLAYER.to_owned(), body, DESKTOP_UA, None)
        }
    };
    let mut request = Request::post_json(url, &body).timeout(REQUEST_TIMEOUT).header("User-Agent", user_agent);
    if client != Client::AndroidVr {
        request = request.header("X-Goog-Api-Format-Version", "2");
    }
    let response = http.send(request)?;
    if !response.ok() {
        return Err(AppError::stream_unavailable(video_id, Some(&format!("{} answered HTTP {}", client.name(), response.status))));
    }
    parse_player(video_id, client, nonce.as_deref(), &response.json()?)
}

/// Reads a player answer: the details, every stream with a plain address, and the captions.
fn parse_player(video_id: &str, client: Client, nonce: Option<&str>, root: &Value) -> Result<PlayerInfo> {
    let status = str_at(root, &["playabilityStatus", "status"]).unwrap_or("UNKNOWN");
    if status != "OK" {
        let reason = str_at(root, &["playabilityStatus", "reason"]).unwrap_or("Video unplayable");
        let detail = format!("{}: {status} ({reason})", client.name());
        // "Sign in to confirm you're not a bot" is about this app, not the video; the next app may get it.
        let mut e = AppError::stream_unavailable(video_id, Some(&detail));
        if status == "ERROR" || status == "UNPLAYABLE" && !reason.to_lowercase().contains("bot") {
            e.code = "VIDEO_UNAVAILABLE".into();
        }
        return Err(e);
    }
    let details = path(root, &["videoDetails"]).cloned().unwrap_or(Value::Null);
    if let Some(returned) = str_at(&details, &["videoId"])
        && returned != video_id
    {
        return Err(AppError::stream_unavailable(video_id, Some(&format!("{} answered with another video ({returned})", client.name()))));
    }
    let streams: Vec<StreamCandidate> = path(root, &["streamingData", "adaptiveFormats"])
        .and_then(Value::as_array)
        .map(|formats| formats.iter().filter_map(|f| candidate(f, nonce)).collect())
        .unwrap_or_default();
    let is_live = details.get("isLive").and_then(Value::as_bool).unwrap_or(false) || details.get("isLiveContent").and_then(Value::as_bool).unwrap_or(false) && streams.is_empty();
    let hls_url = str_at(root, &["streamingData", "hlsManifestUrl"]).map(str::to_owned);
    let playable = streams.iter().any(|s| s.audio_only) || is_live && hls_url.is_some();
    if !playable {
        let scrambled = path(root, &["streamingData", "adaptiveFormats"]).and_then(Value::as_array).is_some_and(|f| f.iter().any(|f| f.get("signatureCipher").is_some()));
        let why = if scrambled { "only scrambled stream addresses" } else { "no playable audio stream" };
        return Err(AppError::stream_unavailable(video_id, Some(&format!("{} gave {why}", client.name()))));
    }
    let expires_unix = streams.iter().filter_map(|s| query_param(&s.url, "expire")).filter_map(|e| e.parse::<i64>().ok()).min();
    Ok(PlayerInfo {
        video_id: video_id.into(),
        title: str_at(&details, &["title"]).unwrap_or_default().into(),
        channel: str_at(&details, &["author"]).unwrap_or_default().into(),
        channel_id: str_at(&details, &["channelId"]).unwrap_or_default().into(),
        duration_seconds: str_at(&details, &["lengthSeconds"]).and_then(|s| s.parse().ok()).unwrap_or(0),
        is_live,
        hls_url,
        streams,
        captions: super::captions(root),
        client: client.name(),
        fetched_at: Instant::now(),
        expires_unix,
    })
}

/// One adaptive format with a plain address, the nonce added (without it googlevideo slows the stream
/// down or cuts it off partway).
fn candidate(f: &Value, nonce: Option<&str>) -> Option<StreamCandidate> {
    let mut url = str_at(f, &["url"]).filter(|u| !u.is_empty())?.to_owned();
    if let Some(nonce) = nonce
        && query_param(&url, "cpn").is_none()
    {
        url.push(if url.contains('?') { '&' } else { '?' });
        url.push_str("cpn=");
        url.push_str(nonce);
    }
    let mime = str_at(f, &["mimeType"]).unwrap_or("");
    let (kind, rest) = mime.split_once('/')?;
    let container = rest.split(';').next().unwrap_or("").trim();
    let codec = mime.split("codecs=\"").nth(1).and_then(|c| c.split('"').next()).map(str::to_owned);
    let bitrate = f.get("bitrate").and_then(Value::as_i64);
    let content_length = str_at(f, &["contentLength"]).and_then(|c| c.parse().ok());
    match kind {
        "audio" => {
            let name = str_at(f, &["audioTrack", "displayName"]).map(str::to_owned);
            Some(StreamCandidate {
                url,
                format: if container == "mp4" { "m4a".into() } else { container.into() },
                bitrate,
                audio_bitrate: bitrate,
                audio_codec: codec,
                audio_only: true,
                content_length,
                audio_description: is_descriptive_track_name(name.as_deref()),
                audio_track_name: name,
                audio_track_id: str_at(f, &["audioTrack", "id"]).map(str::to_owned),
                audio_is_default: f.get("audioTrack").and_then(|t| t.get("audioIsDefault")).and_then(Value::as_bool).unwrap_or(false),
                stable_volume: f.get("isDrc").and_then(Value::as_bool).unwrap_or(false),
                ..Default::default()
            })
        }
        "video" => {
            let height = f.get("height").and_then(Value::as_u64).map(|h| h as u32);
            Some(StreamCandidate {
                url,
                format: container.into(),
                quality_label: str_at(f, &["qualityLabel"]).map(str::to_owned),
                height,
                width: f.get("width").and_then(Value::as_u64).map(|w| w as u32),
                fps: f.get("fps").and_then(Value::as_u64).map(|v| v as u32),
                bitrate,
                video_codec: codec,
                video_only: true,
                content_length,
                ..Default::default()
            })
        }
        _ => None,
    }
}

fn query_param<'a>(url: &'a str, key: &str) -> Option<&'a str> {
    let query = url.split_once('?')?.1;
    query.split('&').find_map(|pair| pair.strip_prefix(key)?.strip_prefix('='))
}

/// The sound stream to play. A track chosen from the menu wins; then audio description when it's on and
/// there; then the preferred language's track, the original over dubs, YouTube's default, the volume
/// version asked for, and the highest bitrate.
pub fn choose_audio<'a>(streams: &'a [StreamCandidate], choice: &StreamChoice) -> Option<&'a StreamCandidate> {
    let audio = || streams.iter().filter(|s| s.audio_only);
    let rate = |s: &&StreamCandidate| s.audio_bitrate.or(s.bitrate).unwrap_or(0);
    if let Some(key) = choice.track_key.as_deref()
        && let Some(s) = audio().filter(|s| s.audio_track().key == key).max_by_key(rate)
    {
        return Some(s);
    }
    let volume_fits = |s: &StreamCandidate| s.stable_volume == choice.stable_volume;
    if choice.audio_description
        && let Some(s) = audio().filter(|s| s.audio_description).max_by_key(|s| (volume_fits(s), rate(s)))
    {
        return Some(s);
    }
    let language = choice.language.to_lowercase();
    audio().filter(|s| !s.audio_description).max_by_key(|s| {
        let track = s.audio_track();
        let in_my_language = choice.track_preference == AudioTrackPreference::MyLanguage && track.language.as_deref() == Some(language.as_str());
        (in_my_language, track.original, track.youtube_default, volume_fits(s), rate(s))
    })
}

/// Higher is better: VP9 and H.264 decode easily everywhere; AV1 needs a newer graphics card to be light.
fn codec_rank(s: &StreamCandidate) -> u8 {
    match s.video_codec.as_deref().unwrap_or("") {
        c if c.starts_with("vp9") || c.starts_with("vp09") => 2,
        c if c.starts_with("avc1") => 1,
        _ => 0,
    }
}

/// The picture stream: the tallest at or under the quality setting (1080p for Automatic).
fn choose_video(streams: &[StreamCandidate], quality: QualityPreference) -> Option<&StreamCandidate> {
    let target = quality.max_height().unwrap_or(1080);
    let video = || streams.iter().filter(|s| s.video_only);
    let key = |s: &&StreamCandidate| (s.height.unwrap_or(0), codec_rank(s), s.fps.unwrap_or(0), s.bitrate.unwrap_or(0));
    video()
        .filter(|s| s.height.unwrap_or(0) <= target)
        .max_by_key(key)
        .or_else(|| video().min_by_key(|s| (s.height.unwrap_or(u32::MAX), std::cmp::Reverse(codec_rank(s)))))
}

/// What to give mpv from an app's answer.
pub fn choose_streams(info: &PlayerInfo, choice: &StreamChoice) -> Result<ResolvedStream> {
    let base = ResolvedStream {
        video_id: info.video_id.clone(),
        captions: info.captions.clone(),
        is_live: info.is_live,
        source: SourceKind::InnerTube,
        has_audio_description: info.has_audio_description(),
        ..Default::default()
    };
    if info.is_live
        && let Some(hls) = &info.hls_url
    {
        return Ok(ResolvedStream { url: hls.clone(), ..base });
    }
    let Some(audio) = choose_audio(&info.streams, choice) else {
        return Err(AppError::stream_unavailable(&info.video_id, Some("No audio stream.")));
    };
    let track = audio.audio_track();
    let uses_audio_description = audio.audio_description;
    let audio_only = choice.audio_only || choice.quality == QualityPreference::AudioOnly;
    let video = if audio_only { None } else { choose_video(&info.streams, choice.quality) };
    Ok(match video {
        Some(v) => ResolvedStream {
            url: v.url.clone(),
            audio_url: Some(audio.url.clone()),
            uses_audio_description,
            audio_track: Some(track),
            alternatives: info.streams.iter().filter(|s| s.url != v.url && s.url != audio.url).cloned().collect(),
            ..base
        },
        None => ResolvedStream { url: audio.url.clone(), uses_audio_description, audio_track: Some(track), ..base },
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn audio(id: Option<&str>, name: Option<&str>, default: bool, drc: bool, bitrate: i64) -> StreamCandidate {
        StreamCandidate {
            url: format!("https://a/{}{}{}", id.unwrap_or("none"), if drc { "drc" } else { "" }, bitrate),
            audio_only: true,
            audio_bitrate: Some(bitrate),
            audio_track_id: id.map(str::to_owned),
            audio_description: is_descriptive_track_name(name),
            audio_track_name: name.map(str::to_owned),
            audio_is_default: default,
            stable_volume: drc,
            ..Default::default()
        }
    }

    fn video(height: u32, codec: &str, bitrate: i64) -> StreamCandidate {
        StreamCandidate { url: format!("https://v/{height}{codec}"), video_only: true, height: Some(height), video_codec: Some(codec.into()), bitrate: Some(bitrate), fps: Some(30), ..Default::default() }
    }

    fn dubbed() -> Vec<StreamCandidate> {
        vec![
            audio(Some("es.3"), Some("Spanish"), true, false, 160_000),
            audio(Some("en.4"), Some("English (United States) original"), false, false, 130_000),
            audio(Some("en.4"), Some("English (United States) original"), false, true, 140_000),
            audio(Some("en-US.5"), Some("English (United States) descriptive"), false, false, 130_000),
        ]
    }

    #[test]
    fn the_original_beats_a_dub_youtube_marks_default() {
        let streams = dubbed();
        let chosen = choose_audio(&streams, &StreamChoice::default()).unwrap();
        assert_eq!(chosen.audio_track_id.as_deref(), Some("en.4"));
        assert!(!chosen.stable_volume);
    }

    #[test]
    fn stable_volume_is_taken_only_when_asked() {
        let streams = dubbed();
        let chosen = choose_audio(&streams, &StreamChoice { stable_volume: true, ..Default::default() }).unwrap();
        assert!(chosen.stable_volume && chosen.audio_track_id.as_deref() == Some("en.4"));
    }

    #[test]
    fn my_language_takes_a_dub_in_it_and_otherwise_the_original() {
        let streams = dubbed();
        let spanish = StreamChoice { track_preference: AudioTrackPreference::MyLanguage, language: "es".into(), ..Default::default() };
        assert_eq!(choose_audio(&streams, &spanish).unwrap().audio_track_id.as_deref(), Some("es.3"));
        let romanian = StreamChoice { language: "ro".into(), ..spanish };
        assert_eq!(choose_audio(&streams, &romanian).unwrap().audio_track_id.as_deref(), Some("en.4"));
    }

    #[test]
    fn audio_description_only_when_on_and_a_chosen_track_wins() {
        let streams = dubbed();
        assert!(!choose_audio(&streams, &StreamChoice::default()).unwrap().audio_description);
        assert!(choose_audio(&streams, &StreamChoice { audio_description: true, ..Default::default() }).unwrap().audio_description);
        let key = streams[0].audio_track().key;
        let picked = StreamChoice { track_key: Some(key), audio_description: true, ..Default::default() };
        assert_eq!(choose_audio(&streams, &picked).unwrap().audio_track_id.as_deref(), Some("es.3"));
    }

    #[test]
    fn a_single_unnamed_track_counts_as_the_original() {
        let streams = vec![audio(None, None, false, false, 128_000), audio(None, None, false, false, 160_000)];
        let chosen = choose_audio(&streams, &StreamChoice::default()).unwrap();
        assert_eq!(chosen.audio_bitrate, Some(160_000));
        assert!(chosen.audio_track().original);
    }

    #[test]
    fn tracks_are_listed_once_with_the_original_first() {
        let info = info_with(dubbed());
        let tracks = info.audio_tracks();
        assert_eq!(tracks.len(), 4);
        assert!(tracks[0].original && !tracks[0].stable_volume);
        assert!(tracks.last().unwrap().audio_description);
        assert_eq!(tracks[1].label(), "English (United States) original, stable volume");
    }

    fn info_with(streams: Vec<StreamCandidate>) -> PlayerInfo {
        PlayerInfo {
            video_id: "dQw4w9WgXcQ".into(),
            title: "T".into(),
            channel: "C".into(),
            channel_id: String::new(),
            duration_seconds: 10,
            is_live: false,
            hls_url: None,
            streams,
            captions: Vec::new(),
            client: "IOS",
            fetched_at: Instant::now(),
            expires_unix: None,
        }
    }

    #[test]
    fn video_is_the_tallest_within_the_quality_with_separate_sound() {
        let mut streams = dubbed();
        streams.extend([video(2160, "vp09.00.50.08", 9_000_000), video(1080, "av01.0.08M.08", 2_000_000), video(1080, "vp09.00.40.08", 2_500_000), video(720, "avc1.4d401f", 1_000_000)]);
        let info = info_with(streams);
        let auto = choose_streams(&info, &StreamChoice::default()).unwrap();
        assert_eq!(auto.url, "https://v/1080vp09.00.40.08");
        assert!(auto.audio_url.as_deref().is_some_and(|a| a.contains("en.4")));
        assert_eq!(auto.source, SourceKind::InnerTube);
        let p720 = choose_streams(&info, &StreamChoice { quality: QualityPreference::P720, ..Default::default() }).unwrap();
        assert_eq!(p720.url, "https://v/720avc1.4d401f");
        let sound = choose_streams(&info, &StreamChoice { quality: QualityPreference::AudioOnly, ..Default::default() }).unwrap();
        assert!(sound.audio_url.is_none() && sound.url.contains("en.4"));
        let small = choose_streams(&info_with(vec![dubbed()[1].clone(), video(1440, "vp09", 1)]), &StreamChoice { quality: QualityPreference::P360, ..Default::default() }).unwrap();
        assert_eq!(small.url, "https://v/1440vp09", "nothing small enough: the smallest there is");
    }

    #[test]
    fn reads_a_player_answer_and_adds_the_nonce() {
        let answer = serde_json::json!({
            "playabilityStatus": { "status": "OK" },
            "videoDetails": { "videoId": "dQw4w9WgXcQ", "title": "Song", "author": "Singer", "channelId": "UCabc", "lengthSeconds": "213" },
            "streamingData": { "adaptiveFormats": [
                { "url": "https://r1.googlevideo.com/videoplayback?expire=2000000000&itag=251", "mimeType": "audio/webm; codecs=\"opus\"", "bitrate": 150000,
                  "audioTrack": { "displayName": "English original", "id": "en.4", "audioIsDefault": true } },
                { "url": "https://r1.googlevideo.com/videoplayback?expire=2000000000&itag=248", "mimeType": "video/webm; codecs=\"vp9\"", "height": 1080, "width": 1920, "fps": 30, "bitrate": 2000000 },
                { "signatureCipher": "s=abc&url=https%3A%2F%2Fx", "mimeType": "audio/mp4; codecs=\"mp4a.40.2\"" }
            ] },
            "captions": { "playerCaptionsTracklistRenderer": { "captionTracks": [ { "baseUrl": "https://www.youtube.com/api/timedtext?v=x", "languageCode": "en", "name": { "simpleText": "English" } } ] } }
        });
        let info = parse_player("dQw4w9WgXcQ", Client::Ios, Some("NONCE"), &answer).unwrap();
        assert_eq!((info.title.as_str(), info.channel.as_str(), info.channel_id.as_str(), info.duration_seconds), ("Song", "Singer", "UCabc", 213));
        assert_eq!(info.streams.len(), 2);
        assert!(info.streams.iter().all(|s| s.url.ends_with("&cpn=NONCE")));
        assert_eq!(info.streams[0].audio_codec.as_deref(), Some("opus"));
        assert_eq!(info.streams[0].format, "webm");
        assert_eq!(info.expires_unix, Some(2_000_000_000));
        assert_eq!(info.captions.len(), 1);
        assert!(info.is_fresh(2_000_000_000 - EXPIRY_MARGIN_SECONDS - 1));
        assert!(!info.is_fresh(2_000_000_000 - EXPIRY_MARGIN_SECONDS));
    }

    #[test]
    fn refusals_say_whether_the_video_itself_is_unavailable() {
        let bot = serde_json::json!({ "playabilityStatus": { "status": "LOGIN_REQUIRED", "reason": "Sign in to confirm you're not a bot" } });
        assert_ne!(parse_player("dQw4w9WgXcQ", Client::AndroidVr, None, &bot).unwrap_err().code, "VIDEO_UNAVAILABLE");
        let gone = serde_json::json!({ "playabilityStatus": { "status": "ERROR", "reason": "Video unavailable" } });
        assert_eq!(parse_player("dQw4w9WgXcQ", Client::Ios, None, &gone).unwrap_err().code, "VIDEO_UNAVAILABLE");
        let other = serde_json::json!({ "playabilityStatus": { "status": "OK" }, "videoDetails": { "videoId": "aaaaaaaaaaa" } });
        assert!(parse_player("dQw4w9WgXcQ", Client::Ios, None, &other).is_err());
        let scrambled = serde_json::json!({ "playabilityStatus": { "status": "OK" }, "streamingData": { "adaptiveFormats": [ { "signatureCipher": "s=1", "mimeType": "audio/webm" } ] } });
        assert!(parse_player("dQw4w9WgXcQ", Client::Ios, None, &scrambled).unwrap_err().details.unwrap().contains("scrambled"));
    }

    /// Against YouTube itself: `cargo test -p timeplayer-core live_ -- --ignored`. The quickest way to find
    /// out whether YouTube changed something.
    #[test]
    #[ignore]
    fn live_streams_resolve_and_seek() {
        let http = Http::new();
        let state = AppPlayerState::default();
        for id in ["dQw4w9WgXcQ", "jNQXAC9IVRw"] {
            let info = player_info(&http, &state, id).unwrap_or_else(|e| panic!("{id}: {e:?}"));
            let stream = choose_streams(&info, &StreamChoice::default()).unwrap();
            for url in [Some(stream.url.as_str()), stream.audio_url.as_deref()].into_iter().flatten() {
                let response = http.send(Request::get(url).header("Range", "bytes=200000-201023").timeout(Duration::from_secs(15))).unwrap();
                assert_eq!(response.status, 206, "{id} via {}: {}", info.client, url.split('?').next().unwrap_or(""));
                assert_eq!(response.body.len(), 1024);
            }
            println!("{id}: {} via {}, {} tracks, video {:?}", info.title, info.client, info.audio_tracks().len(), stream.audio_url.is_some());
        }
    }

    #[test]
    fn tries_the_next_app_and_caches_the_answer() {
        let calls = Arc::new(Mutex::new(Vec::<String>::new()));
        let seen = calls.clone();
        let http = Http::with_transport(move |r: Request| {
            let body = r.body_text();
            seen.lock().unwrap().push(body.clone());
            let answer = if body.contains("VISIONOS") && !body.contains("videoId") {
                serde_json::json!({ "responseContext": { "visitorData": "VISITOR" } })
            } else if body.contains("VISIONOS") {
                serde_json::json!({ "playabilityStatus": { "status": "LOGIN_REQUIRED", "reason": "Sign in to confirm you're not a bot" } })
            } else {
                serde_json::json!({ "playabilityStatus": { "status": "OK" }, "videoDetails": { "videoId": "dQw4w9WgXcQ", "title": "Song" },
                    "streamingData": { "adaptiveFormats": [ { "url": "https://r/videoplayback?itag=251", "mimeType": "audio/webm; codecs=\"opus\"", "bitrate": 1 } ] } })
            };
            Ok(crate::http::Response { status: 200, body: answer.to_string().into_bytes(), ..Default::default() })
        });
        let state = AppPlayerState::default();
        let info = player_info(&http, &state, "dQw4w9WgXcQ").unwrap();
        assert_eq!(info.client, "IOS");
        let asked = calls.lock().unwrap().clone();
        assert_eq!(asked.len(), 3, "visitor session, Vision Pro, iPhone");
        assert!(asked[1].contains("\"visitorData\":\"VISITOR\""));
        player_info(&http, &state, "dQw4w9WgXcQ").unwrap();
        assert_eq!(calls.lock().unwrap().len(), 3, "the second time comes from the cache");
        forget(&state, "dQw4w9WgXcQ");
        player_info(&http, &state, "dQw4w9WgXcQ").unwrap();
        assert_eq!(calls.lock().unwrap().len(), 5, "forgotten: asked again, with the visitor session kept");
    }
}
