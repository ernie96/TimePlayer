//! The main window's controls, laid out as 1.x's MainWindow: the top bar (search box, Search for, Settings),
//! the problem panel, the Sections strip with the section's buttons, the list, the Now playing bar, and the
//! player (video, buttons, Position, the braille line and the panels).

use quiet_list::QuietList;
use timeplayer_core::i18n::t;
use wxdragon::prelude::*;

use crate::sections::{AccountPage, Browse, ChannelTab, FEATURED_CATEGORIES, SearchKind, Section, category_label};
use crate::{App, with_app};

/// A label with its access key: 1.x's "_" becomes wxWidgets' "&".
pub fn wx_label(label: &str) -> String {
    t(label).replace('&', "&&").replacen('_', "&", 1)
}

/// Pages of the player's panel strip, in Alt+letter order of 1.x.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum PlayerPanel {
    UpNext = 0,
    Comments = 1,
    Description = 2,
    Queue = 3,
    Chapters = 4,
    Transcript = 5,
    Lyrics = 6,
}

impl PlayerPanel {
    pub const ALL: [PlayerPanel; 7] =
        [PlayerPanel::UpNext, PlayerPanel::Comments, PlayerPanel::Description, PlayerPanel::Queue, PlayerPanel::Chapters, PlayerPanel::Transcript, PlayerPanel::Lyrics];

    pub fn from_index(index: usize) -> Option<Self> {
        Self::ALL.get(index).copied()
    }
}

pub struct Ui {
    pub frame: Frame,
    pub root: Panel,
    pub library: Panel,
    pub search_box: TextCtrl,
    pub search_kind: Choice,
    pub settings_button: Button,
    pub error_panel: Panel,
    pub error_text: TextCtrl,
    pub retry: Button,
    pub dismiss: Button,
    pub section_tabs: Notebook,
    section_pages: Vec<Panel>,
    pub back: Button,
    pub play_all: Button,
    pub shuffle: Button,
    pub save_playlist: Button,
    pub copy_playlist_link: Button,
    pub new_playlist: Button,
    pub rename_playlist: Button,
    pub delete_playlist: Button,
    /// Playlists and Channels: Settings at its Import page.
    pub import_export: Button,
    category_label: StaticText,
    pub category: Choice,
    channel_tab_label: StaticText,
    pub channel_tab: Choice,
    account_page_label: StaticText,
    pub account_page: Choice,
    pub clear_history: Button,
    pub clear_queue: Button,
    pub refresh: Button,
    pub heading: StaticText,
    pub list: QuietList<usize>,
    pub empty: TextCtrl,
    pub now_playing: Panel,
    pub now_playing_text: StaticText,
    pub now_play: Button,
    pub now_previous: Button,
    pub now_next: Button,
    pub now_open: Button,
    pub now_stop: Button,
    pub player: Panel,
    pub video: Panel,
    pub player_back: Button,
    pub actions: Button,
    pub play: Button,
    pub previous: Button,
    pub next: Button,
    pub mute: Button,
    pub position: Slider,
    pub braille: TextCtrl,
    pub panels: Notebook,
    pub up_next: QuietList<usize>,
    pub comments: TreeCtrl,
    pub comments_status: StaticText,
    pub description: TextCtrl,
    pub queue: QuietList<usize>,
    pub chapters: QuietList<usize>,
    pub transcript: QuietList<usize>,
    pub transcript_status: StaticText,
    pub lyrics: QuietList<usize>,
    pub lyrics_status: StaticText,
}

fn button(parent: &impl WxWidget, label: &str, name: &str) -> Button {
    let b = Button::builder(parent).with_label(&wx_label(label)).build();
    if !name.is_empty() {
        b.set_name(&t(name));
    }
    b
}

fn on_click(b: &Button, f: fn(&mut App)) {
    b.on_click(move |_| with_app(|app| app.after_button(f)));
}

impl Ui {
    /// Makes every control. Call before the frame is shown (quiet-list needs that).
    pub fn build(frame: &Frame) -> Ui {
        let root = Panel::builder(frame).build();
        let library = Panel::builder(&root).build();

        // Top bar.
        let search_label = StaticText::builder(&library).with_label(&wx_label("Search YouTube or paste a link")).build();
        let search_box = TextCtrl::builder(&library).with_style(TextCtrlStyle::ProcessEnter).build();
        search_box.set_name(&t("Search YouTube or paste a link"));
        let kind_label = StaticText::builder(&library).with_label(&wx_label("Search for")).build();
        let search_kind = Choice::builder(&library).with_choices(SearchKind::ALL.iter().map(|k| k.label()).collect()).build();
        search_kind.set_selection(0);
        let settings_button = button(&library, "Settings (F2)", "Settings");

        // The problem panel, hidden until there's a problem.
        let error_panel = Panel::builder(&library).build();
        let error_text = TextCtrl::builder(&error_panel).with_style(TextCtrlStyle::MultiLine | TextCtrlStyle::ReadOnly).with_size(Size::new(-1, 48)).build();
        error_text.set_name(&t("Problem"));
        let retry = button(&error_panel, "_Retry", "Retry");
        let dismiss = button(&error_panel, "_Dismiss", "Dismiss");
        let error_row = BoxSizer::builder(Orientation::Horizontal).build();
        error_row.add(&error_text, 1, SizerFlag::Expand | SizerFlag::Right, 8);
        error_row.add(&retry, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 8);
        error_row.add(&dismiss, 0, SizerFlag::AlignCenterVertical, 0);
        error_panel.set_sizer(error_row, true);
        error_panel.show(false);

        // Sections: a tab strip whose pages stay empty; the list below shows the chosen section.
        let section_tabs = Notebook::builder(&library).with_size(Size::new(-1, 30)).build();
        section_tabs.set_name(&t("Sections"));
        // The pages are empty, so Tab must pass over them rather than stop on a blank "panel".
        let section_pages: Vec<Panel> = (0..9)
            .map(|_| {
                let page = Panel::builder(&section_tabs).with_size(Size::new(1, 1)).build();
                page.disable_focus_from_keyboard();
                page
            })
            .collect();

        // The section's own buttons and boxes, shown for the sections they belong to.
        let back = button(&library, "< _Back", "Back");
        let play_all = button(&library, "Play _all", "Play all");
        let shuffle = button(&library, "Sh_uffle", "Shuffle play");
        let save_playlist = button(&library, "_Save to Playlists", "Save to Playlists");
        let copy_playlist_link = button(&library, "Copy _link", "Copy playlist link");
        let new_playlist = button(&library, "_New playlist...", "New playlist");
        let rename_playlist = button(&library, "_Rename...", "Rename playlist");
        let delete_playlist = button(&library, "_Delete playlist...", "Delete playlist");
        let import_export = button(&library, "_Import and export...", "Import and export");
        let category_label = StaticText::builder(&library).with_label(&wx_label("_Category:")).build();
        let category = Choice::builder(&library).with_choices(FEATURED_CATEGORIES.iter().map(|c| category_label_text(*c)).collect()).build();
        category.set_name(&t("Category"));
        category.set_selection(0);
        let channel_tab_label = StaticText::builder(&library).with_label(&wx_label("Ta_b:")).build();
        let channel_tab = Choice::builder(&library).with_choices(ChannelTab::ALL.iter().map(|c| c.label()).collect()).build();
        channel_tab.set_name(&t("Channel tab"));
        channel_tab.set_selection(0);
        let account_page_label = StaticText::builder(&library).with_label(&wx_label("_Page:")).build();
        let account_page = Choice::builder(&library).with_choices(AccountPage::ALL.iter().map(|p| p.label()).collect()).build();
        account_page.set_name(&t("YouTube page"));
        account_page.set_selection(0);
        let clear_history = button(&library, "_Clear history...", "Clear history");
        let clear_queue = button(&library, "_Clear queue", "Clear queue");
        let refresh = button(&library, "Refresh (F5)", "Refresh");

        let heading = StaticText::builder(&library).with_label("").build();
        let list = QuietList::new(&library, &t("Featured"));
        let empty = TextCtrl::builder(&library).with_style(TextCtrlStyle::MultiLine | TextCtrlStyle::ReadOnly).with_size(Size::new(-1, 60)).build();
        empty.show(false);

        // Now playing (music keeps playing while you browse).
        let now_playing = Panel::builder(&library).build();
        now_playing.set_name(&t("Now playing"));
        let now_playing_text = StaticText::builder(&now_playing).with_label("").build();
        let now_play = button(&now_playing, "Play", "");
        let now_previous = button(&now_playing, "Previous", "Previous");
        let now_next = button(&now_playing, "Next", "Next");
        let now_open = button(&now_playing, "Open player", "Open player");
        let now_stop = button(&now_playing, "Stop", "Stop");
        let now_row = BoxSizer::builder(Orientation::Horizontal).build();
        now_row.add(&now_playing_text, 1, SizerFlag::AlignCenterVertical | SizerFlag::Right, 8);
        for b in [&now_play, &now_previous, &now_next, &now_open, &now_stop] {
            now_row.add(b, 0, SizerFlag::Right, 6);
        }
        now_playing.set_sizer(now_row, true);
        now_playing.show(false);

        let top = BoxSizer::builder(Orientation::Horizontal).build();
        top.add(&search_label, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 6);
        top.add(&search_box, 1, SizerFlag::Expand | SizerFlag::Right, 12);
        top.add(&kind_label, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 6);
        top.add(&search_kind, 0, SizerFlag::Right, 12);
        top.add(&settings_button, 0, SizerFlag::All, 0);
        let controls = BoxSizer::builder(Orientation::Horizontal).build();
        for b in [&back, &play_all, &shuffle, &save_playlist, &copy_playlist_link, &new_playlist, &rename_playlist, &delete_playlist, &import_export] {
            controls.add(b, 0, SizerFlag::Right, 6);
        }
        controls.add(&category_label, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 4);
        controls.add(&category, 0, SizerFlag::Right, 6);
        controls.add(&channel_tab_label, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 4);
        controls.add(&channel_tab, 0, SizerFlag::Right, 6);
        controls.add(&account_page_label, 0, SizerFlag::AlignCenterVertical | SizerFlag::Right, 4);
        controls.add(&account_page, 0, SizerFlag::Right, 6);
        for b in [&clear_history, &clear_queue, &refresh] {
            controls.add(b, 0, SizerFlag::Right, 6);
        }
        let library_sizer = BoxSizer::builder(Orientation::Vertical).build();
        library_sizer.add_sizer(&top, 0, SizerFlag::Expand | SizerFlag::All, 8);
        library_sizer.add(&error_panel, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right, 8);
        library_sizer.add(&section_tabs, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right | SizerFlag::Top, 8);
        library_sizer.add_sizer(&controls, 0, SizerFlag::Expand | SizerFlag::All, 8);
        library_sizer.add(&heading, 0, SizerFlag::Left | SizerFlag::Right, 8);
        library_sizer.add(&list, 1, SizerFlag::Expand | SizerFlag::All, 8);
        library_sizer.add(&empty, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right, 8);
        library_sizer.add(&now_playing, 0, SizerFlag::Expand | SizerFlag::All, 8);
        library.set_sizer(library_sizer, true);

        // The player, hidden until a video plays.
        let player = Panel::builder(&root).build();
        let player_back = button(&player, "< Back (Esc)", "Back to the list");
        let actions = button(&player, "_Actions...", "Actions");
        let video = Panel::builder(&player).with_size(Size::new(640, 360)).build();
        video.set_background_color(Colour::new(0, 0, 0, 255));
        // mpv draws here; there's nothing to do in it with the keyboard.
        video.disable_focus_from_keyboard();
        let position_label = StaticText::builder(&player).with_label(&t("Position")).build();
        let position = Slider::builder(&player).with_value(0).with_min_value(0).with_max_value(1).build();
        position.set_name(&t("Position"));
        let braille = TextCtrl::builder(&player).with_style(TextCtrlStyle::ReadOnly).build();
        braille.set_name(&t("Braille progress"));
        let play = button(&player, "Play", "");
        let previous = button(&player, "Previous", "Previous");
        let next = button(&player, "Next", "Next");
        let mute = button(&player, "Mute", "");
        let panels = Notebook::builder(&player).build();
        panels.set_name(&t("Video panels"));
        let (up_next_page, up_next) = list_page(&panels, "Up next");
        let comments_page = Panel::builder(&panels).build();
        let comments_status = StaticText::builder(&comments_page).with_label(&t("Open this tab to load comments.")).build();
        let comments = TreeCtrl::builder(&comments_page).with_style(TreeCtrlStyle::HasButtons | TreeCtrlStyle::HideRoot | TreeCtrlStyle::LinesAtRoot).build();
        comments.set_name(&t("Comments"));
        let s = BoxSizer::builder(Orientation::Vertical).build();
        s.add(&comments_status, 0, SizerFlag::All, 4);
        s.add(&comments, 1, SizerFlag::Expand, 0);
        comments_page.set_sizer(s, true);
        let description_page = Panel::builder(&panels).build();
        let description = TextCtrl::builder(&description_page).with_style(TextCtrlStyle::MultiLine | TextCtrlStyle::ReadOnly).build();
        description.set_name(&t("Description"));
        let s = BoxSizer::builder(Orientation::Vertical).build();
        s.add(&description, 1, SizerFlag::Expand, 0);
        description_page.set_sizer(s, true);
        let (queue_page, queue) = list_page(&panels, "Queue");
        let (chapters_page, chapters) = list_page(&panels, "Chapters");
        let transcript_page = Panel::builder(&panels).build();
        let transcript_status = StaticText::builder(&transcript_page).with_label(&t("Open this tab to load the transcript.")).build();
        let transcript = QuietList::new(&transcript_page, &t("Transcript"));
        let s = BoxSizer::builder(Orientation::Vertical).build();
        s.add(&transcript_status, 0, SizerFlag::All, 4);
        s.add(&transcript, 1, SizerFlag::Expand, 0);
        transcript_page.set_sizer(s, true);
        let lyrics_page = Panel::builder(&panels).build();
        let lyrics_status = StaticText::builder(&lyrics_page).with_label(&t("Open this tab to look up the lyrics.")).build();
        let lyrics = QuietList::new(&lyrics_page, &t("Lyrics"));
        let s = BoxSizer::builder(Orientation::Vertical).build();
        s.add(&lyrics_status, 0, SizerFlag::All, 4);
        s.add(&lyrics, 1, SizerFlag::Expand, 0);
        lyrics_page.set_sizer(s, true);
        panels.add_page(&up_next_page, &wx_label("_Up next"), true, None);
        panels.add_page(&comments_page, &wx_label("_Comments"), false, None);
        panels.add_page(&description_page, &wx_label("_Description"), false, None);
        panels.add_page(&queue_page, &wx_label("_Queue"), false, None);
        panels.add_page(&chapters_page, &wx_label("C_hapters"), false, None);
        panels.add_page(&transcript_page, &wx_label("_Transcript"), false, None);
        panels.add_page(&lyrics_page, &wx_label("_Lyrics"), false, None);

        let player_top = BoxSizer::builder(Orientation::Horizontal).build();
        player_top.add(&player_back, 0, SizerFlag::Right, 16);
        player_top.add(&actions, 0, SizerFlag::All, 0);
        let buttons = BoxSizer::builder(Orientation::Horizontal).build();
        for b in [&play, &previous, &next, &mute] {
            buttons.add(b, 0, SizerFlag::Right, 6);
        }
        let left = BoxSizer::builder(Orientation::Vertical).build();
        left.add(&video, 1, SizerFlag::Expand | SizerFlag::Bottom, 8);
        left.add(&position_label, 0, SizerFlag::Bottom, 2);
        left.add(&position, 0, SizerFlag::Expand | SizerFlag::Bottom, 8);
        left.add(&braille, 0, SizerFlag::Expand | SizerFlag::Bottom, 8);
        left.add_sizer(&buttons, 0, SizerFlag::Expand, 0);
        let body = BoxSizer::builder(Orientation::Horizontal).build();
        body.add_sizer(&left, 3, SizerFlag::Expand | SizerFlag::Right, 8);
        body.add(&panels, 2, SizerFlag::Expand, 0);
        let player_sizer = BoxSizer::builder(Orientation::Vertical).build();
        player_sizer.add_sizer(&player_top, 0, SizerFlag::All, 8);
        player_sizer.add_sizer(&body, 1, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right | SizerFlag::Bottom, 8);
        player.set_sizer(player_sizer, true);
        player.show(false);

        let root_sizer = BoxSizer::builder(Orientation::Vertical).build();
        root_sizer.add(&library, 1, SizerFlag::Expand, 0);
        root_sizer.add(&player, 1, SizerFlag::Expand, 0);
        root.set_sizer(root_sizer, true);
        let frame_sizer = BoxSizer::builder(Orientation::Vertical).build();
        frame_sizer.add(&root, 1, SizerFlag::Expand, 0);
        frame.set_sizer(frame_sizer, true);

        let ui = Ui {
            frame: *frame,
            root,
            library,
            search_box,
            search_kind,
            settings_button,
            error_panel,
            error_text,
            retry,
            dismiss,
            section_tabs,
            section_pages,
            back,
            play_all,
            shuffle,
            save_playlist,
            copy_playlist_link,
            new_playlist,
            rename_playlist,
            delete_playlist,
            import_export,
            category_label,
            category,
            channel_tab_label,
            channel_tab,
            account_page_label,
            account_page,
            clear_history,
            clear_queue,
            refresh,
            heading,
            list,
            empty,
            now_playing,
            now_playing_text,
            now_play,
            now_previous,
            now_next,
            now_open,
            now_stop,
            player,
            video,
            player_back,
            actions,
            play,
            previous,
            next,
            mute,
            position,
            braille,
            panels,
            up_next,
            comments,
            comments_status,
            description,
            queue,
            chapters,
            transcript,
            transcript_status,
            lyrics,
            lyrics_status,
        };
        ui.bind();
        ui
    }

    fn bind(&self) {
        self.search_box.on_text_enter(|_| with_app(App::search));
        self.search_kind.on_selection_changed(|e| {
            if let Some(kind) = e.get_selection().and_then(|i| SearchKind::ALL.get(i as usize).copied()) {
                with_app(|app| app.search_kind_changed(kind));
            }
        });
        on_click(&self.settings_button, App::open_settings);
        on_click(&self.retry, App::retry_last_action);
        on_click(&self.dismiss, App::dismiss_error);
        self.section_tabs.on_page_changed(|e| {
            if let Some(index) = e.get_selection().and_then(|i| usize::try_from(i).ok()) {
                with_app(|app| app.section_tab_changed(index));
            }
        });
        on_click(&self.back, App::go_back);
        on_click(&self.play_all, |app| app.play_playlist(false));
        on_click(&self.shuffle, |app| app.play_playlist(true));
        on_click(&self.save_playlist, App::save_open_playlist);
        on_click(&self.copy_playlist_link, App::copy_open_playlist_link);
        on_click(&self.new_playlist, App::create_playlist);
        on_click(&self.rename_playlist, App::rename_playlist);
        on_click(&self.delete_playlist, App::delete_playlist);
        on_click(&self.import_export, App::open_import_settings);
        on_click(&self.clear_history, App::clear_history);
        on_click(&self.clear_queue, App::clear_queue);
        on_click(&self.refresh, App::refresh_section);
        self.category.on_selection_changed(|e| {
            if let Some(category) = e.get_selection().and_then(|i| FEATURED_CATEGORIES.get(i as usize).copied()) {
                with_app(|app| app.featured_category_changed(category));
            }
        });
        self.channel_tab.on_selection_changed(|e| {
            if let Some(tab) = e.get_selection().and_then(|i| ChannelTab::ALL.get(i as usize).copied()) {
                with_app(|app| app.channel_tab_changed(tab));
            }
        });
        self.account_page.on_selection_changed(|e| {
            if let Some(page) = e.get_selection().and_then(|i| AccountPage::ALL.get(i as usize).copied()) {
                with_app(|app| app.account_page_changed(page));
            }
        });

        // The list leaves Enter to the app; double clicks and screen-reader clicks come as activate.
        self.list.on_activate(|| with_app(App::activate_selected));
        self.list.on_selection_changed(|| with_app(App::list_selection_changed));
        self.list.on_context_menu(|| with_app(App::list_context_menu));

        on_click(&self.now_play, App::toggle_play_pause);
        on_click(&self.now_previous, App::play_previous);
        on_click(&self.now_next, App::play_next_or_up_next);
        on_click(&self.now_open, App::show_player_view);
        on_click(&self.now_stop, App::stop_playback_session);

        on_click(&self.player_back, App::leave_player_view);
        on_click(&self.actions, App::show_actions_menu);
        on_click(&self.play, App::toggle_play_pause);
        on_click(&self.previous, App::play_previous);
        on_click(&self.next, App::play_next_or_up_next);
        on_click(&self.mute, App::toggle_mute);
        self.position.on_slider(|e| {
            let value = e.get_value();
            with_app(|app| app.position_slider_moved(value));
        });
        self.panels.on_page_changed(|e| {
            if let Some(panel) = e.get_selection().and_then(|i| usize::try_from(i).ok()).and_then(PlayerPanel::from_index) {
                with_app(|app| app.player_panel_shown(panel));
            }
        });
        self.up_next.on_activate(|| with_app(App::play_selected_up_next));
        self.up_next.on_context_menu(|| with_app(App::up_next_context_menu));
        self.queue.on_activate(|| with_app(App::play_selected_queue_item));
        self.queue.on_context_menu(|| with_app(App::queue_context_menu));
        self.chapters.on_activate(|| with_app(App::play_selected_chapter));
        self.transcript.on_activate(|| with_app(App::play_from_transcript_line));
        self.lyrics.on_activate(|| with_app(App::play_from_lyric_line));
        self.comments.on_item_expanding(|e| {
            if let Some(item) = e.get_item() {
                with_app(|app| app.comment_expanding(&item));
            }
        });
        self.comments.on_selection_changed(|_| with_app(App::comment_selection_changed));
        self.comments.on_item_activated(|_| with_app(App::toggle_replies));
    }

    pub fn set_section_tabs(&self, tabs: &[Section]) {
        while self.section_tabs.get_page_count() > 0 {
            self.section_tabs.remove_page(0);
        }
        for (i, section) in tabs.iter().enumerate() {
            self.section_tabs.add_page(&self.section_pages[i], &section.tab_name().replace('&', "&&"), false, None);
        }
    }

    pub fn select_section_tab(&self, index: usize) {
        if usize::try_from(self.section_tabs.selection()).ok() != Some(index) {
            // Changing the selection without the page-changed event, which would show the section again.
            self.section_tabs.change_selection(index);
        }
    }

    /// Shows the buttons and boxes that belong to the section, as 1.x's bindings did.
    pub fn show_section_controls(&self, browse: &Browse, music: bool) {
        let section = browse.current();
        let playlists = section == Section::Playlists;
        let playlist = section == Section::Playlist;
        let can_edit = browse.is_open_playlist_editable() || browse.is_open_playlist_in_library() || playlists;
        self.back.show(section.is_detail());
        self.play_all.show(playlist || playlists);
        self.shuffle.show(playlist || playlists);
        self.save_playlist.show(browse.can_save_open_playlist());
        self.copy_playlist_link.show(playlist);
        self.new_playlist.show(playlists);
        self.rename_playlist.show(can_edit);
        self.delete_playlist.show(can_edit);
        self.import_export.show(playlists || section == Section::Channels);
        let featured = section == Section::Featured && !music;
        self.category_label.show(featured);
        self.category.show(featured);
        self.channel_tab_label.show(section == Section::Channel);
        self.channel_tab.show(section == Section::Channel);
        self.channel_tab.set_selection(ChannelTab::ALL.iter().position(|t| *t == browse.channel_tab).unwrap_or(0) as u32);
        self.account_page_label.show(section == Section::YouTube);
        self.account_page.show(section == Section::YouTube);
        self.clear_history.show(section == Section::History);
        self.clear_queue.show(section == Section::Queue);
        self.refresh.show(section != Section::Queue);
        self.library.layout();
    }

    /// The heading above the list ("Featured, 20 videos") and the empty text in place of an empty list.
    pub fn show_heading(&self, title: &str, count: &str, empty_text: &str) {
        let heading = if count.is_empty() { title.to_owned() } else { format!("{title}, {count}") };
        self.heading.set_label(&heading.replace('&', "&&"));
        self.list.set_name(title);
        let show_empty = !empty_text.is_empty();
        if show_empty {
            self.empty.set_value(empty_text);
        }
        if self.empty.is_shown() != show_empty {
            self.empty.show(show_empty);
        }
        self.library.layout();
    }

    /// Moves to the list, or to the search box when the list is empty.
    pub fn focus_list(&self) {
        if self.list.is_empty() {
            if self.empty.is_shown() {
                self.empty.set_focus();
            } else {
                self.search_box.set_focus();
            }
        } else {
            if self.list.selected_index().is_none() {
                self.list.step(quiet_list::Step::Home);
            }
            self.list.set_focus();
        }
    }

    pub fn focus_search(&self) {
        self.search_box.set_focus();
        self.search_box.select_all();
    }

    /// The first of the section's buttons and boxes that shows, else the list (Tab from the section tabs).
    pub fn first_section_control(&self) -> Option<&dyn WxWidget> {
        let controls: [&dyn WxWidget; 15] = [
            &self.back,
            &self.play_all,
            &self.shuffle,
            &self.save_playlist,
            &self.copy_playlist_link,
            &self.new_playlist,
            &self.rename_playlist,
            &self.delete_playlist,
            &self.import_export,
            &self.category,
            &self.channel_tab,
            &self.account_page,
            &self.clear_history,
            &self.clear_queue,
            &self.refresh,
        ];
        controls.into_iter().find(|c| c.is_shown())
    }

    pub fn focus_section_tabs(&self) {
        self.section_tabs.set_focus();
    }
}

fn category_label_text(category: timeplayer_core::discovery::FeaturedCategory) -> String {
    category_label(category)
}

/// A panel page holding one quiet list.
fn list_page(panels: &Notebook, name: &str) -> (Panel, QuietList<usize>) {
    let page = Panel::builder(panels).build();
    let list = QuietList::new(&page, &t(name));
    let s = BoxSizer::builder(Orientation::Vertical).build();
    s.add(&list, 1, SizerFlag::Expand, 0);
    page.set_sizer(s, true);
    (page, list)
}
