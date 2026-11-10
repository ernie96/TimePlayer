//! Plays videos and runs the queue (1.x's `PlaybackCoordinator`): sources tried in order (TubeRepair,
//! Invidious, InnerTube, yt-dlp, then signed in when allowed, then mpv's own yt-dlp hook), each attempt
//! confirmed by mpv before it counts; audio description first when wanted and present; Shorts preloaded;
//! captions chosen and looked up when a stream comes without them; the queue advanced at the end of a
//! video, skipping ones that can't play (three in a row stop it).

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{Sender, channel};
use std::sync::{Arc, Mutex, Weak};
use std::time::{Duration, Instant};

use crate::account::Account;
use crate::config::{QualityPreference, Settings};
use crate::error::{AppError, Result};
use crate::innertube::InnerTube;
use crate::invidious::Invidious;
use crate::models::{CaptionTrack, Page, ResolvedStream, SearchResult, SourceKind, VideoDetails};
use crate::mpv::{Engine, EngineEvent, PlaybackState};
use crate::tuberepair::TubeRepair;
use crate::ytdlp::YtDlp;
use crate::t;

/// After this many unplayable videos in a row, the queue stops instead of skipping on.
pub const MAX_CONSECUTIVE_SKIPS: usize = 3;
const PREFETCH_LIFETIME: Duration = Duration::from_secs(300);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AudioDescriptionState {
    /// No audio description, or not known yet.
    #[default]
    None,
    /// The video has it, but the original soundtrack is playing.
    Available,
    /// The described track is playing.
    Playing,
}

#[derive(Debug, Clone, PartialEq)]
pub struct QueueItem {
    pub queue_id: u64,
    pub video: SearchResult,
    pub played: bool,
}

/// What the coordinator tells the app.
#[derive(Debug, Clone)]
pub enum Event {
    State(PlaybackState),
    Position { position: f64, duration: f64 },
    SubtitleText(String),
    /// Full details of the video playing, once found.
    CurrentVideo(VideoDetails),
    Error(String),
    /// What's being tried ("Resolving stream via TubeRepair..."), for the log and a status line.
    Status(String),
    QueueChanged,
    /// A video ended with nothing after it in the queue (the moment for autoplay).
    QueueEnded,
    /// A queued video couldn't be played; `will_continue` says whether the next one is being tried.
    Skipped { title: String, reason: String, will_continue: bool, unavailable: bool },
    AudioDescriptionAvailable,
    CaptionsChanged,
}

/// The sources, tried in this order.
#[derive(Clone)]
pub struct Sources {
    pub tube_repair: TubeRepair,
    pub invidious: Invidious,
    pub inner_tube: InnerTube,
    pub ytdlp: YtDlp,
    pub account: Option<Account>,
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum SearchSource {
    TubeRepair,
    Invidious,
    InnerTube,
    YtDlp,
}

#[derive(Default)]
struct State {
    queue: Vec<QueueItem>,
    current_index: Option<usize>,
    playing_video_id: Option<String>,
    captions: Vec<CaptionTrack>,
    selected_caption: Option<CaptionTrack>,
    current_video: Option<VideoDetails>,
    audio_description: AudioDescriptionState,
    active_source: SourceKind,
    last_query: Option<String>,
    last_search_source: Option<SearchSource>,
    next_search_token: Option<String>,
    prefetched: HashMap<String, (Instant, ResolvedStream)>,
    captions_lookup_in_progress: bool,
    captions_looked_up_for: Option<String>,
}

pub struct Coordinator {
    sources: Sources,
    settings: Settings,
    engine: Arc<Engine>,
    state: Mutex<State>,
    on_event: Arc<dyn Fn(Event) + Send + Sync>,
    attempting: AtomicBool,
    /// Waits for mpv to say a load started or failed.
    startup_waiter: Mutex<Option<Sender<bool>>>,
    /// Bumped by every new play; an attempt for an older one gives up.
    generation: AtomicU64,
    next_queue_id: AtomicU64,
    /// Whether to ask yt-dlp for captions when a stream has none (Music turns it off until asked).
    pub look_up_missing_captions: AtomicBool,
    me: Weak<Coordinator>,
}

impl Coordinator {
    pub fn new(sources: Sources, settings: Settings, http: crate::http::Http, on_event: Arc<dyn Fn(Event) + Send + Sync>) -> Arc<Self> {
        Arc::new_cyclic(|weak: &Weak<Coordinator>| {
            let me = weak.clone();
            let weak = weak.clone();
            let engine = Engine::new(http, settings.clone(), Arc::new(move |e| {
                if let Some(c) = weak.upgrade() {
                    c.on_engine_event(e);
                }
            }));
            Coordinator {
                sources,
                settings,
                engine,
                state: Mutex::new(State::default()),
                on_event,
                attempting: AtomicBool::new(false),
                startup_waiter: Mutex::new(None),
                generation: AtomicU64::new(0),
                next_queue_id: AtomicU64::new(1),
                look_up_missing_captions: AtomicBool::new(true),
                me,
            }
        })
    }

    pub fn engine(&self) -> &Arc<Engine> {
        &self.engine
    }

    pub fn sources(&self) -> &Sources {
        &self.sources
    }

    fn emit(&self, e: Event) {
        (self.on_event)(e);
    }

    fn status(&self, s: &str) {
        log::info!("{s}");
        self.emit(Event::Status(s.to_owned()));
    }

    // ----- Search -----

    /// Searches TubeRepair, then Invidious, then InnerTube (within 30 seconds), then yt-dlp. Later pages
    /// come from whichever answered.
    pub fn search(&self, query: &str) -> Result<Page<SearchResult>> {
        let deadline = Instant::now() + Duration::from_secs(30);
        let tries: [(SearchSource, &str); 3] =
            [(SearchSource::TubeRepair, "TubeRepair"), (SearchSource::Invidious, "Invidious"), (SearchSource::InnerTube, "InnerTube")];
        for (source, name) in tries {
            if Instant::now() > deadline {
                break;
            }
            match self.search_with(source, query, None) {
                Ok(page) if !page.items.is_empty() => {
                    log::info!("Found {} videos via {name}.", page.items.len());
                    self.remember_search(query, source, &page);
                    return Ok(page);
                }
                Ok(_) => log::warn!("{name} search returned nothing."),
                Err(e) => log::warn!("{name} search failed: {e}"),
            }
        }
        let page = self.search_with(SearchSource::YtDlp, query, None)?;
        self.remember_search(query, SearchSource::YtDlp, &page);
        Ok(page)
    }

    fn search_with(&self, source: SearchSource, query: &str, token: Option<&str>) -> Result<Page<SearchResult>> {
        match source {
            SearchSource::TubeRepair => self.sources.tube_repair.search(query, token),
            SearchSource::Invidious => self.sources.invidious.search(query, token),
            SearchSource::InnerTube => self.sources.inner_tube.search(query, token),
            SearchSource::YtDlp => self.sources.ytdlp.search(query, token),
        }
    }

    fn remember_search(&self, query: &str, source: SearchSource, page: &Page<SearchResult>) {
        let mut s = self.state.lock().unwrap();
        s.last_query = Some(query.to_owned());
        s.last_search_source = Some(source);
        s.next_search_token = page.next_token.clone().filter(|_| page.has_next());
        s.active_source = match source {
            SearchSource::TubeRepair => SourceKind::TubeRepair,
            SearchSource::Invidious => SourceKind::Invidious,
            SearchSource::InnerTube => SourceKind::InnerTube,
            SearchSource::YtDlp => SourceKind::YtDlp,
        };
    }

    pub fn can_load_more_search_results(&self) -> bool {
        let s = self.state.lock().unwrap();
        s.last_search_source.is_some() && s.next_search_token.as_ref().is_some_and(|t| !t.is_empty())
    }

    /// The next page of the last search, from the source that served the first.
    pub fn search_more(&self) -> Result<Page<SearchResult>> {
        let (query, source, token) = {
            let s = self.state.lock().unwrap();
            match (&s.last_query, s.last_search_source, &s.next_search_token) {
                (Some(q), Some(src), Some(t)) => (q.clone(), src, t.clone()),
                _ => return Ok(Page::default()),
            }
        };
        let page = self.search_with(source, &query, Some(&token))?;
        let mut s = self.state.lock().unwrap();
        if s.last_query.as_deref() == Some(&query) && s.last_search_source == Some(source) {
            s.next_search_token = page.next_token.clone().filter(|_| page.has_next() && !page.items.is_empty());
        }
        Ok(page)
    }

    // ----- What's playing -----

    pub fn state(&self) -> PlaybackState {
        self.engine.state()
    }

    pub fn queue(&self) -> Vec<QueueItem> {
        self.state.lock().unwrap().queue.clone()
    }

    pub fn current_index(&self) -> Option<usize> {
        self.state.lock().unwrap().current_index
    }

    pub fn current_item(&self) -> Option<QueueItem> {
        let s = self.state.lock().unwrap();
        s.current_index.and_then(|i| s.queue.get(i).cloned())
    }

    pub fn current_video(&self) -> Option<VideoDetails> {
        self.state.lock().unwrap().current_video.clone()
    }

    pub fn playing_video_id(&self) -> Option<String> {
        self.state.lock().unwrap().playing_video_id.clone()
    }

    pub fn captions(&self) -> Vec<CaptionTrack> {
        self.state.lock().unwrap().captions.clone()
    }

    pub fn selected_caption(&self) -> Option<CaptionTrack> {
        self.state.lock().unwrap().selected_caption.clone()
    }

    pub fn captions_lookup_in_progress(&self) -> bool {
        self.state.lock().unwrap().captions_lookup_in_progress
    }

    pub fn audio_description(&self) -> AudioDescriptionState {
        self.state.lock().unwrap().audio_description
    }

    pub fn active_source(&self) -> SourceKind {
        self.state.lock().unwrap().active_source
    }

    // ----- Playing -----

    /// Plays a link or ID; a video already in the queue keeps the queue's place.
    pub fn play_url_or_id(&self, url_or_id: &str) -> Result<()> {
        let url = crate::links::YouTubeUrl::validate(url_or_id)?;
        let index = self.state.lock().unwrap().queue.iter().position(|i| i.video.id == url.video_id);
        match index {
            Some(i) if url.start_seconds.is_none() => self.play_queue_from(i, false),
            _ => self.play_video(&url.video_id, url.start_seconds, false),
        }
    }

    /// Plays a video now, as the only thing in a new queue.
    pub fn play_now(&self, video: &SearchResult, start_seconds: Option<f64>) -> Result<()> {
        self.replace_queue(std::slice::from_ref(video));
        {
            let mut s = self.state.lock().unwrap();
            s.current_index = Some(0);
            if let Some(item) = s.queue.first_mut() {
                item.played = true;
            }
        }
        self.emit(Event::QueueChanged);
        self.play_video(&video.id, start_seconds, false)
    }

    /// Plays the queue from `index`, skipping videos that can't be played.
    pub fn play_queue_from(&self, mut index: usize, raise_errors: bool) -> Result<()> {
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        let mut failures = 0;
        let mut last: Result<()> = Err(AppError::playback(t!("The queue is empty.")));
        loop {
            let item = {
                let mut s = self.state.lock().unwrap();
                let Some(item) = s.queue.get_mut(index) else { break };
                item.played = true;
                let item = item.clone();
                s.current_index = Some(index);
                item
            };
            self.emit(Event::QueueChanged);
            last = self.play_video_generation(&item.video.id, None, false, generation);
            if last.is_ok() || self.generation.load(Ordering::SeqCst) != generation {
                return last;
            }
            failures += 1;
            let has_next = index + 1 < self.state.lock().unwrap().queue.len();
            let will_continue = failures < MAX_CONSECUTIVE_SKIPS && has_next;
            let error = last.as_ref().err().cloned().unwrap_or_else(|| AppError::playback(""));
            self.emit(Event::Skipped {
                title: item.video.title.clone(),
                reason: error.message.clone(),
                will_continue,
                unavailable: error.code == "VIDEO_UNAVAILABLE",
            });
            if !will_continue {
                break;
            }
            index += 1;
        }
        if failures >= MAX_CONSECUTIVE_SKIPS {
            let reason = last.as_ref().err().map(|e| e.message.clone()).unwrap_or_default();
            last = Err(AppError::playback(t!("{0} videos in a row couldn't be played, so the queue stopped. {1}", failures, reason)));
        }
        if raise_errors
            && let Err(e) = &last {
                self.emit(Event::Error(e.message.clone()));
            }
        last
    }

    pub fn play_queue_item(&self, queue_id: u64) -> Result<()> {
        let index = self.state.lock().unwrap().queue.iter().position(|i| i.queue_id == queue_id);
        match index {
            Some(i) => self.play_queue_from(i, true),
            None => Err(AppError::new("QUEUE_ITEM_NOT_FOUND", "Queue item was not found.")),
        }
    }

    pub fn play_next(&self) -> Result<()> {
        let next = {
            let s = self.state.lock().unwrap();
            s.current_index.map(|i| i + 1).unwrap_or(0)
        };
        if next < self.state.lock().unwrap().queue.len() { self.play_queue_from(next, true) } else { Ok(()) }
    }

    pub fn play_previous(&self) -> Result<()> {
        let previous = {
            let mut s = self.state.lock().unwrap();
            match s.current_index {
                Some(i) if i > 0 => {
                    s.current_index = Some(i - 1);
                    s.queue.get(i - 1).map(|q| q.video.id.clone())
                }
                _ => None,
            }
        };
        let Some(id) = previous else { return Ok(()) };
        self.emit(Event::QueueChanged);
        self.play_video(&id, None, true)
    }

    /// Plays the current video again from `at` (after audio description is switched).
    pub fn replay_current(&self, at: f64) -> Result<()> {
        let id = self.playing_video_id().ok_or_else(|| AppError::new("NOTHING_PLAYING", "Nothing is playing."))?;
        self.play_video(&id, (at > 0.0).then_some(at), false)
    }

    fn play_video(&self, video_id: &str, start: Option<f64>, raise_errors: bool) -> Result<()> {
        let generation = self.generation.fetch_add(1, Ordering::SeqCst) + 1;
        self.play_video_generation(video_id, start, raise_errors, generation)
    }

    fn superseded(&self, generation: u64) -> bool {
        self.generation.load(Ordering::SeqCst) != generation
    }

    fn play_video_generation(&self, video_id: &str, start: Option<f64>, raise_errors: bool, generation: u64) -> Result<()> {
        log::info!("Playing {video_id}.");
        {
            let mut s = self.state.lock().unwrap();
            s.playing_video_id = Some(video_id.to_owned());
            s.audio_description = AudioDescriptionState::None;
            s.current_video = None;
        }
        let quality = self.settings.with(|c| c.playback.preferred_quality);
        let want_description = self.settings.with(|c| c.playback.audio_description);
        let mut unavailable: Option<String> = None;

        // A quick look at the audio tracks, on the side.
        let (check_tx, check_rx) = channel::<Option<bool>>();
        {
            let inner_tube = self.sources.inner_tube.clone();
            let id = video_id.to_owned();
            std::thread::spawn(move || {
                let _ = check_tx.send(inner_tube.has_audio_description(&id));
            });
        }

        // Found ahead of time (the next Short): start right away.
        let ready = self.state.lock().unwrap().prefetched.remove(video_id).filter(|(at, _)| at.elapsed() < PREFETCH_LIFETIME);
        if let Some((_, stream)) = ready
            && self.attempt(&stream, video_id, start, Duration::from_secs(7), generation) {
                self.report_description_later(check_rx, video_id);
                return Ok(());
            }

        // Audio description: only yt-dlp reaches the described track, so it goes first when wanted and there.
        let has_description = if want_description { check_rx.recv_timeout(Duration::from_secs(4)).ok().flatten() } else { None };
        if want_description && has_description == Some(true) {
            self.state.lock().unwrap().audio_description = AudioDescriptionState::Available;
            self.status("Loading the audio-described version...");
            match self.sources.ytdlp.resolve(video_id, quality, false, true, None) {
                Ok(stream) if stream.uses_audio_description => {
                    if self.attempt(&stream, video_id, start, Duration::from_secs(10), generation) {
                        self.state.lock().unwrap().audio_description = AudioDescriptionState::Playing;
                        return Ok(());
                    }
                    log::warn!("The audio-described version of {video_id} did not start; playing the original.");
                }
                Ok(_) => {}
                Err(e) => log::warn!("Audio-described playback failed for {video_id}: {e}"),
            }
        } else if !want_description {
            self.report_description_later(check_rx, video_id);
        }
        if self.superseded(generation) {
            return Err(AppError::canceled());
        }

        let tiers: [(&str, SourceKind, u64); 3] =
            [("TubeRepair", SourceKind::TubeRepair, 7), ("Invidious", SourceKind::Invidious, 7), ("InnerTube", SourceKind::InnerTube, 7)];
        for (name, kind, timeout) in tiers {
            if self.superseded(generation) {
                return Err(AppError::canceled());
            }
            self.status(&format!("Resolving stream via {name}..."));
            let resolved = match kind {
                SourceKind::TubeRepair => self.sources.tube_repair.resolve(video_id),
                SourceKind::Invidious => self.sources.invidious.resolve(video_id, quality, false),
                _ => self.sources.inner_tube.resolve(video_id),
            };
            match resolved {
                Ok(stream) if !stream.url.trim().is_empty() => {
                    if self.attempt(&stream, video_id, start, Duration::from_secs(timeout), generation) {
                        return Ok(());
                    }
                    log::warn!("The {name} stream did not start playing.");
                }
                Ok(_) => {}
                Err(e) => {
                    if kind == SourceKind::InnerTube && e.message.to_lowercase().contains("unavailable")
                        || e.details.as_deref().is_some_and(|d| d.to_lowercase().contains("unavailable") && kind == SourceKind::InnerTube)
                    {
                        unavailable = Some(e.message.clone());
                    }
                    log::warn!("{name} could not resolve {video_id}: {e}");
                }
            }
        }

        if self.superseded(generation) {
            return Err(AppError::canceled());
        }
        self.status("Resolving stream via local yt-dlp...");
        match self.sources.ytdlp.resolve(video_id, quality, false, false, None) {
            Ok(stream) if !stream.url.trim().is_empty() => {
                if self.attempt(&stream, video_id, start, Duration::from_secs(10), generation) {
                    return Ok(());
                }
            }
            Ok(_) => {}
            Err(e) if e.code == "VIDEO_NOT_FOUND" => unavailable = unavailable.or(Some(e.message.clone())),
            // yt-dlp failed for another reason; mpv's own attempt may still work.
            Err(_) => unavailable = None,
        }

        // Signed in, if the person chose to: age-restricted and members-only videos.
        if let Some(account) = self.sources.account.as_ref().filter(|a| a.for_restricted_videos()) {
            let cookies = account.write_temporary_cookies_file();
            self.status("Trying with your YouTube sign-in...");
            let result = self.sources.ytdlp.resolve(video_id, quality, false, want_description, cookies.as_deref());
            if let Some(file) = &cookies {
                let _ = std::fs::remove_file(file);
            }
            if let Ok(stream) = result
                && self.attempt(&stream, video_id, start, Duration::from_secs(10), generation) {
                    if stream.uses_audio_description {
                        self.state.lock().unwrap().audio_description = AudioDescriptionState::Playing;
                    }
                    return Ok(());
                }
        }

        if let Some(reason) = unavailable {
            log::warn!("YouTube reports {video_id} as unavailable ({reason}); not trying the direct link.");
            return self.fail(&unavailable_message(), raise_errors, "VIDEO_UNAVAILABLE");
        }
        if self.superseded(generation) {
            return Err(AppError::canceled());
        }
        self.status("Playing via yt-dlp direct watch URL...");
        let direct = ResolvedStream { video_id: video_id.into(), url: crate::links::watch_url(video_id), source: SourceKind::YtDlp, ..Default::default() };
        if self.attempt(&direct, video_id, start, Duration::from_secs(25), generation) {
            return Ok(());
        }

        log::error!("Every source failed for {video_id}.");
        let mut message = t!("None of TimePlayer's sources could play this video. It may be private, age-restricted or blocked, or the sources may be having trouble; try again later.");
        if !self.sources.account.as_ref().is_some_and(Account::for_restricted_videos) {
            message.push(' ');
            message.push_str(&t!("Age-restricted videos can play if you sign in: Settings, YouTube account."));
        }
        self.fail(&message, raise_errors, "PLAYBACK_ENGINE_ERROR")
    }

    fn fail(&self, message: &str, raise: bool, code: &str) -> Result<()> {
        if raise {
            self.emit(Event::Error(message.to_owned()));
        }
        self.emit(Event::Status("Playback failed.".into()));
        let mut e = AppError::new(code, message);
        e.recoverable = code != "VIDEO_UNAVAILABLE";
        Err(e)
    }

    fn report_description_later(&self, rx: std::sync::mpsc::Receiver<Option<bool>>, video_id: &str) {
        let Some(me) = self.weak_self() else { return };
        let id = video_id.to_owned();
        std::thread::spawn(move || {
            if rx.recv_timeout(Duration::from_secs(10)).ok().flatten() == Some(true) {
                let Some(c) = me.upgrade() else { return };
                let mut s = c.state.lock().unwrap();
                if s.playing_video_id.as_deref() == Some(&id) && s.audio_description == AudioDescriptionState::None {
                    s.audio_description = AudioDescriptionState::Available;
                    drop(s);
                    c.emit(Event::AudioDescriptionAvailable);
                }
            }
        });
    }

    fn weak_self(&self) -> Option<Weak<Coordinator>> {
        Some(self.me.clone())
    }

    /// Loads a stream and waits for mpv to say it plays (or fails, or time runs out).
    fn attempt(&self, stream: &ResolvedStream, video_id: &str, start: Option<f64>, timeout: Duration, generation: u64) -> bool {
        if self.superseded(generation) {
            return false;
        }
        let (tx, rx) = channel();
        *self.startup_waiter.lock().unwrap() = Some(tx);
        self.attempting.store(true, Ordering::SeqCst);
        let loaded = self.engine.load(&stream.url, stream.audio_url.as_deref(), start);
        let started = loaded.is_ok() && {
            let deadline = Instant::now() + timeout;
            let mut result = false;
            while Instant::now() < deadline {
                if self.superseded(generation) {
                    break;
                }
                match rx.recv_timeout(Duration::from_millis(200)) {
                    Ok(ok) => {
                        result = ok;
                        break;
                    }
                    Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                        if self.engine.state() == PlaybackState::Playing {
                            result = true;
                            break;
                        }
                    }
                    Err(_) => break,
                }
            }
            result
        };
        *self.startup_waiter.lock().unwrap() = None;
        self.attempting.store(false, Ordering::SeqCst);
        if !started {
            log::warn!("Playback did not start within {timeout:?} via {}.", stream.source.name());
            return false;
        }
        let captions = useful_captions(&stream.captions);
        {
            let mut s = self.state.lock().unwrap();
            s.active_source = stream.source;
            s.captions = captions.clone();
            s.selected_caption = None;
        }
        if !captions.is_empty() {
            self.load_default_caption(&captions);
        } else if self.look_up_missing_captions.load(Ordering::SeqCst) {
            self.look_up_captions(video_id);
        }
        log::info!("Playback confirmed via {}.", stream.source.name());
        self.status(&format!("Playing via {}.", stream.source.name()));
        self.fetch_details_later(video_id);
        true
    }

    fn fetch_details_later(&self, video_id: &str) {
        let Some(me) = self.weak_self() else { return };
        let id = video_id.to_owned();
        std::thread::spawn(move || {
            let Some(c) = me.upgrade() else { return };
            let s = &c.sources;
            let details = s
                .tube_repair
                .details(&id)
                .or_else(|_| s.invidious.details(&id))
                .or_else(|_| s.inner_tube.details(&id))
                .or_else(|_| s.ytdlp.details(&id, None));
            match details {
                // The user may have moved on while details loaded.
                Ok(d) if c.playing_video_id().as_deref() == Some(&id) => {
                    c.state.lock().unwrap().current_video = Some(d.clone());
                    c.emit(Event::CurrentVideo(d));
                }
                Ok(_) => {}
                Err(e) => log::warn!("Details for {id} could not be found: {e}"),
            }
        });
    }

    /// Looks up captions now, if not done yet (C pressed in Music).
    pub fn look_up_captions_now(&self) {
        let id = {
            let s = self.state.lock().unwrap();
            match &s.playing_video_id {
                Some(id) if s.captions.is_empty() && !s.captions_lookup_in_progress && s.captions_looked_up_for.as_deref() != Some(id) => id.clone(),
                _ => return,
            }
        };
        self.look_up_captions(&id);
    }

    fn look_up_captions(&self, video_id: &str) {
        {
            let mut s = self.state.lock().unwrap();
            s.captions_lookup_in_progress = true;
            s.captions_looked_up_for = Some(video_id.to_owned());
        }
        let Some(me) = self.weak_self() else { return };
        let id = video_id.to_owned();
        std::thread::spawn(move || {
            let Some(c) = me.upgrade() else { return };
            let found = c.sources.ytdlp.details(&id, None).map(|d| useful_captions(&d.captions)).unwrap_or_default();
            if c.playing_video_id().as_deref() != Some(&id) {
                return;
            }
            log::info!("Found {} caption tracks for {id} via yt-dlp.", found.len());
            c.state.lock().unwrap().captions = found.clone();
            if !found.is_empty() {
                c.load_default_caption(&found);
            }
            c.state.lock().unwrap().captions_lookup_in_progress = false;
            c.emit(Event::CaptionsChanged);
        });
    }

    fn load_default_caption(&self, captions: &[CaptionTrack]) {
        if let Some(track) = choose_default_caption(captions)
            && self.engine.add_subtitles(&track.url, true).is_ok() {
                self.state.lock().unwrap().selected_caption = Some(track);
            }
    }

    pub fn set_caption_track(&self, track: Option<&CaptionTrack>) -> Result<()> {
        match track {
            None => {
                self.engine.set_subtitle_track("no")?;
                self.state.lock().unwrap().selected_caption = None;
            }
            Some(t) => {
                self.engine.add_subtitles(&t.url, true)?;
                self.state.lock().unwrap().selected_caption = Some(t.clone());
            }
        }
        Ok(())
    }

    /// Finds a video's stream ahead of time, so playing it later starts at once (the next Short).
    pub fn prefetch(&self, video_id: &str) {
        if !crate::links::is_video_id(video_id) {
            return;
        }
        if self.state.lock().unwrap().prefetched.get(video_id).is_some_and(|(at, _)| at.elapsed() < PREFETCH_LIFETIME) {
            return;
        }
        let quality = self.settings.with(|c| c.playback.preferred_quality);
        let stream = self.sources.tube_repair.resolve(video_id).or_else(|_| self.sources.ytdlp.resolve(video_id, quality, false, false, None));
        if let Ok(stream) = stream.filter_url() {
            let mut s = self.state.lock().unwrap();
            s.prefetched.retain(|_, (at, _)| at.elapsed() < PREFETCH_LIFETIME);
            s.prefetched.insert(video_id.to_owned(), (Instant::now(), stream));
        }
    }

    // ----- The queue -----

    pub fn enqueue(&self, video: &SearchResult) {
        let id = self.next_queue_id.fetch_add(1, Ordering::SeqCst);
        self.state.lock().unwrap().queue.push(QueueItem { queue_id: id, video: video.clone(), played: false });
        self.emit(Event::QueueChanged);
    }

    /// A new queue in one change (adding a long playlist one by one was slow in 1.x).
    pub fn replace_queue(&self, videos: &[SearchResult]) {
        {
            let mut s = self.state.lock().unwrap();
            s.queue = videos
                .iter()
                .map(|v| QueueItem { queue_id: self.next_queue_id.fetch_add(1, Ordering::SeqCst), video: v.clone(), played: false })
                .collect();
            s.current_index = None;
        }
        self.emit(Event::QueueChanged);
    }

    pub fn is_queued(&self, video_id: &str) -> bool {
        self.state.lock().unwrap().queue.iter().any(|q| q.video.id == video_id)
    }

    pub fn remove_from_queue(&self, queue_id: u64) {
        {
            let mut s = self.state.lock().unwrap();
            let Some(index) = s.queue.iter().position(|q| q.queue_id == queue_id) else { return };
            s.queue.remove(index);
            if let Some(current) = s.current_index
                && current >= index && current > 0 {
                    s.current_index = Some(current - 1);
                }
        }
        self.emit(Event::QueueChanged);
    }

    pub fn clear_queue(&self) {
        {
            let mut s = self.state.lock().unwrap();
            s.queue.clear();
            s.current_index = None;
        }
        self.emit(Event::QueueChanged);
    }

    // ----- Controls -----

    pub fn toggle_play_pause(&self) -> Result<()> {
        match self.engine.state() {
            PlaybackState::Playing => self.engine.pause(),
            PlaybackState::Paused => self.engine.play(),
            // Play after the end starts over.
            PlaybackState::Ended => {
                self.engine.seek(0.0)?;
                self.engine.play()
            }
            _ => Ok(()),
        }
    }

    pub fn stop(&self) {
        self.generation.fetch_add(1, Ordering::SeqCst);
        self.engine.stop();
    }

    fn on_engine_event(&self, event: EngineEvent) {
        match event {
            EngineEvent::State(state) => {
                if let Some(tx) = self.startup_waiter.lock().unwrap().as_ref() {
                    match state {
                        PlaybackState::Playing => {
                            let _ = tx.send(true);
                        }
                        PlaybackState::Error | PlaybackState::Stopped | PlaybackState::Ended => {
                            let _ = tx.send(false);
                        }
                        _ => {}
                    }
                }
                self.emit(Event::State(state));
                if state == PlaybackState::Ended && !self.attempting.load(Ordering::SeqCst) {
                    let has_next = {
                        let s = self.state.lock().unwrap();
                        s.current_index.map(|i| i + 1 < s.queue.len()).unwrap_or(false)
                    };
                    if has_next {
                        if let Some(me) = self.weak_self() {
                            std::thread::spawn(move || {
                                if let Some(c) = me.upgrade() {
                                    let _ = c.play_next();
                                }
                            });
                        }
                    } else {
                        self.emit(Event::QueueEnded);
                    }
                }
            }
            EngineEvent::Error(message) => {
                if self.attempting.load(Ordering::SeqCst) {
                    log::warn!("Ignored a player error while trying a source: {message}");
                    if let Some(tx) = self.startup_waiter.lock().unwrap().as_ref() {
                        let _ = tx.send(false);
                    }
                    return;
                }
                self.emit(Event::Error(message));
            }
            EngineEvent::Position { position, duration } => self.emit(Event::Position { position, duration }),
            EngineEvent::SubtitleText(text) => self.emit(Event::SubtitleText(text)),
        }
    }
}

trait FilterUrl {
    fn filter_url(self) -> Result<ResolvedStream>;
}

impl FilterUrl for Result<ResolvedStream> {
    fn filter_url(self) -> Result<ResolvedStream> {
        self.and_then(|s| if s.url.trim().is_empty() { Err(AppError::stream_unavailable(&s.video_id, None)) } else { Ok(s) })
    }
}

/// Told to users when YouTube itself won't play a video.
pub fn unavailable_message() -> String {
    t!("YouTube says this video isn't available. It may have been removed, made private, or blocked where you are.")
}

fn system_language() -> String {
    sys_locale::get_locale().unwrap_or_else(|| "en".into()).chars().take(2).collect::<String>().to_lowercase()
}

fn is_lang(code: &str, lang: &str) -> bool {
    let code = code.to_lowercase();
    code == lang || code.starts_with(&format!("{lang}-"))
}

/// Real captions (default, the user's language, English, any), then automatic captions in the video's own
/// language ("-orig"), then automatic captions in the user's language.
pub fn choose_default_caption(captions: &[CaptionTrack]) -> Option<CaptionTrack> {
    let lang = system_language();
    let manual: Vec<&CaptionTrack> = captions.iter().filter(|c| !c.auto_generated).collect();
    let auto: Vec<&CaptionTrack> = captions.iter().filter(|c| c.auto_generated).collect();
    manual
        .iter()
        .find(|c| c.is_default)
        .or_else(|| manual.iter().find(|c| is_lang(&c.language_code, &lang)))
        .or_else(|| manual.iter().find(|c| is_lang(&c.language_code, "en")))
        .or_else(|| manual.first())
        .or_else(|| auto.iter().find(|c| c.language_code.to_lowercase().ends_with("-orig")))
        .or_else(|| auto.iter().find(|c| is_lang(&c.language_code, &lang)))
        .or_else(|| auto.first())
        .map(|c| (*c).clone())
}

/// YouTube lists automatic translations into about 150 languages; keep the video's own and the user's.
pub fn useful_captions(captions: &[CaptionTrack]) -> Vec<CaptionTrack> {
    let lang = system_language();
    captions
        .iter()
        .filter(|c| !c.auto_generated || c.language_code.to_lowercase().ends_with("-orig") || c.language_code.eq_ignore_ascii_case(&lang))
        .cloned()
        .collect()
}

/// Which quality to ask the sources for.
pub fn quality(settings: &Settings) -> QualityPreference {
    settings.with(|c| c.playback.preferred_quality)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn track(code: &str, auto: bool) -> CaptionTrack {
        CaptionTrack { language_code: code.into(), display_name: code.into(), url: code.into(), auto_generated: auto, ..Default::default() }
    }

    #[test]
    fn real_captions_win_and_translations_are_dropped() {
        let all = vec![track("de", true), track("en-orig", true), track("fr", false)];
        assert_eq!(choose_default_caption(&all).unwrap().language_code, "fr");
        let useful = useful_captions(&all);
        assert!(useful.iter().any(|c| c.language_code == "en-orig"));
        assert!(!useful.iter().any(|c| c.language_code == "de") || system_language() == "de");
        assert_eq!(choose_default_caption(&[track("de", true), track("en-orig", true)]).unwrap().language_code, "en-orig");
    }
}
