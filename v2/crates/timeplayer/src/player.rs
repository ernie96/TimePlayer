//! Playing and the player (1.x's `MainViewModel` playback commands, `.Player`, `.MusicMode`, `.Community`,
//! `.Chapters`, `.Music`, `.AudioDescription`, `.SoundOutput` and Shorts mode): the player view, the keys'
//! actions, what the engine and the coordinator report, the panels and the braille line.

use std::collections::{HashMap, HashSet};
use std::time::{Duration, Instant};

use timeplayer_core::chapters::{self, Chapter};
use timeplayer_core::community::{self, LyricLine, SegmentEvent, SegmentKind, SegmentTracker, TranscriptLine, Votes};
use timeplayer_core::i18n::{plural, t, tf};
use timeplayer_core::links;
use timeplayer_core::models::{CaptionTrack, Comment, CommentPage, ItemKind, SearchResult, VideoDetails};
use timeplayer_core::mpv::PlaybackState;
use timeplayer_core::playback::{AudioDescriptionState, Event};
use timeplayer_core::{audio, braille, text};
use wxdragon::prelude::*;

use crate::sections::Section;
use crate::speech;
use crate::window::PlayerPanel;
use crate::{App, LastAction};

/// What the player shows and knows about the video playing.
#[derive(Default)]
pub struct Playing {
    /// A video or song is loaded (1.x's IsPlayerDialogOpen); with music it can play while the list shows.
    pub session_open: bool,
    pub engine_ready: bool,
    /// The video asked for, as the list had it.
    pub current: Option<SearchResult>,
    /// Its full details, once found.
    pub details: Option<VideoDetails>,
    pub title: String,
    pub channel: String,
    pub state: PlaybackState,
    pub position: f64,
    pub duration: f64,
    pub volume: f64,
    pub muted: bool,
    pub speed: f64,
    resume_handled_for: Option<String>,
    watch_page_for: Option<String>,
    watch_generation: u64,
    pub up_next: Vec<SearchResult>,
    pub description: String,
    pub votes: Option<Votes>,
    segments: SegmentTracker,
    skipping_segment: bool,
    segments_quiet_until: Option<Instant>,
    pub chapters: Vec<Chapter>,
    last_chapter: Option<usize>,
    pub transcript: Vec<TranscriptLine>,
    transcript_for: Option<String>,
    pub lyrics: Vec<LyricLine>,
    lyrics_for: Option<String>,
    lyrics_loading: bool,
    lyrics_failed: bool,
    last_sung: Option<usize>,
    radio_songs: HashSet<String>,
    radio_refilling: bool,
    last_spoken_caption: Option<String>,
    description_announced_for: Option<String>,
    blink_visible: bool,
    pub comments: Comments,
    pub shorts: Shorts,
}

#[derive(Default)]
pub struct Comments {
    last_page: Option<CommentPage>,
    requested: bool,
    loading: bool,
    speak_next: bool,
    /// What each tree item stands for, by the number stored on it.
    nodes: HashMap<u64, Node>,
    /// The tree item of each node (tree items stay on the window's thread).
    items: HashMap<u64, TreeItemId>,
    next_id: u64,
    top_level: Vec<u64>,
    top_level_ids: HashSet<String>,
}

#[derive(Clone)]
enum Node {
    Comment { comment: Comment, level: usize, replies_loaded: bool },
    /// "More replies": loads the rest of a reply list.
    MoreReplies { parent: Comment, token: String },
}

#[derive(Default)]
pub struct Shorts {
    pub active: bool,
    items: Vec<SearchResult>,
    index: usize,
    has_more: bool,
    loading: bool,
    query: Option<(String, Option<String>)>,
    /// More Shorts come from loading more of the list they started from.
    from_list: bool,
    playing_short: bool,
}

impl Playing {
    pub fn new(volume: f64) -> Self {
        Self { volume, speed: 1.0, blink_visible: true, ..Default::default() }
    }

    pub fn effective_duration(&self) -> f64 {
        if self.duration > 0.0 { self.duration } else { self.details.as_ref().map(|d| d.duration.as_secs_f64()).unwrap_or(0.0) }
    }

    pub fn current_id(&self) -> Option<String> {
        self.current.as_ref().map(|c| c.id.clone())
    }

    fn chapter_starts(&self) -> Vec<f64> {
        self.chapters.iter().map(|c| c.start).collect()
    }
}

impl App {
    // ----- Starting playback -----

    /// Enter on a list row: a video plays, a channel or playlist opens.
    pub(crate) fn play_item(&mut self, item: SearchResult, continue_queue: bool) {
        match item.kind {
            ItemKind::Channel => {
                self.open_channel(&item);
                return;
            }
            ItemKind::Playlist => {
                self.leave_player_view();
                self.open_playlist(&item);
                return;
            }
            ItemKind::Video => {}
        }
        if !self.playing.shorts.playing_short {
            self.end_shorts_mode();
        }
        self.begin_new_playback();
        self.set_current_library_video(Some(item.clone()));
        self.playing.title = item.title.clone();
        self.playing.channel = item.channel_title.clone();
        if let Some(d) = item.duration.filter(|d| !d.is_zero()) {
            self.playing.duration = d.as_secs_f64();
        }
        self.open_player_session();
        self.last_action = Some(LastAction::Play(item.id.clone()));
        self.clear_error();
        speech::say(&if self.music_mode { tf("Playing {0}", &[&item.title]) } else { tf("Loading {0}", &[&item.title]) });
        self.focus_player_if_shown();
        let mut clean = item.clone();
        clean.status_prefix = None;
        if !self.coordinator.is_queued(&item.id) {
            if continue_queue {
                self.coordinator.enqueue(&clean);
            } else {
                self.coordinator.replace_queue(std::slice::from_ref(&clean));
            }
        }
        let coordinator = self.coordinator.clone();
        let id = item.id.clone();
        self.jobs.run("play", move || {
            let result = coordinator.play_url_or_id(&id);
            move |app: &mut App| {
                if let Err(e) = result {
                    app.show_problem(&t("Could not play this video"), Some(&e.message), true);
                }
            }
        });
    }

    /// A pasted or typed YouTube link (1.x's PlayDirectUrlAsync).
    pub(crate) fn play_direct_url(&mut self, url: &str) {
        if url.trim().is_empty() {
            return;
        }
        self.end_shorts_mode();
        self.begin_new_playback();
        self.playing.title = t("Loading YouTube video...");
        self.playing.channel.clear();
        let id = links::YouTubeUrl::parse(url).map(|u| u.video_id).or_else(|| links::is_video_id(url).then(|| url.to_owned()));
        self.set_current_library_video(id.as_ref().map(|id| SearchResult::video(id, "", "")));
        self.open_player_session();
        self.last_action = Some(LastAction::Play(url.to_owned()));
        self.clear_error();
        speech::say(&t("Loading video..."));
        self.focus_player_if_shown();
        if let Some(id) = &id
            && !self.coordinator.is_queued(id)
        {
            self.coordinator.clear_queue();
        }
        let coordinator = self.coordinator.clone();
        let url = url.to_owned();
        self.jobs.run("play link", move || {
            let result = coordinator.play_url_or_id(&url);
            move |app: &mut App| {
                if let Err(e) = result {
                    app.show_problem(&t("Could not load this video"), Some(&e.message), true);
                }
            }
        });
    }

    /// A link typed in the search box, pasted, or sent from a browser: true when it was one.
    pub(crate) fn open_link(&mut self, text: &str) -> bool {
        if !links::looks_like_link(text) {
            return false;
        }
        if !self.music_mode && links::is_youtube_music(text) && (links::is_playlist_link(text) || links::YouTubeUrl::parse(text).is_some()) {
            self.toggle_music_mode();
        }
        if links::is_playlist_link(text)
            && let Some(list) = links::playlist_id(text)
        {
            self.ui.search_box.set_value(text);
            self.open_youtube_playlist(&list);
            return true;
        }
        if let Some(url) = links::YouTubeUrl::parse(text) {
            let normalized = url.normalized();
            self.ui.search_box.set_value(&normalized);
            self.play_direct_url(&normalized);
            return true;
        }
        false
    }

    /// Control+Shift+V.
    pub(crate) fn paste_and_play(&mut self) {
        let text = Clipboard::get().get_text().map(|t| t.trim().to_owned()).unwrap_or_default();
        if text.is_empty() {
            speech::say(&t("Clipboard is empty or does not contain text."));
            return;
        }
        if !self.open_link(&text) {
            self.ui.search_box.set_value(&text);
            speech::say_quietly(&t("Pasted into search box."));
            self.ui.focus_search();
        }
    }

    fn begin_new_playback(&mut self) {
        if self.playing.session_open && matches!(self.playing.state, PlaybackState::Playing | PlaybackState::Paused) {
            self.record_current_progress(self.playing.position, true);
        }
        self.playing.resume_handled_for = None;
        self.playing.segments.rewind();
    }

    fn open_player_session(&mut self) {
        self.playing.session_open = true;
        if !self.music_mode {
            self.set_player_visible(true);
        }
        self.update_now_playing();
        self.update_title();
    }

    pub(crate) fn set_player_visible(&mut self, visible: bool) {
        if self.player_visible == visible {
            return;
        }
        self.player_visible = visible;
        self.ui.library.show(!visible);
        self.ui.player.show(visible);
        self.ui.root.layout();
        self.update_now_playing();
        self.update_title();
    }

    fn focus_player_if_shown(&self) {
        if self.player_visible {
            self.ui.play.set_focus();
        }
    }

    /// Escape in the player, or Back: music keeps playing, a video stops.
    pub(crate) fn leave_player_view(&mut self) {
        if !self.player_visible {
            return;
        }
        if self.music_mode {
            self.set_player_visible(false);
            self.refresh_library_status_of_visible_items();
            self.ui.focus_list();
        } else {
            self.close_player_session();
        }
    }

    /// Alt+P in Music, or Open player.
    pub(crate) fn show_player_view(&mut self) {
        if !self.playing.session_open {
            speech::say(&t("Nothing is playing."));
            return;
        }
        self.set_player_visible(true);
        self.ui.play.set_focus();
    }

    /// Stop in the Now playing bar, or the Stop media key.
    pub(crate) fn stop_playback_session(&mut self) {
        if !self.playing.session_open {
            return;
        }
        self.close_player_session();
        speech::say(&t("Stopped."));
    }

    fn close_player_session(&mut self) {
        if !self.playing.session_open {
            self.set_player_visible(false);
            return;
        }
        self.playing.session_open = false;
        self.end_shorts_mode();
        self.record_current_progress(self.playing.position, true);
        self.persist_volume();
        let engine = self.coordinator.engine().clone();
        std::thread::spawn(move || engine.stop());
        self.playing.state = PlaybackState::Stopped;
        self.playing.position = 0.0;
        self.update_braille();
        self.set_player_visible(false);
        self.refresh_library_status_of_visible_items();
        self.ui.focus_list();
    }

    pub(crate) fn update_now_playing(&self) {
        let show = self.playing.session_open && !self.player_visible;
        let text = if self.playing.channel.trim().is_empty() {
            tf("Now playing: {0}", &[&self.playing.title])
        } else {
            tf("Now playing: {0}, by {1}", &[&self.playing.title, &self.playing.channel])
        };
        self.ui.now_playing_text.set_label(&text.replace('&', "&&"));
        if self.ui.now_playing.is_shown() != show {
            self.ui.now_playing.show(show);
            self.ui.library.layout();
        }
    }

    pub(crate) fn mode_name(&self) -> String {
        if self.music_mode { t("Music") } else { t("Video") }
    }

    pub(crate) fn update_title(&self) {
        let title = if self.playing.session_open && self.player_visible && !self.playing.title.trim().is_empty() {
            tf("{0} - TimePlayer {1}", &[&self.playing.title, &self.mode_name()])
        } else {
            tf("TimePlayer {0}", &[&self.mode_name()])
        };
        self.ui.frame.set_title(&title);
    }

    fn update_play_buttons(&self) {
        let label = if self.playing.state == PlaybackState::Playing { t("Pause") } else { t("Play") };
        self.ui.play.set_label(&label);
        self.ui.now_play.set_label(&label);
        self.ui.mute.set_label(&if self.playing.muted { t("Unmute") } else { t("Mute") });
    }

    // ----- What the coordinator and engine report -----

    pub(crate) fn on_playback_event(&mut self, event: Event) {
        match event {
            Event::State(state) => self.on_state(state),
            Event::Position { position, duration } => self.on_position(position, duration),
            Event::SubtitleText(text) => self.on_subtitle(&text),
            Event::CurrentVideo(video) => {
                self.playing.title = video.title.clone();
                self.playing.channel = video.channel.name.clone();
                if self.playing.description.trim().is_empty()
                    && let Some(d) = video.description.as_ref().filter(|d| !d.trim().is_empty())
                {
                    self.set_description(d.clone());
                }
                let as_item = video.to_search_result();
                self.playing.details = Some(video);
                self.set_current_library_video(Some(as_item));
                self.update_now_playing();
                self.update_title();
                self.populate_caption_choices();
            }
            Event::Error(message) => self.show_problem(&t("Playback problem"), Some(&message), true),
            Event::Status(status) => log::info!("{status}"),
            Event::QueueChanged => self.on_queue_changed(),
            Event::QueueEnded => self.on_queue_ended(),
            Event::Skipped { title, reason: _, will_continue, unavailable } => {
                if will_continue {
                    let why = if unavailable { t("it isn't available") } else { t("it wouldn't play") };
                    speech::say_quietly(&tf("Skipped {0}: {1}.", &[&title, &why]));
                }
            }
            Event::AudioDescriptionAvailable => {
                let id = self.playing.details.as_ref().map(|d| d.id.clone());
                if id.is_some() && self.playing.description_announced_for != id {
                    self.playing.description_announced_for = id;
                    speech::say_quietly(&t("Audio description available: press D."));
                }
            }
            Event::CaptionsChanged => {
                self.populate_caption_choices();
                if self.settings.with(|c| c.accessibility.speak_captions) && self.playing.session_open && self.coordinator.captions().is_empty() {
                    speech::say_quietly(&t("This video has no captions."));
                }
            }
        }
    }

    fn on_state(&mut self, state: PlaybackState) {
        if !self.playing.engine_ready && state != PlaybackState::Stopped {
            self.playing.engine_ready = true;
        }
        self.playing.state = state;
        self.playing.blink_visible = true;
        self.update_play_buttons();
        self.update_braille();
        // Resume where the video was left.
        if state == PlaybackState::Playing
            && let Some(id) = self.playing.current_id()
            && self.playing.resume_handled_for.as_deref() != Some(&id)
        {
            self.playing.resume_handled_for = Some(id.clone());
            let (resume, record) = self.settings.with(|c| (c.library.resume_playback, c.library.record_history));
            if resume
                && record
                && let Some(at) = self.library.get_history(&id).and_then(|h| h.resume_position())
            {
                self.playing.segments_quiet_until = Some(Instant::now() + Duration::from_secs(2));
                self.playing.position = at;
                self.update_braille();
                let _ = self.coordinator.engine().seek(at);
                speech::say_quietly(&tf("Resumed at {0}. Home starts over.", &[&text::time_spoken_secs(at)]));
            }
        }
        if state == PlaybackState::Ended {
            self.record_current_progress(self.playing.duration, true);
        }
        if state == PlaybackState::Playing {
            let id = self.playing.details.as_ref().map(|d| d.id.clone());
            if id.is_some() && self.coordinator.audio_description() == AudioDescriptionState::Playing && self.playing.description_announced_for != id {
                self.playing.description_announced_for = id;
                speech::say_quietly(&t("With audio description."));
            }
        }
    }

    fn on_position(&mut self, position: f64, duration: f64) {
        self.playing.position = position;
        if duration > 0.0 {
            if (duration - self.playing.duration).abs() > 0.5 && !self.playing.chapters.is_empty() {
                self.playing.duration = duration;
                self.refresh_chapters();
            }
            self.playing.duration = duration;
        }
        self.update_position_slider();
        self.update_braille();
        if self.playing.state == PlaybackState::Playing && position > 5.0 {
            self.record_current_progress(position, false);
        }
        self.on_segment_position(position);
        self.on_chapter_position(position);
        self.on_lyrics_position(position);
    }

    fn on_subtitle(&mut self, text: &str) {
        if !self.settings.with(|c| c.accessibility.speak_captions) || text.trim().is_empty() {
            return;
        }
        let spoken = text.split_whitespace().collect::<Vec<_>>().join(" ");
        if self.playing.last_spoken_caption.as_deref() == Some(&spoken) {
            return;
        }
        self.playing.last_spoken_caption = Some(spoken.clone());
        speech::say_caption(&spoken);
    }

    fn on_queue_changed(&mut self) {
        if let Some(item) = self.coordinator.current_item() {
            self.playing.title = item.video.title.clone();
            self.playing.channel = item.video.channel_title.clone();
            if self.playing.current_id().as_deref() != Some(&item.video.id) {
                self.set_current_library_video(Some(item.video.clone()));
            }
            self.update_now_playing();
            self.update_title();
        }
        self.show_player_queue();
        if self.browse.current() == Section::Queue {
            self.show_rows(quiet_list::Select::Keep);
        }
    }

    fn on_queue_ended(&mut self) {
        if self.playing.session_open && self.continue_radio() {
            return;
        }
        let autoplay = self.settings.with(|c| c.library.autoplay);
        if autoplay
            && self.playing.session_open
            && let Some(next) = self.next_autoplay_candidate()
        {
            speech::say_quietly(&tf("Up next: {0}", &[&next.title]));
            self.play_item(next, true);
        } else {
            speech::say_quietly(&t("Video ended."));
        }
    }

    fn next_autoplay_candidate(&self) -> Option<SearchResult> {
        let current = self.playing.current_id();
        self.playing
            .up_next
            .iter()
            .find(|v| v.kind == ItemKind::Video && !v.is_live && Some(&v.id) != current.as_ref() && !self.library.get_history(&v.id).is_some_and(|h| h.is_finished()))
            .cloned()
    }

    fn can_play_next(&self) -> bool {
        let queue = self.coordinator.queue();
        self.coordinator.current_index().is_some_and(|i| i + 1 < queue.len())
    }

    fn can_play_previous(&self) -> bool {
        self.coordinator.current_index().is_some_and(|i| i > 0)
    }

    fn announce_queue_neighbour(&self, offset: isize) {
        let queue = self.coordinator.queue();
        if let Some(index) = self.coordinator.current_index()
            && let Some(item) = index.checked_add_signed(offset).and_then(|i| queue.get(i))
        {
            speech::say_quietly(&item.video.title);
        }
    }

    // ----- Library state of the video playing -----

    fn set_current_library_video(&mut self, video: Option<SearchResult>) {
        if let Some(previous) = &self.playing.current
            && Some(&previous.id) != video.as_ref().map(|v| &v.id)
        {
            self.record_current_progress(self.playing.position, true);
        }
        self.playing.current = video.map(|mut v| {
            v.status_prefix = None;
            v
        });
        if self.playing.details.as_ref().map(|d| &d.id) != self.playing.current.as_ref().map(|c| &c.id) {
            self.playing.details = None;
        }
        let id = self.playing.current_id();
        self.reset_watch_page(id);
    }

    fn record_current_progress(&self, position: f64, flush: bool) {
        let Some(current) = &self.playing.current else { return };
        if !self.settings.with(|c| c.library.record_history) || self.playing.resume_handled_for.as_deref() != Some(&current.id) {
            // Not before the resume decision, or the old resume point is overwritten.
            return;
        }
        let video = match &self.playing.details {
            Some(d) if d.id == current.id => d.to_search_result(),
            _ => current.clone(),
        };
        self.library.record_progress(&video, position, self.playing.duration, flush, self.music_mode);
    }

    /// The video for Like, Watch Later and the like: the details when they match what's playing.
    pub(crate) fn current_video_for_actions(&self) -> Option<SearchResult> {
        match (&self.playing.details, &self.playing.current) {
            (Some(d), Some(c)) if d.id == c.id => Some(d.to_search_result()),
            (_, current) => current.clone(),
        }
    }

    fn persist_volume(&self) {
        let volume = self.playing.volume;
        if self.settings.with(|c| (c.playback.default_volume_percent - volume).abs() >= 0.5) {
            self.settings.update(|c| c.playback.default_volume_percent = volume);
        }
    }

    // ----- Keys and buttons -----

    pub(crate) fn toggle_play_pause(&mut self) {
        if !self.playing.session_open {
            return;
        }
        let was_playing = self.playing.state == PlaybackState::Playing;
        match self.coordinator.toggle_play_pause() {
            Ok(()) => speech::say_quietly(&if was_playing { t("Paused") } else { t("Playing") }),
            Err(e) => log::warn!("Couldn't play or pause: {}", e.message),
        }
    }

    pub(crate) fn play_next(&mut self) {
        if !self.can_play_next() {
            return;
        }
        self.announce_queue_neighbour(1);
        let coordinator = self.coordinator.clone();
        self.jobs.run("next", move || {
            let result = coordinator.play_next();
            move |app: &mut App| {
                if let Err(e) = result {
                    app.show_problem(&t("Could not play this video"), Some(&e.message), true);
                }
            }
        });
    }

    pub(crate) fn play_previous(&mut self) {
        if !self.can_play_previous() {
            return;
        }
        self.announce_queue_neighbour(-1);
        let coordinator = self.coordinator.clone();
        self.jobs.run("previous", move || {
            let result = coordinator.play_previous();
            move |app: &mut App| {
                if let Err(e) = result {
                    app.show_problem(&t("Could not play this video"), Some(&e.message), true);
                }
            }
        });
    }

    /// Shift+N, Next, the next media key: the queue's next, or else Up next.
    pub(crate) fn play_next_or_up_next(&mut self) {
        if self.can_play_next() {
            self.play_next();
        } else if let Some(next) = self.next_autoplay_candidate() {
            self.play_item(next, true);
        } else {
            speech::say(&t("Nothing up next yet."));
        }
    }

    fn clamp_to_duration(&self, position: f64) -> f64 {
        let duration = self.playing.effective_duration();
        let position = position.max(0.0);
        if duration > 0.0 { position.min(duration) } else { position }
    }

    fn seek_by(&mut self, delta: f64) {
        if !self.playing.session_open {
            return;
        }
        let target = self.clamp_to_duration(self.playing.position + delta);
        if self.coordinator.engine().seek_relative(delta).is_ok() {
            self.playing.position = target;
            self.update_braille();
            speech::say_quietly(&text::time_spoken_secs(target));
        }
    }

    pub(crate) fn seek_small(&mut self, forward: bool) {
        let step = self.settings.with(|c| c.playback.small_seek_seconds);
        self.seek_by(if forward { step } else { -step });
    }

    pub(crate) fn seek_large(&mut self, forward: bool) {
        let step = self.settings.with(|c| c.playback.large_seek_seconds);
        self.seek_by(if forward { step } else { -step });
    }

    fn seek_to(&mut self, target: f64) {
        self.playing.position = target;
        self.update_braille();
        let _ = self.coordinator.engine().seek(target);
    }

    /// 0 to 9.
    pub(crate) fn seek_to_percent(&mut self, tenths: u32) {
        let duration = self.playing.effective_duration();
        if !self.playing.session_open || duration <= 0.0 {
            return;
        }
        let target = duration * (tenths.min(9) as f64 / 10.0);
        self.seek_to(target);
        speech::say_quietly(&tf("{0} percent, {1}.", &[&(tenths * 10), &text::time_spoken_secs(target)]));
    }

    /// Home.
    pub(crate) fn restart_from_beginning(&mut self) {
        if !self.playing.session_open {
            return;
        }
        self.seek_to(0.0);
        speech::say_quietly(&t("From the beginning."));
    }

    /// The Position slider moved (its own Left, Right, Page Up and Page Down).
    pub(crate) fn position_slider_moved(&mut self, value: i32) {
        if !self.playing.session_open || (value as f64 - self.playing.position).abs() < 0.5 {
            return;
        }
        let target = self.clamp_to_duration(value as f64);
        self.seek_to(target);
        speech::say_quietly(&text::time_spoken_secs(target));
    }

    fn update_position_slider(&self) {
        let duration = self.playing.effective_duration().max(1.0).round() as i32;
        let (small, large) = self.settings.with(|c| (c.playback.small_seek_seconds, c.playback.large_seek_seconds));
        let _ = (small, large);
        self.ui.position.set_range(0, duration);
        let value = self.playing.position.round() as i32;
        if self.ui.position.get_value() != value {
            self.ui.position.set_value(value);
        }
    }

    fn max_volume(&self) -> f64 {
        if self.settings.with(|c| c.playback.volume_boost) { 200.0 } else { 100.0 }
    }

    fn volume_spoken(percent: f64) -> String {
        if percent > 100.0 { tf("Volume {0} percent, boosted", &[&(percent.round() as i64)]) } else { text::volume_spoken(percent) }
    }

    pub(crate) fn change_volume(&mut self, up: bool) {
        if !self.playing.session_open {
            return;
        }
        let step = self.settings.with(|c| c.playback.volume_step_percent);
        let volume = (self.playing.volume + if up { step } else { -step }).clamp(0.0, self.max_volume());
        if self.coordinator.engine().set_volume(volume).is_ok() {
            self.playing.volume = volume;
            speech::say_quietly(&Self::volume_spoken(volume));
        }
    }

    pub(crate) fn toggle_mute(&mut self) {
        if !self.playing.session_open {
            return;
        }
        let muted = !self.playing.muted;
        if self.coordinator.engine().set_mute(muted).is_ok() {
            self.playing.muted = muted;
            self.update_play_buttons();
            speech::say_quietly(&if muted { t("Audio muted.") } else { t("Audio unmuted.") });
        }
    }

    pub(crate) fn change_speed(&mut self, direction: i32) {
        if !self.playing.session_open {
            return;
        }
        let (step, min, max) = self.settings.with(|c| (c.playback.speed_step, c.playback.min_speed, c.playback.max_speed));
        let speed = if direction == 0 { 1.0 } else { (self.playing.speed + step * direction as f64).clamp(min, max) };
        if self.coordinator.engine().set_speed(speed).is_ok() {
            self.playing.speed = speed;
            speech::say_quietly(&text::speed_spoken(speed));
        }
    }

    /// T.
    pub(crate) fn announce_time(&self) {
        let duration = self.playing.effective_duration();
        let position = self.playing.position;
        if duration <= 0.0 {
            speech::say(&tf("{0} elapsed.", &[&text::time_spoken_secs(position)]));
            return;
        }
        let percent = (position / duration * 100.0).round() as i64;
        let mut said = tf("{0} of {1}. ", &[&text::time_spoken_secs(position), &text::time_spoken_secs(duration)])
            + &tf("{0} remaining. {1} percent.", &[&text::time_spoken_secs(duration - position), &percent]);
        if let Some(chapter) = self.current_chapter_spoken() {
            said = format!("{said} {chapter}");
        }
        speech::say(&said);
    }

    /// I.
    pub(crate) fn announce_video_info(&self) {
        let mut parts = vec![self.playing.title.clone()];
        let channel_id = self.playing.details.as_ref().map(|d| d.channel.id.clone()).filter(|c| !c.is_empty()).or_else(|| self.playing.current.as_ref().and_then(|c| c.channel_id.clone()));
        let subscribed = channel_id.is_some_and(|c| self.library.is_subscribed(&c));
        if !self.playing.channel.trim().is_empty() {
            parts.push(tf("by {0}", &[&self.playing.channel]) + &if subscribed { t(", subscribed") } else { String::new() });
        }
        if let Some(details) = &self.playing.details {
            if let Some(uploaded) = details.upload_date {
                parts.push(tf("uploaded {0}", &[&text::age_spoken(uploaded, chrono::Utc::now())]));
            }
            if let Some(views) = details.view_count {
                parts.push(tf("{0:N0} views", &[&views]));
            }
        }
        if let Some(votes) = &self.playing.votes {
            parts.push(votes.spoken());
        }
        if let Some(segments) = self.segment_summary() {
            parts.push(segments);
        }
        if let Some(id) = self.playing.current_id() {
            if self.library.is_liked(&id) {
                parts.push(t("liked"));
            }
            if self.library.is_in_watch_later(&id) {
                parts.push(t("in Watch Later"));
            }
        }
        speech::say(&(parts.join(". ") + "."));
    }

    /// U.
    pub(crate) fn announce_up_next(&self) {
        let queue = self.coordinator.queue();
        let said = if let Some(next) = self.coordinator.current_index().and_then(|i| queue.get(i + 1)) {
            tf("Next in queue: {0}", &[&next.video.title])
        } else if let Some(candidate) = self.next_autoplay_candidate() {
            if self.settings.with(|c| c.library.autoplay) {
                tf("Up next: {0}, by {1}", &[&candidate.title, &candidate.channel_title])
            } else {
                tf("Up next, autoplay off: {0}, by {1}", &[&candidate.title, &candidate.channel_title])
            }
        } else {
            t("Nothing up next yet.")
        };
        speech::say(&said);
    }

    /// A.
    pub(crate) fn toggle_autoplay(&mut self) {
        let on = !self.settings.with(|c| c.library.autoplay);
        self.settings.update(|c| c.library.autoplay = on);
        speech::say_quietly(&if on { t("Autoplay on.") } else { t("Autoplay off.") });
    }

    /// C.
    pub(crate) fn toggle_speak_captions(&mut self) {
        let on = !self.settings.with(|c| c.accessibility.speak_captions);
        self.settings.update(|c| c.accessibility.speak_captions = on);
        self.coordinator.look_up_missing_captions.store(!self.music_mode || on, std::sync::atomic::Ordering::Relaxed);
        if on {
            self.coordinator.look_up_captions_now();
        }
        let message = if !on {
            t("Captions will not be read aloud.")
        } else if !self.coordinator.captions().is_empty() || !self.playing.session_open {
            t("Captions will be read aloud.")
        } else if self.coordinator.captions_lookup_in_progress() {
            t("Captions will be read aloud. Still looking for this video's captions.")
        } else {
            t("Captions will be read aloud, but this video has no captions.")
        };
        speech::say_quietly(&message);
    }

    /// D.
    pub(crate) fn toggle_audio_description(&mut self) {
        let on = !self.settings.with(|c| c.playback.audio_description);
        self.settings.update(|c| c.playback.audio_description = on);
        let state = self.coordinator.audio_description();
        if state == AudioDescriptionState::None {
            speech::say(&if on { t("Audio description on. This video doesn't have it.") } else { t("Audio description off.") });
            return;
        }
        speech::say(&if on { t("Audio description on.") } else { t("Audio description off.") });
        let needs_switch = if on { state == AudioDescriptionState::Available } else { state == AudioDescriptionState::Playing };
        if !needs_switch {
            return;
        }
        self.playing.description_announced_for = self.playing.details.as_ref().map(|d| d.id.clone());
        let coordinator = self.coordinator.clone();
        let at = self.playing.position;
        self.jobs.run("audio description", move || {
            let result = coordinator.replay_current(at);
            move |app: &mut App| {
                if let Err(e) = result {
                    log::warn!("Switching audio description failed: {}", e.message);
                    app.show_problem(&t("Could not play this video"), Some(&e.message), true);
                }
            }
        });
    }

    // ----- Captions -----

    /// What the Captions menu offers: off, then each track.
    pub(crate) fn caption_choices(&self) -> Vec<(Option<CaptionTrack>, String, bool)> {
        let selected = self.coordinator.selected_caption();
        let mut choices = vec![(None, t("Captions Off"), selected.is_none())];
        for track in self.coordinator.captions() {
            let chosen = selected.as_ref() == Some(&track);
            choices.push((Some(track.clone()), track.label(), chosen));
        }
        choices
    }

    fn populate_caption_choices(&self) {}

    pub(crate) fn select_caption_track(&mut self, track: Option<CaptionTrack>) {
        let label = match &track {
            Some(t) => tf("Subtitles switched to {0}", &[&t.label()]),
            None => t("Subtitles turned off"),
        };
        if self.coordinator.set_caption_track(track.as_ref()).is_ok() {
            speech::say_quietly(&label);
        }
    }

    // ----- Sound -----

    pub(crate) fn active_sound_preset(&self) -> String {
        self.settings.with(|c| if self.music_mode { c.audio.music_preset.clone() } else { c.audio.preset.clone() })
    }

    pub(crate) fn apply_sound(&self) {
        let (preset, custom) = self.settings.with(|c| (if self.music_mode { c.audio.music_preset.clone() } else { c.audio.preset.clone() }, c.audio.custom.clone()));
        let chain = audio::filter_chain(&audio::settings_for(&preset, &custom));
        self.coordinator.engine().set_audio_filters(&chain);
    }

    pub(crate) fn select_sound_preset(&mut self, name: &str) {
        if !audio::preset_names().contains(&name) {
            return;
        }
        let music = self.music_mode;
        self.settings.update(|c| if music { c.audio.music_preset = name.to_owned() } else { c.audio.preset = name.to_owned() });
        self.apply_sound();
        speech::say_quietly(&tf("{0} sound.", &[&t(name)]));
    }

    /// E and Shift+E.
    pub(crate) fn cycle_sound_preset(&mut self, direction: i32) {
        let names = audio::preset_names();
        let index = names.iter().position(|n| *n == self.active_sound_preset()).unwrap_or(0);
        let next = names[(index + if direction < 0 { names.len() - 1 } else { 1 }) % names.len()];
        self.select_sound_preset(next);
    }

    /// Control+M.
    pub(crate) fn toggle_music_mode(&mut self) {
        self.music_mode = !self.music_mode;
        let music = self.music_mode;
        self.settings.update(|c| c.audio.music_mode = music);
        self.coordinator.engine().set_video_enabled(!music);
        self.apply_sound();
        speech::say(&if music {
            tf("Music. {0} sound.", &[&t(&self.active_sound_preset())])
        } else {
            tf("Video. {0} sound.", &[&t(&self.active_sound_preset())])
        });
        self.apply_app_mode(true);
    }

    pub(crate) fn apply_app_mode(&mut self, announce: bool) {
        let speak = self.settings.with(|c| c.accessibility.speak_captions);
        self.coordinator.look_up_missing_captions.store(!self.music_mode || speak, std::sync::atomic::Ordering::Relaxed);
        self.browse.tabs = self.section_tabs();
        self.ui.set_section_tabs(&self.browse.tabs);
        self.ui.select_section_tab(0);
        self.clear_navigation();
        self.update_title();
        if self.playing.session_open {
            self.set_player_visible(!self.music_mode);
        }
        if !announce {
            return;
        }
        if !self.player_visible {
            let first = self.browse.tabs[0];
            self.show_section(first, true, false);
        } else {
            self.ui.play.set_focus();
        }
    }

    pub(crate) fn choose_audio_device(&mut self, device: &str, label: &str) {
        let device = device.to_owned();
        self.coordinator.engine().set_audio_device(&device);
        self.settings.update(|c| c.playback.audio_device = device.clone());
        speech::say_quietly(&tf("Sound plays through {0}.", &[&label]));
    }

    // ----- The watch page: Up next, comments, description, votes, segments -----

    fn reset_watch_page(&mut self, video_id: Option<String>) {
        if video_id == self.playing.watch_page_for {
            return;
        }
        self.playing.watch_generation += 1;
        self.playing.watch_page_for = video_id.clone();
        self.playing.up_next.clear();
        self.ui.up_next.update_labelled(Vec::new(), quiet_list::Select::Start);
        self.reset_comments();
        self.set_description(String::new());
        self.reset_transcript();
        self.reset_lyrics();
        self.playing.segments.reset(video_id.as_deref(), Vec::new());
        self.playing.votes = None;
        let Some(video_id) = video_id else { return };
        let generation = self.playing.watch_generation;
        let watch_page = self.watch_page.clone();
        let id = video_id.clone();
        self.jobs.run("up next", move || {
            let result = watch_page.watch_next(&id);
            move |app: &mut App| {
                if app.playing.watch_generation != generation {
                    return;
                }
                if let Ok(next) = result {
                    let related: Vec<SearchResult> = next.related.into_iter().filter(|v| v.id != id).collect();
                    app.playing.up_next = related.into_iter().map(|v| app.decorate(v, None)).collect();
                    app.show_up_next();
                    if let Some(d) = next.description.filter(|d| !d.trim().is_empty())
                        && app.playing.description.trim().is_empty()
                    {
                        app.set_description(d);
                    }
                }
            }
        });
        let http = self.http.clone();
        let settings = self.settings.clone();
        let id = video_id.clone();
        self.jobs.run("segments", move || {
            let segments = if settings.with(|c| c.sponsor_block.enabled) { community::sponsor_segments(&http, &settings, &id) } else { Vec::new() };
            move |app: &mut App| {
                if app.playing.segments.video_id.as_deref() == Some(&id) {
                    app.playing.segments.reset(Some(&id), segments);
                }
            }
        });
        let http = self.http.clone();
        let settings = self.settings.clone();
        self.jobs.run("votes", move || {
            let votes = if settings.with(|c| c.dislikes.enabled) { community::votes(&http, &settings, &video_id) } else { None };
            move |app: &mut App| {
                if app.playing.current_id().as_deref() == Some(&video_id) {
                    app.playing.votes = votes;
                    app.show_description();
                }
            }
        });
        if self.selected_panel() == Some(PlayerPanel::Comments) {
            self.load_comments(false);
        }
    }

    fn show_up_next(&self) {
        let rows = self.playing.up_next.iter().enumerate().map(|(i, v)| (i, v.accessible_summary())).collect();
        self.ui.up_next.update_labelled(rows, quiet_list::Select::Start);
    }

    pub(crate) fn selected_up_next(&self) -> Option<SearchResult> {
        self.ui.up_next.selected_index().and_then(|i| self.playing.up_next.get(i).cloned())
    }

    pub(crate) fn play_selected_up_next(&mut self) {
        if let Some(item) = self.selected_up_next() {
            self.play_item(item, false);
        }
    }

    fn set_description(&mut self, description: String) {
        self.playing.description = description;
        self.show_description();
        self.refresh_chapters();
    }

    fn show_description(&self) {
        let description = if self.playing.description.trim().is_empty() { t("No description.") } else { self.playing.description.clone() };
        let text = match &self.playing.votes {
            Some(votes) => tf("{0} (dislikes estimated by Return YouTube Dislike).", &[&votes.spoken()]) + "\n\n" + &description,
            None => description,
        };
        self.ui.description.set_value(&text);
    }

    pub(crate) fn selected_panel(&self) -> Option<PlayerPanel> {
        usize::try_from(self.ui.panels.selection()).ok().and_then(PlayerPanel::from_index)
    }

    /// A panel tab came forward: Comments, Transcript and Lyrics load the first time.
    pub(crate) fn player_panel_shown(&mut self, panel: PlayerPanel) {
        match panel {
            PlayerPanel::Comments if !self.playing.comments.requested => self.load_comments(true),
            PlayerPanel::Transcript => self.load_transcript(),
            PlayerPanel::Lyrics => self.load_lyrics(true),
            _ => {}
        }
    }

    /// Alt+U, Alt+C and the rest: the panel, and focus in it.
    pub(crate) fn show_player_panel(&mut self, panel: PlayerPanel) {
        if self.selected_panel() != Some(panel) {
            self.ui.panels.set_selection(panel as usize);
        } else {
            self.player_panel_shown(panel);
        }
        self.focus_panel(panel);
    }

    fn focus_panel(&self, panel: PlayerPanel) {
        match panel {
            PlayerPanel::UpNext if !self.ui.up_next.is_empty() => self.ui.up_next.set_focus(),
            PlayerPanel::Comments if self.ui.comments.get_count() > 0 => self.ui.comments.set_focus(),
            PlayerPanel::Description => self.ui.description.set_focus(),
            PlayerPanel::Queue if !self.ui.queue.is_empty() => self.ui.queue.set_focus(),
            PlayerPanel::Chapters if !self.ui.chapters.is_empty() => self.ui.chapters.set_focus(),
            PlayerPanel::Transcript if !self.ui.transcript.is_empty() => self.ui.transcript.set_focus(),
            PlayerPanel::Lyrics if !self.ui.lyrics.is_empty() => self.ui.lyrics.set_focus(),
            _ => self.ui.panels.set_focus(),
        }
    }

    pub(crate) fn focus_selected_panel(&self) {
        self.focus_panel(self.selected_panel().unwrap_or(PlayerPanel::UpNext));
    }

    // ----- Comments -----

    fn reset_comments(&mut self) {
        self.playing.comments = Comments::default();
        self.ui.comments.delete_all_items();
        self.ui.comments.add_root("", None, None);
        self.ui.comments_status.set_label(&t("Open this tab to load comments."));
    }

    fn can_load_more_comments(&self) -> bool {
        self.playing.comments.last_page.as_ref().is_some_and(|p| p.next_token.as_ref().is_some_and(|t| !t.is_empty()))
    }

    fn load_comments(&mut self, announce: bool) {
        let Some(video_id) = self.playing.watch_page_for.clone() else { return };
        if self.playing.comments.loading || (self.playing.comments.requested && !self.can_load_more_comments()) {
            return;
        }
        let first_page = self.playing.comments.last_page.is_none();
        self.playing.comments.requested = true;
        self.playing.comments.loading = true;
        if first_page {
            self.ui.comments_status.set_label(&t("Loading comments..."));
        }
        let watch_page = self.watch_page.clone();
        let previous = self.playing.comments.last_page.clone();
        let generation = self.playing.watch_generation;
        self.jobs.run("comments", move || {
            let result = watch_page.comments(&video_id, previous.as_ref());
            move |app: &mut App| {
                if app.playing.watch_generation != generation {
                    return;
                }
                app.comments_loaded(result, first_page, announce);
            }
        });
    }

    fn comments_loaded(&mut self, result: timeplayer_core::Result<CommentPage>, first_page: bool, announce: bool) {
        self.playing.comments.loading = false;
        let speak_more = std::mem::take(&mut self.playing.comments.speak_next);
        let page = match result {
            Ok(page) => page,
            Err(_) => {
                if first_page {
                    let status = t("Comments could not be loaded. They may be turned off for this video.");
                    self.ui.comments_status.set_label(&status);
                    if announce {
                        speech::say_quietly(&status);
                    }
                }
                return;
            }
        };
        let Some(root) = self.ui.comments.get_root_item() else { return };
        let mut added = 0;
        let mut first_item = None;
        for mut comment in page.comments.clone() {
            if !self.playing.comments.top_level_ids.insert(comment.id.clone()) {
                continue;
            }
            comment.is_reply = false;
            let id = self.add_comment_node(&root, Node::Comment { comment, level: 1, replies_loaded: false });
            self.playing.comments.top_level.push(id.0);
            first_item.get_or_insert(id.1);
            added += 1;
        }
        self.playing.comments.last_page = Some(page);
        let count = self.playing.comments.top_level.len();
        if first_page {
            self.ui.comments_status.set_label(&if count == 0 { t("No comments yet.") } else { String::new() });
            if let Some(item) = first_item {
                self.ui.comments.select_item(&item);
            }
            if announce {
                speech::say_quietly(&if count == 0 { t("No comments yet.") } else { tf("{0} loaded.", &[&plural(count as i64, "{0} comment", "{0} comments")]) });
            }
            // Comments that arrive while the user waits on the Comments tab: into the list.
            if count > 0 && self.ui.panels.has_focus() && self.selected_panel() == Some(PlayerPanel::Comments) {
                self.ui.comments.set_focus();
            }
        } else if speak_more && added > 0 {
            speech::say_quietly(&tf("{0} more.", &[&plural(added as i64, "{0} comment", "{0} comments")]));
        }
    }

    /// Adds a node under `parent`; a comment with replies can be opened.
    fn add_comment_node(&mut self, parent: &TreeItemId, node: Node) -> (u64, TreeItemId) {
        let label = match &node {
            Node::Comment { comment, .. } => comment.accessible_summary(),
            Node::MoreReplies { .. } => t("More replies, press Enter to load"),
        };
        let id = self.playing.comments.next_id;
        self.playing.comments.next_id += 1;
        let item = self.ui.comments.append_item_with_data(parent, &label, id, None, None).expect("a tree item");
        if let Node::Comment { comment, .. } = &node
            && comment.has_replies()
            && !comment.is_reply
        {
            self.ui.comments.set_item_has_children(&item, true);
        }
        self.playing.comments.nodes.insert(id, node);
        self.playing.comments.items.insert(id, item.clone());
        (id, item)
    }

    fn node_id(&self, item: &TreeItemId) -> Option<u64> {
        self.ui.comments.get_custom_data_direct(item).and_then(|d| d.downcast_ref::<u64>().copied())
    }

    /// Right arrow, or a click on the expander: replies load the first time.
    pub(crate) fn comment_expanding(&mut self, item: &TreeItemId) {
        let Some(id) = self.node_id(item) else { return };
        if let Some(Node::Comment { replies_loaded: false, .. }) = self.playing.comments.nodes.get(&id) {
            self.load_replies(id, None, None);
        }
    }

    fn load_replies(&mut self, parent_id: u64, page_token: Option<String>, replace: Option<u64>) {
        let Some(video_id) = self.playing.watch_page_for.clone() else { return };
        let Some(Node::Comment { comment, level, .. }) = self.playing.comments.nodes.get_mut(&parent_id).map(|n| {
            if let Node::Comment { replies_loaded, .. } = n {
                *replies_loaded = true;
            }
            n.clone()
        }) else {
            return;
        };
        let mut source = comment.clone();
        if let Some(token) = &page_token {
            source.replies_token = Some(token.clone());
        }
        let watch_page = self.watch_page.clone();
        let generation = self.playing.watch_generation;
        self.jobs.run("replies", move || {
            let result = watch_page.replies(&video_id, &source);
            move |app: &mut App| {
                if app.playing.watch_generation != generation {
                    return;
                }
                if let Some(item) = replace.and_then(|id| {
                    app.playing.comments.nodes.remove(&id);
                    app.playing.comments.items.remove(&id)
                }) {
                    app.ui.comments.delete(&item);
                }
                let Some(parent_item) = app.playing.comments.items.get(&parent_id).cloned() else { return };
                let page = match result {
                    Ok(page) if !page.comments.is_empty() => page,
                    _ => {
                        if page_token.is_none() {
                            if let Some(Node::Comment { replies_loaded, .. }) = app.playing.comments.nodes.get_mut(&parent_id) {
                                *replies_loaded = false;
                            }
                            app.ui.comments.collapse(&parent_item);
                            speech::say_quietly(&t("Replies could not be loaded."));
                        }
                        return;
                    }
                };
                let count = page.comments.len();
                for mut reply in page.comments {
                    reply.is_reply = true;
                    app.add_comment_node(&parent_item, Node::Comment { comment: reply, level: level + 1, replies_loaded: true });
                }
                if let Some(token) = page.next_token.filter(|t| !t.is_empty()) {
                    app.add_comment_node(&parent_item, Node::MoreReplies { parent: comment.clone(), token });
                }
                if page_token.is_none() {
                    app.ui.comments.expand(&parent_item);
                    speech::say_quietly(&plural(count as i64, "{0} reply", "{0} replies"));
                }
            }
        });
    }

    /// Enter on a comment: open or close its replies; on More replies, load them.
    pub(crate) fn toggle_replies(&mut self) {
        let Some(item) = self.ui.comments.get_selection() else { return };
        let Some(id) = self.node_id(&item) else { return };
        match self.playing.comments.nodes.get(&id).cloned() {
            Some(Node::MoreReplies { parent, token }) => {
                let Some(parent_item) = self.ui.comments.get_item_parent(&item) else { return };
                let Some(parent_id) = self.node_id(&parent_item) else { return };
                let _ = parent;
                self.load_replies(parent_id, Some(token), Some(id));
            }
            Some(Node::Comment { comment, .. }) => {
                if !comment.has_replies() || comment.is_reply {
                    speech::say_quietly(&t("No replies."));
                } else if self.ui.comments.is_expanded(&item) {
                    self.ui.comments.collapse(&item);
                } else {
                    self.ui.comments.expand(&item);
                }
            }
            None => {}
        }
    }

    /// Near the end of the comments, more load.
    pub(crate) fn comment_selection_changed(&mut self) {
        let Some(item) = self.ui.comments.get_selection() else { return };
        let Some(id) = self.node_id(&item) else { return };
        let Some(index) = self.playing.comments.top_level.iter().position(|c| *c == id) else { return };
        let count = self.playing.comments.top_level.len();
        if index + 5 >= count && self.can_load_more_comments() {
            if index + 1 == count {
                self.playing.comments.speak_next = true;
            }
            self.load_comments(false);
        }
    }

    /// Control+C on a comment.
    pub(crate) fn copy_comment(&mut self) {
        let Some(item) = self.ui.comments.get_selection() else { return };
        let Some(id) = self.node_id(&item) else { return };
        if let Some(Node::Comment { comment, .. }) = self.playing.comments.nodes.get(&id)
            && self.copy_to_clipboard(&format!("{}: {}", comment.author, comment.text))
        {
            speech::say_quietly(&t("Comment copied."));
        }
    }

    // ----- SponsorBlock -----

    fn on_segment_position(&mut self, position: f64) {
        let current = self.playing.current_id();
        if self.playing.skipping_segment
            || self.playing.state != PlaybackState::Playing
            || self.playing.segments.video_id.is_none()
            || self.playing.segments.video_id != current
            || self.playing.resume_handled_for != current
            || self.playing.segments_quiet_until.is_some_and(|until| Instant::now() < until)
        {
            return;
        }
        let settings = self.settings.with(|c| c.sponsor_block.clone());
        for event in self.playing.segments.update(position, &settings) {
            match event {
                SegmentEvent::Skip(segment) => self.skip_segment(&segment, true),
                SegmentEvent::Announce(segment) => {
                    speech::say_quietly(&tf("{0}, {1}. S skips it.", &[&capitalize(&segment.name()), &text::time_spoken_secs(segment.length())]));
                }
                SegmentEvent::WholeVideo(segment) => speech::say_quietly(&tf("This whole video is labeled {0}.", &[&segment.name()])),
                SegmentEvent::HighlightAvailable(segment) => {
                    speech::say_quietly(&tf("Highlight at {0}. H jumps to it.", &[&text::time_spoken_secs(segment.start)]));
                }
            }
        }
    }

    fn skip_segment(&mut self, segment: &community::Segment, automatic: bool) {
        self.playing.skipping_segment = true;
        let target = self.clamp_to_duration(segment.end);
        self.seek_to(target);
        if !automatic || self.settings.with(|c| c.sponsor_block.announce_skips) {
            speech::say_quietly(&tf("Skipped {0}, {1}.", &[&segment.name(), &text::time_spoken_secs(segment.length())]));
        }
        self.playing.skipping_segment = false;
    }

    /// S.
    pub(crate) fn skip_current_segment(&mut self) {
        let settings = self.settings.with(|c| c.sponsor_block.clone());
        if !settings.enabled {
            speech::say(&t("SponsorBlock is turned off in Settings."));
            return;
        }
        match self.playing.segments.segment_at(self.playing.position, &settings).cloned() {
            Some(segment) => self.skip_segment(&segment, false),
            None => speech::say(&if self.playing.segments.segments().is_empty() { t("No SponsorBlock segments for this video.") } else { t("No segment playing now.") }),
        }
    }

    /// H.
    pub(crate) fn jump_to_highlight(&mut self) {
        let enabled = self.settings.with(|c| c.sponsor_block.enabled);
        let Some(highlight) = self.playing.segments.highlight().cloned().filter(|_| enabled) else {
            speech::say(&t("This video has no highlight."));
            return;
        };
        self.seek_to(highlight.start);
        speech::say_quietly(&tf("Highlight, {0}.", &[&text::time_spoken_secs(highlight.start)]));
    }

    fn segment_summary(&self) -> Option<String> {
        if !self.settings.with(|c| c.sponsor_block.enabled) || self.playing.segments.video_id != self.playing.current_id() {
            return None;
        }
        let skippable: Vec<_> = self.playing.segments.segments().iter().filter(|s| s.kind == SegmentKind::Skip).collect();
        match skippable.len() {
            0 => None,
            1 => Some(tf("1 SponsorBlock segment: {0}", &[&skippable[0].name()])),
            n => Some(plural(n as i64, "{0} SponsorBlock segment", "{0} SponsorBlock segments")),
        }
    }

    // ----- Chapters -----

    fn refresh_chapters(&mut self) {
        let duration = self.playing.effective_duration();
        self.playing.chapters = chapters::from_description(&self.playing.description, duration);
        self.playing.last_chapter = None;
        let rows = self.playing.chapters.iter().enumerate().map(|(i, c)| (i, c.label())).collect();
        self.ui.chapters.update_labelled(rows, quiet_list::Select::Start);
        self.update_braille();
    }

    fn on_chapter_position(&mut self, position: f64) {
        let Some(chapter) = chapters::at(&self.playing.chapters, position).cloned() else { return };
        if self.playing.last_chapter == Some(chapter.number) {
            return;
        }
        let first = self.playing.last_chapter.is_none();
        self.playing.last_chapter = Some(chapter.number);
        if !first && self.settings.with(|c| c.accessibility.announce_chapters) {
            speech::say_quietly(&tf("Chapter {0}: {1}", &[&chapter.number, &chapter.title]));
        }
    }

    fn current_chapter_spoken(&self) -> Option<String> {
        let chapter = chapters::at(&self.playing.chapters, self.playing.position)?;
        Some(tf("Chapter {0} of {1}: {2}.", &[&chapter.number, &self.playing.chapters.len(), &chapter.title]))
    }

    pub(crate) fn next_chapter(&mut self) {
        let chapter = chapters::next(&self.playing.chapters, self.playing.position).cloned();
        self.go_to_chapter(chapter, Some(t("This is the last chapter.")));
    }

    pub(crate) fn previous_chapter(&mut self) {
        let chapter = chapters::previous(&self.playing.chapters, self.playing.position).cloned();
        self.go_to_chapter(chapter, None);
    }

    pub(crate) fn play_selected_chapter(&mut self) {
        let chapter = self.ui.chapters.selected_index().and_then(|i| self.playing.chapters.get(i).cloned());
        self.go_to_chapter(chapter, None);
    }

    fn go_to_chapter(&mut self, chapter: Option<Chapter>, when_none: Option<String>) {
        if self.playing.chapters.is_empty() {
            speech::say(&t("This video has no chapters."));
            return;
        }
        let Some(chapter) = chapter else {
            if let Some(text) = when_none {
                speech::say(&text);
            }
            return;
        };
        if !self.playing.session_open {
            return;
        }
        self.playing.last_chapter = Some(chapter.number);
        speech::say(&tf("Chapter {0} of {1}: {2}.", &[&chapter.number, &self.playing.chapters.len(), &chapter.title]));
        self.seek_to(chapter.start);
    }

    // ----- Transcript -----

    fn reset_transcript(&mut self) {
        self.playing.transcript.clear();
        self.playing.transcript_for = None;
        self.ui.transcript.update_labelled(Vec::new(), quiet_list::Select::Start);
        self.ui.transcript_status.set_label(&t("Open this tab to load the transcript."));
    }

    fn choose_transcript_track(&self) -> Option<CaptionTrack> {
        if let Some(chosen) = self.coordinator.selected_caption() {
            return Some(chosen);
        }
        let tracks = self.coordinator.captions();
        let current = timeplayer_core::i18n::current();
        let speaks = |t: &CaptionTrack, lang: &str| t.language_code.to_lowercase().starts_with(lang);
        tracks
            .iter()
            .find(|t| speaks(t, current) && !t.auto_generated)
            .or_else(|| tracks.iter().find(|t| speaks(t, "en") && !t.auto_generated))
            .or_else(|| tracks.iter().find(|t| !t.auto_generated))
            .or_else(|| tracks.iter().find(|t| speaks(t, current)))
            .or_else(|| tracks.first())
            .cloned()
    }

    fn load_transcript(&mut self) {
        let Some(video_id) = self.playing.details.as_ref().map(|d| d.id.clone()) else { return };
        if self.playing.transcript_for.as_deref() == Some(&video_id) {
            return;
        }
        self.playing.transcript_for = Some(video_id.clone());
        let Some(track) = self.choose_transcript_track() else {
            let status = t("This video has no captions, so there is no transcript.");
            self.ui.transcript_status.set_label(&status);
            speech::say_quietly(&status);
            return;
        };
        self.ui.transcript_status.set_label(&t("Loading the transcript..."));
        let http = self.http.clone();
        self.jobs.run("transcript", move || {
            let result = community::load_transcript(&http, &track.url);
            move |app: &mut App| {
                if app.playing.details.as_ref().map(|d| &d.id) != Some(&video_id) {
                    return;
                }
                match result {
                    Err(e) => {
                        app.playing.transcript_for = None;
                        let status = if e.message.is_empty() { t("The captions could not be loaded.") } else { e.message };
                        app.ui.transcript_status.set_label(&status);
                        speech::say_quietly(&status);
                    }
                    Ok(lines) => {
                        let at = lines.iter().rposition(|l| l.start <= app.playing.position).unwrap_or(0);
                        let rows = lines.iter().enumerate().map(|(i, l)| (i, l.label())).collect();
                        app.playing.transcript = lines;
                        app.ui.transcript.update_labelled(rows, quiet_list::Select::Key(at));
                        app.ui.transcript_status.set_label("");
                        speech::say_quietly(&tf("Transcript, {0}.", &[&plural(app.playing.transcript.len() as i64, "{0} line", "{0} lines")]));
                        if app.selected_panel() == Some(PlayerPanel::Transcript) && app.ui.panels.has_focus() {
                            app.ui.transcript.set_focus();
                        }
                    }
                }
            }
        });
    }

    pub(crate) fn play_from_transcript_line(&mut self) {
        let Some(line) = self.ui.transcript.selected_index().and_then(|i| self.playing.transcript.get(i).cloned()) else { return };
        if !self.playing.session_open {
            return;
        }
        speech::say_quietly(&tf("Playing from {0}.", &[&text::time_spoken_secs(line.start)]));
        self.seek_to(line.start);
        if self.playing.state == PlaybackState::Paused {
            let _ = self.coordinator.toggle_play_pause();
        }
    }

    // ----- Lyrics -----

    fn reset_lyrics(&mut self) {
        self.playing.lyrics.clear();
        self.playing.lyrics_for = None;
        self.playing.lyrics_failed = false;
        self.playing.last_sung = None;
        self.ui.lyrics.update_labelled(Vec::new(), quiet_list::Select::Start);
        self.ui.lyrics_status.set_label(&t("Open this tab to look up the lyrics."));
    }

    fn load_lyrics(&mut self, announce: bool) {
        let Some(video) = self.playing.details.clone() else { return };
        if self.playing.lyrics_loading || (self.playing.lyrics_for.as_deref() == Some(&video.id) && !(announce && self.playing.lyrics_failed)) {
            return;
        }
        self.playing.lyrics_failed = false;
        self.playing.lyrics_loading = true;
        self.playing.lyrics_for = Some(video.id.clone());
        self.ui.lyrics_status.set_label(&t("Looking up the lyrics..."));
        let http = self.http.clone();
        let duration = self.playing.effective_duration();
        self.jobs.run("lyrics", move || {
            let result = community::find_lyrics(&http, &video.title, &video.channel.name, duration);
            move |app: &mut App| {
                app.playing.lyrics_loading = false;
                if app.playing.details.as_ref().map(|d| &d.id) != Some(&video.id) {
                    return;
                }
                match result {
                    Err(e) => {
                        app.playing.lyrics_failed = e.code != "NO_LYRICS";
                        let status = if e.message.is_empty() { t("No lyrics were found for this song.") } else { e.message };
                        app.ui.lyrics_status.set_label(&status);
                        if announce {
                            speech::say_quietly(&status);
                        }
                    }
                    Ok(song) => {
                        let at = song.lines.iter().rposition(|l| l.start.is_some_and(|s| s <= app.playing.position)).unwrap_or(0);
                        let rows = song.lines.iter().enumerate().map(|(i, l)| (i, l.label())).collect();
                        app.playing.lyrics = song.lines.clone();
                        app.ui.lyrics.update_labelled(rows, quiet_list::Select::Key(at));
                        app.ui.lyrics_status.set_label("");
                        if announce {
                            speech::say_quietly(&tf("Lyrics: {0} by {1}.", &[&song.track, &song.artist]));
                        }
                        if app.selected_panel() == Some(PlayerPanel::Lyrics) && app.ui.panels.has_focus() {
                            app.ui.lyrics.set_focus();
                        }
                    }
                }
            }
        });
    }

    /// L.
    pub(crate) fn toggle_speak_lyrics(&mut self) {
        let on = !self.settings.with(|c| c.accessibility.speak_lyrics);
        self.settings.update(|c| c.accessibility.speak_lyrics = on);
        if !on {
            speech::say(&t("Lyrics will not be read aloud."));
            return;
        }
        speech::say(&t("Lyrics will be read aloud as they're sung."));
        self.load_lyrics(false);
        if !self.playing.lyrics.is_empty() && self.playing.lyrics.iter().any(|l| l.start.is_none()) {
            speech::say_quietly(&t("These lyrics have no timing, so they can't be read along; Alt+L shows them."));
        }
    }

    fn on_lyrics_position(&mut self, position: f64) {
        if !self.settings.with(|c| c.accessibility.speak_lyrics) || self.playing.state != PlaybackState::Playing {
            return;
        }
        if self.playing.lyrics.is_empty() {
            if self.playing.details.as_ref().map(|d| &d.id) != self.playing.lyrics_for.as_ref() {
                self.load_lyrics(false);
            }
            return;
        }
        let Some(index) = self.playing.lyrics.iter().rposition(|l| l.start.is_some_and(|s| s <= position + 0.15)) else { return };
        if self.playing.last_sung == Some(index) {
            return;
        }
        self.playing.last_sung = Some(index);
        speech::say_caption(&self.playing.lyrics[index].text);
    }

    pub(crate) fn play_from_lyric_line(&mut self) {
        let Some(index) = self.ui.lyrics.selected_index() else { return };
        let Some(start) = self.playing.lyrics.get(index).and_then(|l| l.start) else { return };
        if !self.playing.session_open {
            return;
        }
        self.playing.last_sung = Some(index);
        self.seek_to(start);
    }

    // ----- Radio -----

    /// R in the player, Control+R on a list row.
    pub(crate) fn start_radio(&mut self, item: Option<SearchResult>) {
        let from_player = item.is_none();
        let Some(seed) = item.or_else(|| self.current_video_for_actions()).filter(|s| s.is_video()) else {
            speech::say(&t("Choose a song first."));
            return;
        };
        speech::say_quietly(&tf("Starting a radio from {0}...", &[&seed.title]));
        let inner_tube = self.sources.inner_tube.clone();
        self.jobs.run("radio", move || {
            let radio = inner_tube.radio(&seed.id);
            move |app: &mut App| match radio {
                Err(e) => speech::say(&if e.message.is_empty() { t("YouTube has no radio for this song.") } else { e.message }),
                Ok(songs) => {
                    let playing_seed = from_player && app.playing.details.as_ref().is_some_and(|d| d.id == seed.id);
                    let songs: Vec<SearchResult> = songs
                        .into_iter()
                        .filter(|s| s.is_video() && !(playing_seed && s.id == seed.id))
                        .map(|mut s| {
                            s.status_prefix = None;
                            s
                        })
                        .collect();
                    app.playing.radio_songs.clear();
                    app.playing.radio_songs.insert(seed.id.clone());
                    app.playing.radio_songs.extend(songs.iter().map(|s| s.id.clone()));
                    app.coordinator.replace_queue(&songs);
                    speech::say_quietly(&tf("Radio: {0} queued, and more follow.", &[&plural(songs.len() as i64, "{0} song", "{0} songs")]));
                    if !playing_seed && let Some(first) = songs.first().cloned() {
                        app.play_item(first, false);
                    }
                }
            }
        });
    }

    fn continue_radio(&mut self) -> bool {
        let Some(last) = self.playing.details.as_ref().map(|d| d.id.clone()) else { return false };
        if !self.playing.radio_songs.contains(&last) || self.playing.radio_refilling {
            return false;
        }
        self.playing.radio_refilling = true;
        let inner_tube = self.sources.inner_tube.clone();
        self.jobs.run("radio more", move || {
            let more = inner_tube.radio(&last);
            move |app: &mut App| {
                app.playing.radio_refilling = false;
                let fresh: Vec<SearchResult> = more
                    .unwrap_or_default()
                    .into_iter()
                    .filter(|s| s.is_video() && app.playing.radio_songs.insert(s.id.clone()))
                    .map(|mut s| {
                        s.status_prefix = None;
                        s
                    })
                    .collect();
                let Some(first) = fresh.first().cloned() else {
                    speech::say_quietly(&t("The radio has run out of songs."));
                    return;
                };
                app.coordinator.replace_queue(&fresh);
                app.play_item(first, false);
            }
        });
        true
    }

    // ----- Queue in the player -----

    pub(crate) fn show_player_queue(&self) {
        let queue = self.coordinator.queue();
        let current = self.coordinator.current_item().map(|q| q.queue_id);
        let rows = queue
            .iter()
            .enumerate()
            .map(|(i, q)| {
                let mut label = q.video.accessible_summary();
                if current == Some(q.queue_id) {
                    label = format!("{}. {label}", t("Now playing"));
                } else if q.played {
                    label = format!("{label}. {}", t("Played"));
                }
                (i, label)
            })
            .collect();
        self.ui.queue.update_labelled(rows, quiet_list::Select::Keep);
        self.ui.previous.enable(self.can_play_previous());
        self.ui.now_previous.enable(self.can_play_previous());
    }

    pub(crate) fn play_queue_item(&mut self, index: usize) {
        let Some(item) = self.coordinator.queue().get(index).cloned() else { return };
        self.begin_new_playback();
        self.playing.title = item.video.title.clone();
        self.playing.channel = item.video.channel_title.clone();
        self.set_current_library_video(Some(item.video.clone()));
        self.open_player_session();
        speech::say(&tf("Loading {0}", &[&item.video.title]));
        self.focus_player_if_shown();
        let coordinator = self.coordinator.clone();
        self.jobs.run("play queued", move || {
            let result = coordinator.play_queue_item(item.queue_id);
            move |app: &mut App| {
                if let Err(e) = result {
                    app.show_problem(&t("Could not play this video"), Some(&e.message), true);
                }
            }
        });
    }

    pub(crate) fn play_selected_queue_item(&mut self) {
        if let Some(index) = self.ui.queue.selected_index() {
            self.play_queue_item(index);
        }
    }

    pub(crate) fn remove_queue_item(&mut self, index: usize) {
        if let Some(item) = self.coordinator.queue().get(index) {
            self.coordinator.remove_from_queue(item.queue_id);
            speech::say_quietly(&t("Removed from queue."));
        }
    }

    pub(crate) fn clear_queue(&mut self) {
        self.coordinator.clear_queue();
        speech::say_quietly(&t("Playback queue cleared."));
    }

    // ----- Shorts -----

    /// Control+Shift+S.
    pub(crate) fn start_shorts(&mut self) {
        self.playing.shorts = Shorts::default();
        let on_screen: Vec<SearchResult> = self.browse.items.iter().filter(|r| r.is_short).cloned().collect();
        if !on_screen.is_empty() {
            let selected = self.selected_item().filter(|s| s.is_short);
            self.playing.shorts.index = selected.and_then(|s| on_screen.iter().position(|o| o.id == s.id)).unwrap_or(0);
            self.playing.shorts.items = on_screen;
            if self.browse.can_load_more() {
                self.playing.shorts.from_list = true;
                self.playing.shorts.has_more = true;
            }
            self.begin_shorts();
            return;
        }
        let from_channels: Vec<SearchResult> = self.browse.feed.as_ref().map(|f| f.videos.iter().filter(|v| v.is_short).cloned().collect()).unwrap_or_default();
        if !from_channels.is_empty() {
            self.playing.shorts.items = from_channels;
            self.begin_shorts();
            return;
        }
        let mut channels: Vec<String> = Vec::new();
        for entry in self.library.history().into_iter().take(20) {
            let name = entry.channel_title.clone();
            if !name.trim().is_empty() && !channels.contains(&name) {
                channels.push(name);
            }
            if channels.len() == 2 {
                break;
            }
        }
        let query = if channels.is_empty() { "trending".to_owned() } else { channels.join(" ") };
        speech::say_quietly(&t("Finding Shorts..."));
        let inner_tube = self.sources.inner_tube.clone();
        self.jobs.run("shorts", move || {
            let found = inner_tube.search_shorts(&query, None);
            move |app: &mut App| {
                if let Ok(page) = found {
                    app.playing.shorts.items = page.items.into_iter().filter(|i| i.is_short).collect();
                    app.playing.shorts.has_more = page.next_token.is_some();
                    app.playing.shorts.query = Some((query, page.next_token));
                }
                app.begin_shorts();
            }
        });
    }

    fn begin_shorts(&mut self) {
        if self.playing.shorts.items.is_empty() {
            speech::say(&t("No Shorts were found."));
            return;
        }
        self.playing.shorts.active = true;
        self.coordinator.engine().set_loop(true);
        let queue: Vec<SearchResult> = self.playing.shorts.items.iter().cloned().map(|mut s| {
            s.status_prefix = None;
            s
        }).collect();
        self.coordinator.replace_queue(&queue);
        speech::say_quietly(&t("Shorts. Up and Down move between them, and each one repeats."));
        self.play_short();
    }

    pub(crate) fn next_short(&mut self) {
        if !self.playing.shorts.active {
            return;
        }
        if self.playing.shorts.index + 1 >= self.playing.shorts.items.len() {
            if self.playing.shorts.has_more {
                self.load_more_shorts(true);
            } else {
                speech::say(&t("That was the last Short."));
            }
            return;
        }
        self.playing.shorts.index += 1;
        self.play_short();
    }

    pub(crate) fn previous_short(&mut self) {
        if !self.playing.shorts.active {
            return;
        }
        if self.playing.shorts.index == 0 {
            speech::say(&t("This is the first Short."));
            return;
        }
        self.playing.shorts.index -= 1;
        self.play_short();
    }

    /// Loads more Shorts; `then_play_next` moves on to the first new one when it was asked for by Down.
    fn load_more_shorts(&mut self, then_play_next: bool) {
        if self.playing.shorts.loading {
            return;
        }
        self.playing.shorts.loading = true;
        if self.playing.shorts.from_list {
            let before = self.browse.items.len();
            self.after_more_loaded = Some(Box::new(move |app: &mut App, _| {
                let fresh: Vec<SearchResult> = app.browse.items[before.min(app.browse.items.len())..].to_vec();
                let has_more = app.browse.can_load_more();
                app.shorts_loaded(fresh, has_more, then_play_next);
            }));
            self.load_more(false);
            return;
        }
        let Some((query, Some(token))) = self.playing.shorts.query.clone() else {
            self.playing.shorts.loading = false;
            self.playing.shorts.has_more = false;
            if then_play_next {
                speech::say(&t("That was the last Short."));
            }
            return;
        };
        let inner_tube = self.sources.inner_tube.clone();
        self.jobs.run("more shorts", move || {
            let page = inner_tube.search_shorts(&query, Some(&token));
            move |app: &mut App| {
                let (items, next) = page.map(|p| (p.items, p.next_token)).unwrap_or_default();
                let has_more = next.is_some();
                app.playing.shorts.query = Some((query, next));
                app.shorts_loaded(items, has_more, then_play_next);
            }
        });
    }

    fn shorts_loaded(&mut self, items: Vec<SearchResult>, has_more: bool, then_play_next: bool) {
        self.playing.shorts.loading = false;
        self.playing.shorts.has_more = has_more;
        let fresh: Vec<SearchResult> = items.into_iter().filter(|i| i.is_short && !self.playing.shorts.items.iter().any(|s| s.id == i.id)).collect();
        for item in &fresh {
            let mut queued = item.clone();
            queued.status_prefix = None;
            self.coordinator.enqueue(&queued);
        }
        let added = !fresh.is_empty();
        self.playing.shorts.items.extend(fresh);
        if then_play_next {
            if added {
                self.playing.shorts.index += 1;
                self.play_short();
            } else {
                speech::say(&t("That was the last Short."));
            }
        }
    }

    fn play_short(&mut self) {
        let shorts = &self.playing.shorts;
        let Some(item) = shorts.items.get(shorts.index).cloned() else { return };
        let by = if item.channel_title.trim().is_empty() { String::new() } else { format!(", {}", tf("by {0}", &[&item.channel_title])) };
        speech::say(&(tf("Short {0} of {1}: {2}", &[&(shorts.index + 1), &shorts.items.len(), &item.title]) + &by));
        self.playing.shorts.playing_short = true;
        self.play_item(item, true);
        self.playing.shorts.playing_short = false;
        let shorts = &self.playing.shorts;
        if let Some(next) = shorts.items.get(shorts.index + 1) {
            self.coordinator.prefetch(&next.id);
        } else if shorts.has_more {
            self.load_more_shorts(false);
        }
    }

    pub(crate) fn end_shorts_mode(&mut self) {
        if !self.playing.shorts.active {
            return;
        }
        self.playing.shorts.active = false;
        self.coordinator.engine().set_loop(false);
    }

    // ----- Braille -----

    pub(crate) fn update_braille(&mut self) {
        let playing = matches!(self.playing.state, PlaybackState::Playing | PlaybackState::Buffering);
        let paused = self.playing.state == PlaybackState::Paused;
        let (enabled, cells) = self.settings.with(|c| (c.accessibility.enable_braille_progress, c.accessibility.braille_display_cell_count));
        if !enabled {
            if !self.ui.braille.get_value().is_empty() {
                self.ui.braille.set_value("");
            }
            braille::publish_state("off");
            return;
        }
        let duration = self.playing.effective_duration();
        let starts = self.playing.chapter_starts();
        let line = braille::Track::new(cells as usize).line(self.playing.position, duration, playing, paused, self.playing.blink_visible, &starts);
        if self.ui.braille.get_value() != line {
            self.ui.braille.set_value(&line);
        }
        braille::publish_state(&braille::state_text(playing, paused, self.playing.position, duration, &starts));
    }

    /// The blink of the paused position, from the frame's timer.
    pub(crate) fn braille_blink(&mut self) {
        if self.playing.state == PlaybackState::Paused {
            self.playing.blink_visible = !self.playing.blink_visible;
            self.update_braille();
        }
    }

    /// A routing key on the NVDA add-on's braille line: go there (to a chapter's start if one is marked there).
    pub(crate) fn braille_touch(&mut self, cell: usize, cell_count: usize) {
        let duration = self.playing.effective_duration();
        if !self.playing.session_open || duration <= 0.0 {
            return;
        }
        let cells = if cell_count >= 2 { cell_count } else { self.settings.with(|c| c.accessibility.braille_display_cell_count as usize) };
        let mut target = braille::Track::seek_time(cell, cells, duration);
        let track = braille::Track::new(cells);
        if let Some(start) = self.playing.chapter_starts().into_iter().find(|s| *s > 0.0 && track.cell_index(*s, duration) == cell) {
            target = start;
        }
        self.seek_to(target);
    }

    /// Control+Shift+D.
    pub(crate) fn toggle_braille_progress(&mut self) {
        let on = !self.settings.with(|c| c.accessibility.enable_braille_progress);
        self.settings.update(|c| c.accessibility.enable_braille_progress = on);
        let cells = self.settings.with(|c| c.accessibility.braille_display_cell_count);
        speech::say(&if on { tf("Braille progress track enabled ({0} cells).", &[&cells]) } else { t("Braille progress track disabled. Screen reader standard display active.") });
        self.update_braille();
    }
}

fn capitalize(text: &str) -> String {
    let mut chars = text.chars();
    match chars.next() {
        Some(first) => first.to_uppercase().collect::<String>() + chars.as_str(),
        None => String::new(),
    }
}
