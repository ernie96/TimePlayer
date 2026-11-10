//! The keys, as 1.x's MainWindow handled them in PreviewKeyDown: help, Settings, F6, search, the music keys,
//! then the list's or the player's own. The frame's key hook sees every key first (on Windows a dialog-like
//! panel would take Enter and Escape before a control's key-down).

use timeplayer_core::i18n::{t, tf};
use wxdragon::event::KeyboardEvent;
use wxdragon::prelude::*;

use crate::sections::Section;
use crate::window::PlayerPanel;
use crate::{App, dialogs, help, speech};

/// Where the keyboard is.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Focus {
    SearchBox,
    TopBar,
    Error,
    SectionTabs,
    SectionControls,
    List,
    Empty,
    NowPlaying,
    PlayerButtons,
    Position,
    Braille,
    PanelTabs,
    UpNext,
    Comments,
    Description,
    PlayerQueue,
    Chapters,
    Transcript,
    Lyrics,
    Other,
}

impl Focus {
    fn in_side_panel(self) -> bool {
        matches!(self, Focus::PanelTabs | Focus::UpNext | Focus::Comments | Focus::Description | Focus::PlayerQueue | Focus::Chapters | Focus::Transcript | Focus::Lyrics)
    }
}

/// Digit keys, on the main row or the number pad.
fn digit(code: i32) -> Option<u32> {
    match code {
        c if (i32::from(b'0')..=i32::from(b'9')).contains(&c) => Some((c - i32::from(b'0')) as u32),
        c if (WXK_NUMPAD0..=WXK_NUMPAD9).contains(&c) => Some((c - WXK_NUMPAD0) as u32),
        _ => None,
    }
}

fn letter(code: i32) -> Option<char> {
    u8::try_from(code).ok().filter(u8::is_ascii_alphabetic).map(|c| c.to_ascii_uppercase() as char)
}

impl App {
    pub(crate) fn focus(&self) -> Focus {
        let ui = &self.ui;
        if ui.search_box.has_focus() {
            return Focus::SearchBox;
        }
        if ui.search_kind.has_focus() || ui.settings_button.has_focus() {
            return Focus::TopBar;
        }
        if ui.error_text.has_focus() || ui.retry.has_focus() || ui.dismiss.has_focus() {
            return Focus::Error;
        }
        if ui.section_tabs.has_focus() {
            return Focus::SectionTabs;
        }
        if ui.list.has_focus() {
            return Focus::List;
        }
        if ui.empty.has_focus() {
            return Focus::Empty;
        }
        if [&ui.now_play, &ui.now_previous, &ui.now_next, &ui.now_open, &ui.now_stop].iter().any(|b| b.has_focus()) {
            return Focus::NowPlaying;
        }
        if [&ui.player_back, &ui.actions, &ui.play, &ui.previous, &ui.next, &ui.mute].iter().any(|b| b.has_focus()) {
            return Focus::PlayerButtons;
        }
        if ui.position.has_focus() {
            return Focus::Position;
        }
        if ui.braille.has_focus() {
            return Focus::Braille;
        }
        if ui.panels.has_focus() {
            return Focus::PanelTabs;
        }
        if ui.up_next.has_focus() {
            return Focus::UpNext;
        }
        if ui.comments.has_focus() {
            return Focus::Comments;
        }
        if ui.description.has_focus() {
            return Focus::Description;
        }
        if ui.queue.has_focus() {
            return Focus::PlayerQueue;
        }
        if ui.chapters.has_focus() {
            return Focus::Chapters;
        }
        if ui.transcript.has_focus() {
            return Focus::Transcript;
        }
        if ui.lyrics.has_focus() {
            return Focus::Lyrics;
        }
        if self.player_visible { Focus::Other } else { Focus::SectionControls }
    }

    /// The frame's key hook: true when the key was TimePlayer's.
    pub(crate) fn key(&mut self, key: &KeyboardEvent) -> bool {
        self.key_code(key.get_key_code().unwrap_or(0), key.control_down(), key.shift_down(), key.alt_down())
    }

    /// A key with its modifiers, as the key hook gets it (the self-check calls this directly, since keys
    /// posted to a window never reach the key hook).
    pub(crate) fn key_code(&mut self, code: i32, ctrl: bool, shift: bool, alt: bool) -> bool {
        let none = !ctrl && !shift && !alt;
        let ctrl_only = ctrl && !shift && !alt;
        let ctrl_shift = ctrl && shift && !alt;
        let focus = self.focus();
        if code == WXK_F1 && none {
            self.speak_quick_help(focus);
            return true;
        }
        if code == WXK_F1 && ctrl_only {
            self.show_context_help(focus);
            return true;
        }
        if code == WXK_F2 && none {
            self.open_settings();
            return true;
        }
        if code == WXK_F6 && !ctrl && !alt {
            self.cycle_area(focus, shift);
            return true;
        }
        if ctrl_shift && letter(code) == Some('D') {
            self.toggle_braille_progress();
            return true;
        }
        let search_key = (none && (code == i32::from(b'/') || code == WXK_DIVIDE || code == WXK_NUMPAD_DIVIDE)) || (ctrl_only && matches!(letter(code), Some('F' | 'E')));
        let in_text_box = matches!(focus, Focus::SearchBox | Focus::Description | Focus::Error | Focus::Empty | Focus::Braille);
        if search_key && !(in_text_box && none) {
            self.focus_search_from_anywhere();
            return true;
        }
        if ctrl_shift {
            match letter(code) {
                Some('V') => {
                    self.paste_and_play();
                    return true;
                }
                Some('B') => {
                    self.show_report(false);
                    return true;
                }
                Some('F') => {
                    self.show_report(true);
                    return true;
                }
                Some('S') => {
                    self.start_shorts();
                    return true;
                }
                _ => {}
            }
        }
        if self.music_key(code, ctrl, shift, alt) {
            return true;
        }
        if self.player_visible { self.player_key(code, ctrl, shift, alt, focus) } else { self.library_key(code, ctrl, shift, alt, focus) }
    }

    /// Control+M from anywhere; while something plays, Control+Space, the next and previous, Stop and Alt+P.
    fn music_key(&mut self, code: i32, ctrl: bool, shift: bool, alt: bool) -> bool {
        if ctrl && !shift && !alt && letter(code) == Some('M') {
            self.toggle_music_mode();
            return true;
        }
        if !self.playing.session_open {
            return false;
        }
        let ctrl_shift = ctrl && shift && !alt;
        if code == WXK_MEDIA_PLAY_PAUSE || (ctrl && !shift && !alt && code == WXK_SPACE) {
            self.toggle_play_pause();
            return true;
        }
        if code == WXK_MEDIA_NEXT_TRACK || (ctrl_shift && letter(code) == Some('N')) {
            self.play_next_or_up_next();
            return true;
        }
        if code == WXK_MEDIA_PREV_TRACK || (ctrl_shift && letter(code) == Some('P')) {
            self.play_previous();
            return true;
        }
        if code == WXK_MEDIA_STOP {
            self.stop_playback_session();
            return true;
        }
        if alt && !ctrl && !shift && letter(code) == Some('P') && !self.player_visible {
            self.show_player_view();
            return true;
        }
        false
    }

    fn library_key(&mut self, code: i32, ctrl: bool, shift: bool, alt: bool, focus: Focus) -> bool {
        let none = !ctrl && !shift && !alt;
        let ctrl_only = ctrl && !shift && !alt;
        let detail = self.browse.current().is_detail();
        if code == WXK_ESCAPE && none {
            if focus == Focus::SearchBox && !self.ui.search_box.get_value().is_empty() {
                self.ui.search_box.set_value("");
                return true;
            }
            if matches!(focus, Focus::SearchBox | Focus::TopBar) {
                if self.ui.list.is_empty() {
                    self.ui.focus_section_tabs();
                } else {
                    self.ui.focus_list();
                }
                return true;
            }
            if detail {
                self.go_back();
                return true;
            }
            return false;
        }
        if ctrl_only && let Some(number) = digit(code).filter(|d| (1..=9).contains(d)) {
            self.show_section_by_number(number as usize);
            return true;
        }
        if code == WXK_TAB && ctrl && !alt {
            self.next_section(shift);
            return true;
        }
        if code == WXK_F5 && none {
            self.refresh_section();
            return true;
        }
        if detail && ((code == WXK_LEFT && alt && !ctrl && !shift) || (code == WXK_BACK && none && focus != Focus::SearchBox)) {
            self.go_back();
            return true;
        }
        if self.browse.is_open_playlist_in_library() && alt && !ctrl && !shift && (code == WXK_UP || code == WXK_DOWN) && focus == Focus::List {
            self.move_selected_in_playlist(if code == WXK_UP { -1 } else { 1 });
            return true;
        }
        if focus == Focus::List {
            return self.list_key(code, ctrl, shift, alt);
        }
        if focus == Focus::Empty && code == WXK_ESCAPE {
            self.ui.focus_section_tabs();
            return true;
        }
        false
    }

    /// The main list's own keys (1.x's KeyBindings on the results and queue lists).
    fn list_key(&mut self, code: i32, ctrl: bool, shift: bool, alt: bool) -> bool {
        let none = !ctrl && !shift && !alt;
        let ctrl_only = ctrl && !shift && !alt;
        if self.browse.current() == Section::Queue {
            if none && (code == WXK_RETURN || code == WXK_NUMPAD_ENTER) {
                self.activate_selected();
                return true;
            }
            if none && code == WXK_DELETE {
                self.remove_selected();
                return true;
            }
            return false;
        }
        if none && (code == WXK_RETURN || code == WXK_NUMPAD_ENTER) {
            self.activate_selected();
            return true;
        }
        if none && code == WXK_DELETE {
            self.remove_selected();
            return true;
        }
        if shift && !ctrl && !alt && code == WXK_DELETE {
            if let Some(item) = self.selected_item() {
                self.dont_recommend_channel(&item);
            }
            return true;
        }
        if !ctrl_only {
            return false;
        }
        let Some(item) = self.selected_item() else { return false };
        match letter(code) {
            Some('Q') => self.enqueue_item(&item),
            Some('L') => self.toggle_watch_later_item(&item),
            Some('S') => self.toggle_subscription_item(&item),
            Some('G') => self.open_channel(&item),
            Some('C') => self.copy_link_item(&item),
            Some('R') => self.start_radio(Some(item)),
            Some('K') => self.toggle_like_item(&item),
            Some('P') => self.show_add_to_playlist_menu(Some(item)),
            Some('D') if item.is_video() => self.show_download_menu(Some(item)),
            _ => return false,
        }
        true
    }

    fn player_key(&mut self, code: i32, ctrl: bool, shift: bool, alt: bool, focus: Focus) -> bool {
        let none = !ctrl && !shift && !alt;
        if code == WXK_ESCAPE && none {
            if focus.in_side_panel() {
                self.ui.play.set_focus();
            } else {
                self.leave_player_view();
            }
            return true;
        }
        if alt && !ctrl && !shift && letter(code) == Some('A') {
            self.show_actions_menu();
            return true;
        }
        let on_list_item = matches!(focus, Focus::UpNext | Focus::PlayerQueue | Focus::Chapters | Focus::Transcript | Focus::Lyrics | Focus::Comments);
        if !on_list_item && ((code == WXK_WINDOWS_MENU && none) || (code == WXK_F10 && shift && !ctrl && !alt)) {
            self.show_actions_menu();
            return true;
        }
        if alt && !ctrl && !shift {
            let panel = match letter(code) {
                Some('U') => PlayerPanel::UpNext,
                Some('C') => PlayerPanel::Comments,
                Some('D') => PlayerPanel::Description,
                Some('Q') => PlayerPanel::Queue,
                Some('H') => PlayerPanel::Chapters,
                Some('T') => PlayerPanel::Transcript,
                Some('L') => PlayerPanel::Lyrics,
                _ => return false,
            };
            self.show_player_panel(panel);
            return true;
        }
        // The panels' lists have keys of their own.
        if let Some(handled) = self.panel_list_key(code, ctrl, shift, alt, focus) {
            return handled;
        }
        // The Position slider: Left and Right move the small step, Page Up and Page Down the large one.
        if focus == Focus::Position && none {
            match code {
                WXK_LEFT | WXK_DOWN => self.seek_small(false),
                WXK_RIGHT | WXK_UP => self.seek_small(true),
                WXK_PAGEUP => self.seek_large(true),
                WXK_PAGEDOWN => self.seek_large(false),
                WXK_HOME => self.restart_from_beginning(),
                _ => {}
            }
            if matches!(code, WXK_LEFT | WXK_RIGHT | WXK_UP | WXK_DOWN | WXK_PAGEUP | WXK_PAGEDOWN | WXK_HOME) {
                return true;
            }
        }
        let in_text_box = matches!(focus, Focus::Description | Focus::Braille | Focus::Error);
        let is_tree = focus == Focus::Comments;
        let is_list = on_list_item;
        let arrows_belong_to_control = in_text_box || focus == Focus::Position || is_tree;
        let space_belongs_to_control = matches!(focus, Focus::PlayerButtons | Focus::PanelTabs) || in_text_box;
        if ctrl && shift && !alt && letter(code) == Some('C') {
            self.copy_share_link_current();
            return true;
        }
        if ctrl && !shift && !alt && letter(code) == Some('D') {
            match self.selected_up_next().filter(|_| focus == Focus::UpNext && self.selected_up_next().is_some_and(|i| i.is_video())) {
                Some(item) => self.show_download_menu(Some(item)),
                None => self.show_download_menu(None),
            }
            return true;
        }
        if ctrl && !shift && !alt {
            match (letter(code), code) {
                (Some('C'), _) if !is_list && !in_text_box => self.copy_share_link_at_current_time(),
                (Some('S'), _) if !is_list => self.toggle_subscribe_current(),
                (Some('L'), _) if !is_list => self.toggle_watch_later_current(),
                (Some('K'), _) if !is_list => self.toggle_like_current(),
                (Some('P'), _) if !is_list => self.show_add_to_playlist_menu(None),
                (Some('G'), _) if !is_list => self.open_current_channel(),
                (_, WXK_LEFT) if !arrows_belong_to_control => self.seek_large(false),
                (_, WXK_RIGHT) if !arrows_belong_to_control => self.seek_large(true),
                _ => return false,
            }
            return true;
        }
        if shift && !ctrl && !alt {
            match (letter(code), code) {
                (Some('N'), _) => self.play_next_or_up_next(),
                (Some('P'), _) => self.play_previous(),
                (Some('E'), _) => self.cycle_sound_preset(-1),
                (_, c) if c == i32::from(b'.') || c == i32::from(b'>') => self.change_speed(1),
                (_, c) if c == i32::from(b',') || c == i32::from(b'<') => self.change_speed(-1),
                (_, WXK_RIGHT) if !arrows_belong_to_control => self.next_chapter(),
                (_, WXK_LEFT) if !arrows_belong_to_control => self.previous_chapter(),
                (_, WXK_UP) if self.playing.shorts.active && !arrows_belong_to_control => self.change_volume(true),
                (_, WXK_DOWN) if self.playing.shorts.active && !arrows_belong_to_control => self.change_volume(false),
                _ => return false,
            }
            return true;
        }
        if !none {
            return false;
        }
        if let Some(d) = digit(code)
            && !in_text_box
        {
            self.seek_to_percent(d);
            return true;
        }
        match (letter(code), code) {
            (_, WXK_SPACE) if !space_belongs_to_control => self.toggle_play_pause(),
            (Some('K'), _) => self.toggle_play_pause(),
            (Some('M'), _) => self.toggle_mute(),
            (Some('T'), _) => self.announce_time(),
            (Some('I'), _) => self.announce_video_info(),
            (Some('U'), _) => self.announce_up_next(),
            (Some('A'), _) => self.toggle_autoplay(),
            (Some('D'), _) => self.toggle_audio_description(),
            (Some('L'), _) => self.toggle_speak_lyrics(),
            (Some('R'), _) => self.start_radio(None),
            (Some('C'), _) => self.toggle_speak_captions(),
            (Some('S'), _) => self.skip_current_segment(),
            (Some('H'), _) => self.jump_to_highlight(),
            (Some('E'), _) => self.cycle_sound_preset(1),
            (_, WXK_HOME) if !arrows_belong_to_control && !is_list => self.restart_from_beginning(),
            (_, WXK_LEFT) if !arrows_belong_to_control => self.seek_small(false),
            (_, WXK_RIGHT) if !arrows_belong_to_control => self.seek_small(true),
            (_, WXK_UP) if self.playing.shorts.active && !arrows_belong_to_control && !is_list => self.previous_short(),
            (_, WXK_DOWN) if self.playing.shorts.active && !arrows_belong_to_control && !is_list => self.next_short(),
            (_, WXK_UP) if !arrows_belong_to_control && !is_list => self.change_volume(true),
            (_, WXK_DOWN) if !arrows_belong_to_control && !is_list => self.change_volume(false),
            _ => return false,
        }
        true
    }

    /// Up next, the queue, chapters, transcript, lyrics and comments: Enter and their Control keys.
    fn panel_list_key(&mut self, code: i32, ctrl: bool, shift: bool, alt: bool, focus: Focus) -> Option<bool> {
        let none = !ctrl && !shift && !alt;
        let ctrl_only = ctrl && !shift && !alt;
        let enter = none && (code == WXK_RETURN || code == WXK_NUMPAD_ENTER);
        match focus {
            Focus::UpNext => {
                if enter {
                    self.play_selected_up_next();
                    return Some(true);
                }
                if ctrl_only && let Some(item) = self.selected_up_next() {
                    match letter(code) {
                        Some('Q') => self.enqueue_item(&item),
                        Some('L') => self.toggle_watch_later_item(&item),
                        Some('S') => self.toggle_subscription_item(&item),
                        Some('G') => self.open_channel(&item),
                        Some('C') => self.copy_link_item(&item),
                        Some('K') => self.toggle_like_item(&item),
                        Some('P') => self.show_add_to_playlist_menu(Some(item)),
                        _ => return None,
                    }
                    return Some(true);
                }
                None
            }
            Focus::PlayerQueue => {
                if enter {
                    self.play_selected_queue_item();
                    return Some(true);
                }
                if none && code == WXK_DELETE {
                    if let Some(index) = self.ui.queue.selected_index() {
                        self.remove_queue_item(index);
                    }
                    return Some(true);
                }
                None
            }
            Focus::Chapters if enter => {
                self.play_selected_chapter();
                Some(true)
            }
            Focus::Transcript if enter => {
                self.play_from_transcript_line();
                Some(true)
            }
            Focus::Lyrics if enter => {
                self.play_from_lyric_line();
                Some(true)
            }
            Focus::Comments => {
                if enter {
                    self.toggle_replies();
                    return Some(true);
                }
                if ctrl_only && letter(code) == Some('C') {
                    self.copy_comment();
                    return Some(true);
                }
                if (code == WXK_WINDOWS_MENU && none) || (code == WXK_F10 && shift && !ctrl && !alt) {
                    self.comment_context_menu();
                    return Some(true);
                }
                None
            }
            _ => None,
        }
    }

    fn comment_context_menu(&mut self) {
        let mut menu = dialogs::PopupMenu::new();
        menu.item("Show or hide _replies", "Right, Left or Enter", App::toggle_replies);
        menu.item("_Copy comment", "Ctrl+C", App::copy_comment);
        menu.show_at(self, dialogs::MenuTarget::Comments);
    }

    /// Slash, Control+F or Control+E: the search box, leaving the player.
    fn focus_search_from_anywhere(&mut self) {
        self.leave_player_view();
        self.ui.focus_search();
    }

    /// F6 and Shift+F6: the areas of the window in turn.
    fn cycle_area(&mut self, focus: Focus, backwards: bool) {
        type Area = fn(&mut App);
        let mut areas: Vec<(Area, bool)> = Vec::new();
        if self.ui.error_panel.is_shown() && !self.player_visible {
            areas.push((|app| {
                app.ui.error_text.set_focus();
                app.ui.error_text.select_all();
            }, focus == Focus::Error));
        }
        if self.player_visible {
            areas.push((|app| app.ui.play.set_focus(), !focus.in_side_panel() && !self.ui.player_back.has_focus()));
            areas.push((|app| app.focus_selected_panel(), focus.in_side_panel()));
            areas.push((|app| app.ui.player_back.set_focus(), self.ui.player_back.has_focus()));
        } else {
            areas.push((|app| app.ui.focus_search(), matches!(focus, Focus::SearchBox | Focus::TopBar)));
            areas.push((|app| app.ui.focus_section_tabs(), matches!(focus, Focus::SectionTabs | Focus::SectionControls)));
            areas.push((|app| app.ui.focus_list(), matches!(focus, Focus::List | Focus::Empty)));
            if self.ui.now_playing.is_shown() {
                areas.push((|app| app.ui.now_play.set_focus(), focus == Focus::NowPlaying));
            }
        }
        let current = areas.iter().position(|(_, here)| *here).unwrap_or(if self.player_visible { 0 } else { usize::MAX });
        let count = areas.len();
        let next = match (current, backwards) {
            (usize::MAX, false) => 0,
            (usize::MAX, true) => count - 1,
            (c, false) => (c + 1) % count,
            (c, true) => (c + count - 1) % count,
        };
        (areas[next].0)(self);
    }

    /// F1: one or two sentences about where the keyboard is.
    fn speak_quick_help(&self, focus: Focus) {
        let text = if self.player_visible {
            match focus {
                Focus::UpNext => help::quick::up_next(),
                Focus::Comments => help::quick::comments(),
                Focus::Description => help::quick::description(),
                Focus::PlayerQueue => help::quick::player_queue(),
                Focus::Chapters => help::quick::chapters(),
                Focus::Transcript => help::quick::transcript(),
                Focus::Lyrics => help::quick::lyrics(),
                Focus::PanelTabs => match self.selected_panel() {
                    Some(PlayerPanel::Chapters) => help::quick::chapters(),
                    Some(PlayerPanel::Transcript) => help::quick::transcript(),
                    Some(PlayerPanel::Lyrics) => help::quick::lyrics(),
                    _ => self.player_quick_help(),
                },
                _ => self.player_quick_help(),
            }
        } else {
            match focus {
                Focus::NowPlaying => help::quick::now_playing(),
                Focus::SearchBox => help::quick::search(),
                Focus::SectionTabs => help::quick::sections(self.music_mode),
                _ => help::quick::list(self.browse.current(), self.ui.list.len(), &self.browse.empty_text),
            }
        };
        speech::say(&text);
    }

    fn player_quick_help(&self) -> String {
        if self.playing.shorts.active { help::quick::shorts() } else { help::quick::player() }
    }

    /// Control+F1: the full help for where the keyboard is.
    fn show_context_help(&self, focus: Focus) {
        let (title, text) = if self.player_visible {
            match focus {
                Focus::UpNext => (t("Up next"), help::full::up_next()),
                Focus::Comments => (t("Comments"), help::full::comments()),
                Focus::Description => (t("Description"), help::full::description()),
                Focus::PlayerQueue => (t("Queue"), help::full::queue()),
                Focus::Chapters => (t("Chapters"), help::full::chapters()),
                Focus::Transcript => (t("Transcript"), help::full::transcript()),
                Focus::Lyrics => (t("Lyrics"), help::full::lyrics()),
                _ => {
                    let (small, large) = self.settings.with(|c| (c.playback.small_seek_seconds, c.playback.large_seek_seconds));
                    (t("Player"), help::full::player(small, large))
                }
            }
        } else {
            match focus {
                Focus::NowPlaying => (t("Now playing"), help::full::now_playing()),
                Focus::SearchBox => (t("Search box"), help::full::search_box()),
                Focus::SectionTabs => (t("Sections"), help::full::sections()),
                _ => (self.browse.title.clone(), help::full::for_list(self.browse.current())),
            }
        };
        dialogs::message(&self.ui.frame, &text, &tf("Help: {0}", &[&title]));
    }
}
