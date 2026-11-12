//! The sections of the main window (1.x's `MainViewModel.Library`, `.Music`, `.Playlists` and the channel tabs
//! of `.Shorts`): what each shows, loading more as the end of the list nears, and Back through pages opened
//! from a list.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use chrono::Utc;
use timeplayer_core::discovery::FeaturedCategory;
use timeplayer_core::http::Cancel;
use timeplayer_core::i18n::{plural, t, tf};
use timeplayer_core::library::{LIKED_PLAYLIST_ID, PlaylistKind, SubscriptionFeed};
use timeplayer_core::models::{ItemKind, SearchResult};
use timeplayer_core::{innertube, text};

use crate::App;
use crate::speech;

const FEATURED_CACHE_LIFETIME: Duration = Duration::from_secs(10 * 60);
const FEED_AUTO_REFRESH_AGE: chrono::TimeDelta = chrono::TimeDelta::minutes(30);
/// Back remembers this many pages.
const BACK_LIMIT: usize = 20;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Section {
    Search,
    Featured,
    WhatsNew,
    Channels,
    History,
    WatchLater,
    Queue,
    /// Uploads of one channel, opened from any list; Back returns.
    Channel,
    ForYou,
    Playlists,
    /// The videos of one playlist, opened from any list; Back returns.
    Playlist,
    MusicCharts,
    MusicRecent,
    /// The signed-in account's YouTube pages.
    YouTube,
}

impl Section {
    /// Pages opened from a section keep that section's tab.
    pub fn is_detail(self) -> bool {
        matches!(self, Section::Channel | Section::Playlist | Section::Search)
    }

    pub fn tab_name(self) -> String {
        match self {
            Section::Featured => t("Featured"),
            Section::ForYou => t("For You"),
            Section::WhatsNew => t("What's New"),
            Section::Channels => t("Channels"),
            Section::Playlists => t("Playlists"),
            Section::WatchLater => t("Watch Later"),
            Section::History => t("History"),
            Section::Queue => t("Queue"),
            Section::MusicCharts => t("Charts"),
            Section::MusicRecent => t("Recently played"),
            Section::YouTube => t("YouTube"),
            Section::Search => t("Search results"),
            Section::Channel => t("Channels"),
            Section::Playlist => t("Playlist"),
        }
    }
}

pub const VIDEO_TABS: [Section; 8] =
    [Section::Featured, Section::ForYou, Section::WhatsNew, Section::Channels, Section::Playlists, Section::WatchLater, Section::History, Section::Queue];
pub const MUSIC_TABS: [Section; 4] = [Section::MusicCharts, Section::MusicRecent, Section::Playlists, Section::Queue];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SearchKind {
    #[default]
    Videos,
    Playlists,
    Shorts,
    Live,
    Newest,
    MostViewed,
}

impl SearchKind {
    pub const ALL: [SearchKind; 6] = [SearchKind::Videos, SearchKind::Playlists, SearchKind::Shorts, SearchKind::Live, SearchKind::Newest, SearchKind::MostViewed];

    pub fn label(self) -> String {
        match self {
            SearchKind::Videos => t("Videos"),
            SearchKind::Playlists => t("Playlists"),
            SearchKind::Shorts => t("Shorts"),
            SearchKind::Live => t("Live"),
            SearchKind::Newest => t("Newest first"),
            SearchKind::MostViewed => t("Most viewed"),
        }
    }

    fn filter(self) -> Option<&'static str> {
        match self {
            SearchKind::Shorts => Some(innertube::SHORTS_FILTER),
            SearchKind::Live => Some(innertube::LIVE_FILTER),
            SearchKind::Newest => Some(innertube::NEWEST_FILTER),
            SearchKind::MostViewed => Some(innertube::MOST_VIEWED_FILTER),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ChannelTab {
    #[default]
    Videos,
    Shorts,
    Live,
    Playlists,
}

impl ChannelTab {
    pub const ALL: [ChannelTab; 4] = [ChannelTab::Videos, ChannelTab::Shorts, ChannelTab::Live, ChannelTab::Playlists];

    pub fn label(self) -> String {
        match self {
            ChannelTab::Videos => t("Videos"),
            ChannelTab::Shorts => t("Shorts"),
            ChannelTab::Live => t("Live"),
            ChannelTab::Playlists => t("Playlists"),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum AccountPage {
    #[default]
    Home,
    Subscriptions,
    History,
    Liked,
    WatchLater,
}

impl AccountPage {
    pub const ALL: [AccountPage; 5] = [AccountPage::Home, AccountPage::Subscriptions, AccountPage::History, AccountPage::Liked, AccountPage::WatchLater];

    pub fn label(self) -> String {
        match self {
            AccountPage::Home => t("Home"),
            AccountPage::Subscriptions => t("Subscriptions"),
            AccountPage::History => t("History"),
            AccountPage::Liked => t("Liked videos"),
            AccountPage::WatchLater => t("Watch Later"),
        }
    }
}

pub const FEATURED_CATEGORIES: [FeaturedCategory; 7] = [
    FeaturedCategory::Trending,
    FeaturedCategory::Music,
    FeaturedCategory::Gaming,
    FeaturedCategory::Movies,
    FeaturedCategory::News,
    FeaturedCategory::Sports,
    FeaturedCategory::Live,
];

pub fn category_label(category: FeaturedCategory) -> String {
    t(match category {
        FeaturedCategory::Trending => "Trending",
        FeaturedCategory::Music => "Music",
        FeaturedCategory::Gaming => "Gaming",
        FeaturedCategory::Movies => "Movies",
        FeaturedCategory::News => "News",
        FeaturedCategory::Sports => "Sports",
        FeaturedCategory::Live => "Live",
    })
}

/// Loads the next page of the list shown: the items, and whether there are more after them.
pub type Loader = Arc<dyn Fn() -> (Vec<SearchResult>, bool) + Send + Sync>;

#[derive(Clone)]
struct NavEntry {
    section: Section,
    selected: usize,
    channel_id: Option<String>,
    channel_name: String,
    channel_tab: ChannelTab,
    library_playlist: Option<String>,
    youtube_playlist: Option<String>,
    playlist_title: String,
}

/// What the main window's list shows and how it got there.
#[derive(Default)]
pub struct Browse {
    pub section: Option<Section>,
    pub tabs: Vec<Section>,
    pub items: Vec<SearchResult>,
    pub title: String,
    pub empty_text: String,
    pub loading: bool,
    load_more: Option<Loader>,
    pub loading_more: bool,
    speak_next_more: bool,
    /// Bumped by every section shown; what an older load finds is dropped.
    generation: u64,
    quiet_next_load: bool,
    quiet_this_load: bool,
    pub last_search: Vec<SearchResult>,
    pub search_kind: SearchKind,
    pub featured: FeaturedCategory,
    featured_cache: HashMap<FeaturedCategory, (Instant, Vec<SearchResult>, Option<String>)>,
    pub channel_id: Option<String>,
    pub channel_name: String,
    pub channel_tab: ChannelTab,
    pub library_playlist: Option<String>,
    pub youtube_playlist: Option<String>,
    pub playlist_title: String,
    pub playlist_owner: Option<String>,
    pub account_page: AccountPage,
    back: Vec<NavEntry>,
    pub feed: Option<SubscriptionFeed>,
    feed_refreshing: bool,
}

impl Browse {
    pub fn current(&self) -> Section {
        self.section.unwrap_or(Section::Featured)
    }

    pub fn can_load_more(&self) -> bool {
        self.load_more.is_some()
    }

    pub fn is_open_playlist_in_library(&self) -> bool {
        self.current() == Section::Playlist && self.library_playlist.is_some()
    }

    pub fn is_open_playlist_editable(&self) -> bool {
        self.is_open_playlist_in_library() && self.library_playlist.as_deref() != Some(LIKED_PLAYLIST_ID)
    }

    pub fn can_save_open_playlist(&self) -> bool {
        self.current() == Section::Playlist && self.library_playlist.is_none() && self.youtube_playlist.is_some()
    }

    pub fn clear_featured_cache(&mut self) {
        self.featured_cache.clear();
    }

    pub fn forget_feed(&mut self) {
        self.feed = None;
    }
}

/// The first sentence of a longer text, for speech.
pub fn first_sentence(text: &str) -> String {
    let text = text.replace('\n', " ");
    let text = text.trim();
    match text.find(". ") {
        Some(end) => text[..=end].to_owned(),
        None => text.to_owned(),
    }
}

impl App {
    fn hide_shorts(&self) -> bool {
        self.settings.with(|c| c.library.hide_shorts_in_feed)
    }

    fn region(&self) -> String {
        self.settings.with(|c| c.library.featured_region.clone())
    }

    fn without_hidden_shorts(&self, items: Vec<SearchResult>) -> Vec<SearchResult> {
        if self.hide_shorts() { items.into_iter().filter(|v| !v.is_short).collect() } else { items }
    }

    /// The tabs of the mode in use, with YouTube (Control+9) when the account is used for it.
    pub(crate) fn section_tabs(&self) -> Vec<Section> {
        if self.music_mode {
            return MUSIC_TABS.to_vec();
        }
        let mut tabs = VIDEO_TABS.to_vec();
        if self.account_features_on() {
            tabs.push(Section::YouTube);
        }
        tabs
    }

    /// Fills the Sections tab strip again after the mode or the account changed.
    pub(crate) fn refresh_section_tabs(&mut self) {
        let tabs = self.section_tabs();
        if tabs != self.browse.tabs {
            self.browse.tabs = tabs;
            self.ui.set_section_tabs(&self.browse.tabs);
        }
        self.sync_selected_tab(self.browse.current());
    }

    fn sync_selected_tab(&mut self, section: Section) {
        if section.is_detail() {
            return;
        }
        if let Some(index) = self.browse.tabs.iter().position(|s| *s == section) {
            self.ui.select_section_tab(index);
        }
    }

    pub(crate) fn load_start_section_quietly(&mut self) {
        self.browse.quiet_next_load = true;
        let first = self.browse.tabs.first().copied().unwrap_or(Section::Featured);
        self.show_section(first, false, false);
    }

    /// Control+1 to 9.
    pub(crate) fn show_section_by_number(&mut self, number: usize) {
        if let Some(section) = number.checked_sub(1).and_then(|i| self.browse.tabs.get(i)).copied() {
            if self.player_visible {
                self.leave_player_view();
            }
            self.show_section(section, true, false);
        }
    }

    /// Control+Tab and Control+Shift+Tab.
    pub(crate) fn next_section(&mut self, backwards: bool) {
        let count = self.browse.tabs.len();
        if count == 0 {
            return;
        }
        let current = self.browse.tabs.iter().position(|s| *s == self.browse.current()).unwrap_or(0);
        let next = (current + if backwards { count - 1 } else { 1 }) % count;
        self.show_section_by_number(next + 1);
    }

    /// The tab strip's own Left and Right.
    pub(crate) fn section_tab_changed(&mut self, index: usize) {
        if let Some(section) = self.browse.tabs.get(index).copied()
            && section != self.browse.current()
        {
            self.show_section(section, false, false);
        }
    }

    /// F5.
    pub(crate) fn refresh_section(&mut self) {
        self.show_section(self.browse.current(), false, true);
    }

    pub(crate) fn show_section(&mut self, section: Section, move_focus: bool, force_refresh: bool) {
        self.browse.generation += 1;
        self.browse.quiet_this_load = std::mem::take(&mut self.browse.quiet_next_load);
        self.browse.section = Some(section);
        self.browse.load_more = None;
        self.browse.loading_more = false;
        self.sync_selected_tab(section);
        self.ui.show_section_controls(&self.browse, self.music_mode);
        match section {
            Section::Search => {
                self.browse.title = t("Search results");
                self.browse.empty_text = t("No results. Press slash to search again.");
                let items = self.browse.last_search.clone();
                let is_playlists = items.first().is_some_and(|i| i.kind == ItemKind::Playlist);
                self.show_items(items, move_focus, None, None);
                if !is_playlists {
                    self.configure_search_paging();
                }
            }
            Section::Featured => self.show_featured(move_focus, force_refresh, false),
            Section::MusicCharts => self.show_featured(move_focus, force_refresh, true),
            Section::YouTube => self.show_account_page(move_focus),
            Section::WhatsNew => self.show_whats_new(move_focus, force_refresh),
            Section::Channels => {
                self.browse.title = t("Channels");
                self.browse.empty_text = t("You are not subscribed to any channels yet.\nPress Control+S on a video to subscribe to its channel, or choose Import and export above to bring them from YouTube, Google Takeout, NewPipe, LibreTube or FreeTube.");
                let items = self.library.subscriptions().iter().map(|s| s.to_search_result()).collect();
                self.show_items(items, move_focus, None, None);
            }
            Section::History | Section::MusicRecent => self.show_history(section, move_focus),
            Section::WatchLater => {
                self.browse.title = t("Watch Later");
                self.browse.empty_text = t("Watch Later is empty. Press Control+L on any video, or in the player, to save it here.");
                let items = self.library.watch_later().iter().map(|v| v.to_search_result()).collect();
                self.show_items(items, move_focus, None, None);
            }
            Section::Channel => self.show_channel(move_focus),
            Section::ForYou => self.show_for_you(move_focus, force_refresh),
            Section::Playlists => self.show_playlists(move_focus),
            Section::Playlist => self.show_playlist_page(move_focus, force_refresh),
            Section::Queue => {
                self.browse.title = t("Queue");
                self.browse.empty_text = t("The queue is empty. Press Control+Q on any video to add it; queued videos play one after another.");
                self.browse.loading = false;
                let queue: Vec<SearchResult> = self.coordinator.queue().into_iter().map(|q| q.video).collect();
                let spoken = if queue.is_empty() { self.browse.empty_text.clone() } else { plural(queue.len() as i64, "{0} video", "{0} videos") };
                self.browse.items = queue;
                self.show_rows(quiet_list::Select::Start);
                self.say_load(&spoken);
                if move_focus && !self.browse.items.is_empty() {
                    self.ui.focus_list();
                }
            }
        }
    }

    /// Says what a load found, unless it was the quiet one at startup.
    fn say_load(&mut self, text: &str) {
        if std::mem::take(&mut self.browse.quiet_this_load) {
            return;
        }
        speech::say_quietly(text);
    }

    fn begin_loading(&mut self, message: &str) {
        self.browse.loading = true;
        self.browse.items.clear();
        self.show_rows(quiet_list::Select::Start);
        if !self.browse.quiet_this_load {
            speech::say_quietly(message);
        }
    }

    /// Runs `work` for the section on a thread; `then` gets what it found if the section is still the one shown.
    fn load_section<T: Send + 'static>(&self, work: impl FnOnce() -> T + Send + 'static, then: impl FnOnce(&mut App, T) + Send + 'static) {
        let generation = self.browse.generation;
        self.jobs.run("section", move || {
            let found = work();
            move |app: &mut App| {
                if app.browse.generation == generation {
                    then(app, found);
                }
            }
        });
    }

    /// Shows a new list: the first row selected, the count said.
    pub(crate) fn show_items(&mut self, items: Vec<SearchResult>, move_focus: bool, extra_summary: Option<String>, new_since: Option<chrono::DateTime<Utc>>) {
        let items = items.into_iter().map(|i| self.decorate(i, new_since)).collect();
        self.browse.items = items;
        self.browse.loading = false;
        self.show_rows(quiet_list::Select::Start);
        let count = self.browse.items.len();
        let spoken = if count == 0 {
            first_sentence(&self.browse.empty_text)
        } else {
            [Some(self.section_noun(count)), extra_summary].into_iter().flatten().filter(|s| !s.trim().is_empty()).collect::<Vec<_>>().join(", ")
        };
        self.say_load(&spoken);
        if move_focus && count > 0 {
            self.ui.focus_list();
        }
        if self.browse.load_more.is_some() && count < 10 {
            self.load_more(false);
        }
    }

    /// Puts the items in the list, and the heading and empty text above it.
    pub(crate) fn show_rows(&mut self, select: quiet_list::Select<usize>) {
        let section = self.browse.current();
        let rows: Vec<(usize, String)> = if section == Section::Queue {
            let queue = self.coordinator.queue();
            let current = self.coordinator.current_item().map(|q| q.queue_id);
            queue.iter().enumerate().map(|(i, q)| (i, queue_label(q, current == Some(q.queue_id)))).collect()
        } else {
            self.browse.items.iter().enumerate().map(|(i, item)| (i, item.accessible_summary())).collect()
        };
        self.ui.list.update_labelled(rows, select);
        let count = self.ui.list.len();
        self.ui.show_heading(&self.browse.title, &self.section_count_text(count), if count == 0 && !self.browse.loading { &self.browse.empty_text } else { "" });
    }

    fn section_count_text(&self, count: usize) -> String {
        if count == 0 {
            return String::new();
        }
        let more = if self.browse.load_more.is_some() && self.browse.current() != Section::Queue { t(", more load as you go") } else { String::new() };
        self.section_noun(count) + &more
    }

    fn section_noun(&self, count: usize) -> String {
        let count = count as i64;
        match self.browse.current() {
            Section::Channels => plural(count, "{0} channel", "{0} channels"),
            Section::Playlists => plural(count, "{0} playlist", "{0} playlists"),
            Section::Search if self.browse.items.first().is_some_and(|i| i.kind == ItemKind::Playlist) => plural(count, "{0} playlist", "{0} playlists"),
            _ => plural(count, "{0} video", "{0} videos"),
        }
    }

    /// "New", "Watched" or "Resume at ..." said before a video's title.
    pub(crate) fn decorate(&self, mut item: SearchResult, new_since: Option<chrono::DateTime<Utc>>) -> SearchResult {
        if item.kind != ItemKind::Video {
            item.status_prefix = None;
            return item;
        }
        let record = self.settings.with(|c| c.library.record_history);
        item.status_prefix = if new_since.is_some_and(|since| since.timestamp() > 0 && item.published_at.is_some_and(|p| p > since)) {
            Some(t("New"))
        } else if record && let Some(watched) = self.library.get_history(&item.id) {
            Some(match watched.resume_position() {
                Some(resume) => tf("Resume at {0}", &[&text::time_spoken_secs(resume)]),
                None => t("Watched"),
            })
        } else {
            None
        };
        item
    }

    /// After a video played: the list's Watched and Resume labels catch up.
    pub(crate) fn refresh_library_status_of_visible_items(&mut self) {
        match self.browse.current() {
            Section::History | Section::MusicRecent => {
                let selected = self.ui.list.selected_index();
                self.show_section(self.browse.current(), false, false);
                if let Some(index) = selected.filter(|i| *i < self.browse.items.len()) {
                    self.ui.list.select(Some(&index));
                }
            }
            Section::Queue => self.show_rows(quiet_list::Select::Keep),
            _ => {
                let items = std::mem::take(&mut self.browse.items);
                self.browse.items = items
                    .into_iter()
                    .map(|item| if item.kind != ItemKind::Video || item.status_prefix.as_deref() == Some(&t("New")) { item } else { self.decorate(item, None) })
                    .collect();
                self.show_rows(quiet_list::Select::Keep);
            }
        }
    }

    pub(crate) fn selected_item(&self) -> Option<SearchResult> {
        let index = self.ui.list.selected_index()?;
        if self.browse.current() == Section::Queue {
            return self.coordinator.queue().get(index).map(|q| q.video.clone());
        }
        self.browse.items.get(index).cloned()
    }

    /// The list's selection moved: near its end, the next page loads.
    pub(crate) fn list_selection_changed(&mut self) {
        let Some(index) = self.ui.list.selected_index() else { return };
        let count = self.browse.items.len();
        if self.browse.load_more.is_none() || self.browse.current() == Section::Queue || index + 5 < count {
            return;
        }
        self.load_more(index + 1 == count);
    }

    pub(crate) fn load_more(&mut self, speak: bool) {
        self.browse.speak_next_more |= speak;
        let Some(loader) = self.browse.load_more.clone() else { return };
        if self.browse.loading_more {
            return;
        }
        self.browse.loading_more = true;
        let generation = self.browse.generation;
        self.jobs.run("load more", move || {
            // A page with nothing new (all already shown) tries the next one, up to three.
            let mut pages = Vec::new();
            let mut has_more = true;
            for _ in 0..3 {
                let (items, more) = loader();
                has_more = more;
                let empty = items.is_empty();
                pages.extend(items);
                if !empty || !has_more {
                    break;
                }
            }
            move |app: &mut App| app.more_loaded(generation, pages, has_more)
        });
    }

    fn more_loaded(&mut self, generation: u64, items: Vec<SearchResult>, has_more: bool) {
        if generation != self.browse.generation {
            return;
        }
        self.browse.loading_more = false;
        let mut seen: HashSet<String> = self.browse.items.iter().map(|i| i.id.clone()).collect();
        let fresh: Vec<SearchResult> = items.into_iter().filter(|i| seen.insert(i.id.clone())).collect();
        let added = fresh.len();
        for item in fresh {
            let item = self.decorate(item, None);
            if self.browse.current() == Section::Search {
                self.browse.last_search.push(item.clone());
            }
            self.browse.items.push(item);
        }
        if !has_more {
            self.browse.load_more = None;
        }
        self.show_rows(quiet_list::Select::Keep);
        if std::mem::take(&mut self.browse.speak_next_more) {
            speech::say_quietly(&if added > 0 { tf("{0} more.", &[&added]) } else { t("End of list.") });
        }
        if let Some(waiting) = self.after_more_loaded.take() {
            waiting(self, added > 0);
        }
    }

    fn set_load_more(&mut self, loader: Option<Loader>) {
        self.browse.load_more = loader;
    }

    // ----- Search -----

    /// Enter in the search box.
    pub(crate) fn search(&mut self) {
        let query = self.ui.search_box.get_value().trim().to_owned();
        if query.is_empty() {
            speech::say(&t("Please enter a search query or YouTube link."));
            return;
        }
        // A playlist link opens the playlist; a video link (even one inside a playlist) plays the video.
        if self.open_link(&query) {
            return;
        }
        self.clear_error();
        self.last_action = Some(crate::LastAction::Search(query.clone()));
        speech::say(&t("Searching..."));
        let kind = self.browse.search_kind;
        let coordinator = self.coordinator.clone();
        let playlists = self.playlists.clone();
        let inner_tube = self.sources.inner_tube.clone();
        self.searching += 1;
        let searching = self.searching;
        let q = query.clone();
        self.jobs.run("search", move || {
            let result = match kind {
                SearchKind::Playlists => playlists.search(&q, None),
                _ => match kind.filter() {
                    Some(filter) => inner_tube.search_filtered(&q, filter, None),
                    None => coordinator.search(&q),
                },
            };
            move |app: &mut App| {
                if app.searching != searching {
                    return;
                }
                app.search_finished(&query, kind, result);
            }
        });
    }

    fn search_finished(&mut self, query: &str, kind: SearchKind, result: timeplayer_core::Result<timeplayer_core::models::Page<SearchResult>>) {
        match result {
            Ok(page) => {
                let mut seen = HashSet::new();
                self.browse.last_search = page.items.into_iter().filter(|i| seen.insert(i.id.clone())).collect();
                // Results are a page of their own; Back returns to where the search started.
                if self.browse.current() != Section::Search {
                    self.push_navigation();
                }
                if self.player_visible {
                    self.leave_player_view();
                }
                self.show_section(Section::Search, true, false);
                match kind {
                    SearchKind::Videos => self.configure_search_paging(),
                    SearchKind::Playlists => self.configure_token_paging(page.next_token, {
                        let playlists = self.playlists.clone();
                        let query = query.to_owned();
                        move |token| playlists.search(&query, Some(token))
                    }),
                    _ => {
                        let filter = kind.filter().unwrap_or_default();
                        let inner_tube = self.sources.inner_tube.clone();
                        let query = query.to_owned();
                        self.configure_token_paging(page.next_token, move |token| inner_tube.search_filtered(&query, filter, Some(token)));
                    }
                }
                if self.browse.items.is_empty() {
                    speech::say(&text::search_results_spoken(0, query));
                }
            }
            Err(e) => self.show_problem_text(&tf("Search failed: {0}", &[&e.message]), true),
        }
    }

    fn configure_search_paging(&mut self) {
        if !self.coordinator.can_load_more_search_results() || self.browse.search_kind != SearchKind::Videos {
            return;
        }
        let coordinator = self.coordinator.clone();
        self.set_load_more(Some(Arc::new(move || match coordinator.search_more() {
            Ok(page) => (page.items, coordinator.can_load_more_search_results()),
            Err(_) => (Vec::new(), false),
        })));
    }

    /// Paging through a continuation token, starting at `first`.
    fn configure_token_paging(
        &mut self,
        first: Option<String>,
        fetch: impl Fn(&str) -> timeplayer_core::Result<timeplayer_core::models::Page<SearchResult>> + Send + Sync + 'static,
    ) {
        let Some(first) = first else {
            self.set_load_more(None);
            return;
        };
        let next = Arc::new(Mutex::new(Some(first)));
        self.set_load_more(Some(Arc::new(move || {
            let Some(token) = next.lock().unwrap().clone() else { return (Vec::new(), false) };
            match fetch(&token) {
                Ok(page) => {
                    let more = page.next_token.is_some();
                    *next.lock().unwrap() = page.next_token;
                    (page.items, more)
                }
                Err(_) => (Vec::new(), false),
            }
        })));
    }

    /// The Search for box changed.
    pub(crate) fn search_kind_changed(&mut self, kind: SearchKind) {
        if kind == self.browse.search_kind {
            return;
        }
        self.browse.search_kind = kind;
        speech::say_quietly(&if kind == SearchKind::Playlists { t("Searching for playlists.") } else { t("Searching for videos.") });
        if !self.ui.search_box.get_value().trim().is_empty() && self.browse.current() == Section::Search && !self.browse.last_search.is_empty() {
            self.search();
        }
    }

    // ----- Featured and Charts -----

    fn show_featured(&mut self, move_focus: bool, force_refresh: bool, charts: bool) {
        let category = if charts { FeaturedCategory::Music } else { self.browse.featured };
        if charts {
            self.browse.title = t("Charts: top music videos");
            self.browse.empty_text = t("The charts could not be loaded. Press F5 to try again.");
        } else {
            self.browse.title = match category {
                FeaturedCategory::Trending => t("Featured"),
                FeaturedCategory::Music => t("Featured: top music videos"),
                FeaturedCategory::Gaming => t("Featured: gaming this week"),
                FeaturedCategory::Movies => t("Featured: movie trailers this week"),
                FeaturedCategory::Live => t("Featured: live now"),
                other => tf("Featured: {0}", &[&category_label(other)]),
            };
            self.browse.empty_text = t("Featured videos could not be loaded. Press F5 to try again.");
        }
        if !force_refresh
            && let Some((fetched, items, next)) = self.browse.featured_cache.get(&category).cloned()
            && fetched.elapsed() < FEATURED_CACHE_LIFETIME
        {
            let items = if charts { items } else { self.without_hidden_shorts(items) };
            self.configure_featured_paging(category, next, charts);
            self.show_items(items, move_focus, None, None);
            return;
        }
        self.begin_loading(&t("Loading..."));
        let discovery = self.discovery.clone();
        let region = self.region();
        self.load_section(
            move || discovery.featured(category, &region, None),
            move |app, result| match result {
                Ok(page) => {
                    app.browse.featured_cache.insert(category, (Instant::now(), page.items.clone(), page.next_token.clone()));
                    let items = if charts { page.items } else { app.without_hidden_shorts(page.items) };
                    app.configure_featured_paging(category, page.next_token, charts);
                    app.show_items(items, move_focus, None, None);
                }
                Err(e) => {
                    app.show_items(Vec::new(), false, None, None);
                    speech::say(&if charts {
                        tf("The charts are unavailable right now. {0} Press F5 to retry.", &[&e.message])
                    } else {
                        tf("Featured videos are unavailable right now. {0} Press F5 to retry.", &[&e.message])
                    });
                }
            },
        );
    }

    fn configure_featured_paging(&mut self, category: FeaturedCategory, first: Option<String>, charts: bool) {
        let discovery = self.discovery.clone();
        let region = self.region();
        let hide = !charts && self.hide_shorts();
        let Some(first) = first else {
            self.set_load_more(None);
            return;
        };
        let next = Arc::new(Mutex::new(Some(first)));
        self.set_load_more(Some(Arc::new(move || {
            let Some(token) = next.lock().unwrap().clone() else { return (Vec::new(), false) };
            match discovery.featured(category, &region, Some(&token)) {
                Ok(page) => {
                    let more = page.next_token.is_some();
                    *next.lock().unwrap() = page.next_token;
                    let items = if hide { page.items.into_iter().filter(|v| !v.is_short).collect() } else { page.items };
                    (items, more)
                }
                Err(_) => (Vec::new(), false),
            }
        })));
    }

    /// The Category box changed.
    pub(crate) fn featured_category_changed(&mut self, category: FeaturedCategory) {
        if self.browse.featured == category {
            return;
        }
        self.browse.featured = category;
        if self.browse.current() == Section::Featured {
            self.show_section(Section::Featured, false, false);
        }
    }

    // ----- What's New -----

    fn show_whats_new(&mut self, move_focus: bool, force_refresh: bool) {
        self.browse.title = t("What's New");
        let subs = self.library.subscriptions();
        if subs.is_empty() {
            self.browse.empty_text = t("You have no subscriptions yet.\nPress Control+S on any video to subscribe to its channel, or bring them in from Settings (F2), Import.");
            self.show_items(Vec::new(), move_focus, None, None);
            return;
        }
        self.browse.empty_text = t("No recent uploads from your channels. Press F5 to refresh.");
        let new_since = self.library.feed_last_viewed();
        let stale = self.browse.feed.as_ref().is_none_or(|f| Utc::now() - f.fetched_utc > FEED_AUTO_REFRESH_AGE);
        if self.browse.feed.is_some() && !force_refresh {
            self.show_feed_items(new_since, move_focus && !stale);
        }
        if force_refresh || stale {
            self.begin_loading(&tf("Checking {0} for new videos...", &[&plural(subs.len() as i64, "{0} channel", "{0} channels")]));
            let generation = self.browse.generation;
            self.refresh_feed(move |app| {
                if app.browse.generation != generation {
                    return;
                }
                app.show_feed_items(new_since, move_focus);
                if let Some(failed) = app.browse.feed.as_ref().map(|f| f.failed_channels.len()).filter(|n| *n > 0) {
                    speech::say_quietly(&tf("{0} could not be checked.", &[&plural(failed as i64, "{0} channel", "{0} channels")]));
                }
                app.library.mark_feed_viewed(Utc::now());
            });
        } else {
            self.library.mark_feed_viewed(Utc::now());
        }
    }

    fn feed_videos(&self) -> Vec<SearchResult> {
        let hide = self.hide_shorts();
        self.browse.feed.as_ref().map(|f| f.videos.iter().filter(|v| !(hide && v.is_short)).cloned().collect()).unwrap_or_default()
    }

    fn show_feed_items(&mut self, new_since: chrono::DateTime<Utc>, move_focus: bool) {
        let videos = self.feed_videos();
        let known = new_since.timestamp() > 0;
        let new_count = if known { videos.iter().filter(|v| v.published_at.is_some_and(|p| p > new_since)).count() } else { 0 };
        let extra = (new_count > 0).then(|| tf("{0} new", &[&new_count]));
        self.show_items(videos, move_focus, extra, known.then_some(new_since));
    }

    /// Checks the subscribed channels' feeds; `then` runs when it's done (or when the check already running is).
    fn refresh_feed(&mut self, then: impl FnOnce(&mut App) + 'static) {
        self.feed_waiters.push(Box::new(then));
        if self.browse.feed_refreshing {
            return;
        }
        self.browse.feed_refreshing = true;
        let discovery = self.discovery.clone();
        let library = self.library.clone();
        self.jobs.run("feed", move || {
            let feed = discovery.subscription_feed(&library.subscriptions(), &|_| {}, &Cancel::new());
            library.save_feed(&feed);
            move |app: &mut App| {
                app.browse.feed_refreshing = false;
                app.browse.feed = Some(feed);
                for waiter in std::mem::take(&mut app.feed_waiters) {
                    waiter(app);
                }
            }
        });
    }

    /// At startup: says how many new videos the channels have, unless turned off.
    pub(crate) fn check_feed_in_background(&mut self) {
        if !self.settings.with(|c| c.library.check_feed_on_startup) || self.library.subscriptions().is_empty() {
            return;
        }
        let last_viewed = self.library.feed_last_viewed();
        self.refresh_feed(move |app| {
            if last_viewed.timestamp() <= 0 || app.browse.current() == Section::WhatsNew {
                return;
            }
            let new_count = app.feed_videos().iter().filter(|v| v.published_at.is_some_and(|p| p > last_viewed)).count();
            if new_count > 0 {
                speech::say_quietly(&tf("{0} from your channels. Press Control+3 for What's New.", &[&plural(new_count as i64, "{0} new video", "{0} new videos")]));
            }
        });
    }

    // ----- History, For You, Playlists -----

    fn show_history(&mut self, section: Section, move_focus: bool) {
        let record = self.settings.with(|c| c.library.record_history);
        let music = section == Section::MusicRecent;
        self.browse.title = if music { t("Recently played") } else { t("History") };
        self.browse.empty_text = match (record, music) {
            (false, _) => t("Watch history is turned off in Settings (F2)."),
            (true, true) => t("Nothing played in Music yet. Songs you play here appear in this list, newest first."),
            (true, false) => t("Nothing watched yet. Videos you play appear here, newest first, and continue where you left off."),
        };
        let now = Utc::now();
        let items = self
            .library
            .history()
            .into_iter()
            .filter(|h| !music || h.is_music)
            .map(|h| {
                let mut item = h.to_search_result();
                let age = text::age_spoken(h.last_watched_utc, now);
                item.upload_date_text = Some(if music { tf("played {0}", &[&age]) } else { tf("watched {0}", &[&age]) });
                item.published_at = None;
                item
            })
            .collect();
        self.show_items(items, move_focus, None, None);
    }

    fn show_for_you(&mut self, move_focus: bool, force_refresh: bool) {
        self.browse.title = t("For You");
        self.browse.empty_text = t("No recommendations yet. Press F5 to try again.");
        if force_refresh || !self.recommendations.is_cache_fresh() {
            self.begin_loading(&t("Finding videos for you..."));
        }
        let recommendations = self.recommendations.clone();
        let region = self.region();
        let hide = self.hide_shorts();
        self.load_section(
            move || recommendations.get(&region, hide, force_refresh, &|_| {}),
            move |app, result| {
                let extra = (!result.is_personal).then(|| t("trending for now. Watch, like or subscribe and For You learns what you enjoy"));
                app.show_items(result.videos, move_focus, extra, None);
            },
        );
    }

    pub(crate) fn show_playlists(&mut self, move_focus: bool) {
        self.browse.title = t("Playlists");
        self.browse.empty_text = t("No playlists yet.");
        let mut playlists = self.library.playlists();
        if playlists.iter().all(|p| p.kind != PlaylistKind::Liked)
            && let Some(liked) = self.library.get_playlist(LIKED_PLAYLIST_ID)
        {
            playlists.insert(0, liked);
        }
        let items = playlists.iter().map(|p| p.to_search_result()).collect();
        self.show_items(items, move_focus, None, None);
    }

    // ----- A channel -----

    /// Enter on a channel, or Control+G.
    pub(crate) fn open_channel(&mut self, item: &SearchResult) {
        let channel_id = if item.kind == ItemKind::Channel { Some(item.id.clone()) } else { item.channel_id.clone() };
        let Some(channel_id) = channel_id.filter(|c| !c.trim().is_empty()) else {
            speech::say(&t("The channel for this video is not known yet. Play the video, then press Control+G in the player."));
            return;
        };
        if self.player_visible {
            self.leave_player_view();
        }
        if self.browse.current() != Section::Channel || self.browse.channel_id.as_deref() != Some(&channel_id) {
            self.push_navigation();
        }
        if self.browse.channel_id.as_deref() != Some(&channel_id) {
            self.browse.channel_tab = ChannelTab::Videos;
        }
        self.browse.channel_id = Some(channel_id);
        self.browse.channel_name = if item.kind == ItemKind::Channel { item.title.clone() } else { item.channel_title.clone() };
        self.show_section(Section::Channel, true, false);
    }

    fn show_channel(&mut self, move_focus: bool) {
        let Some(channel_id) = self.browse.channel_id.clone() else { return };
        let tab = self.browse.channel_tab;
        if tab != ChannelTab::Videos {
            self.show_channel_tab(channel_id, tab, move_focus);
            return;
        }
        self.browse.title = tf("{0}: latest uploads", &[&self.browse.channel_name]);
        self.browse.empty_text = t("This channel's uploads could not be loaded. Press F5 to try again, or Alt+Left to go back.");
        self.begin_loading(&t("Loading..."));
        let discovery = self.discovery.clone();
        let id = channel_id.clone();
        self.load_section(
            move || discovery.channel_uploads(&id),
            move |app, result| match result {
                Ok(uploads) => {
                    app.browse.channel_name = uploads.channel_name.clone();
                    app.browse.title = tf("{0}: latest uploads", &[&app.browse.channel_name]);
                    let discovery = app.discovery.clone();
                    let id = channel_id.clone();
                    let next: Arc<Mutex<Option<String>>> = Arc::new(Mutex::new(None));
                    app.set_load_more(Some(Arc::new(move || {
                        let token = next.lock().unwrap().clone();
                        match discovery.channel_uploads_page(&id, token.as_deref()) {
                            Ok(page) => {
                                let more = page.next_token.is_some();
                                *next.lock().unwrap() = page.next_token;
                                (page.items, more)
                            }
                            Err(_) => (Vec::new(), false),
                        }
                    })));
                    let extra = app.library.is_subscribed(&channel_id).then(|| t("subscribed"));
                    app.show_items(uploads.videos, move_focus, extra, None);
                }
                Err(e) => {
                    app.show_items(Vec::new(), false, None, None);
                    speech::say(&tf("Could not load the channel. {0}", &[&e.message]));
                }
            },
        );
    }

    fn show_channel_tab(&mut self, channel_id: String, tab: ChannelTab, move_focus: bool) {
        let tab_name = tab.label();
        let channel_name = self.browse.channel_name.clone();
        self.browse.title = tf("{0}: {1}", &[&channel_name, &tab_name]);
        self.browse.empty_text = tf("This channel has nothing in {0}.", &[&tab_name]);
        self.begin_loading(&t("Loading..."));
        let inner_tube = self.sources.inner_tube.clone();
        let youtube_name = match tab {
            ChannelTab::Shorts => "Shorts",
            ChannelTab::Live => "Live",
            _ => "Playlists",
        };
        self.load_section(
            move || {
                let params = inner_tube.channel_tabs(&channel_id).ok()?.get(youtube_name).cloned()?;
                let fetch = {
                    let inner_tube = inner_tube.clone();
                    let channel_id = channel_id.clone();
                    let channel_name = channel_name.clone();
                    move |continuation: Option<&str>| -> (Vec<SearchResult>, Option<String>) {
                        match inner_tube.channel_tab_page(&channel_id, &params, continuation) {
                            Ok(page) => {
                                let items = page
                                    .items
                                    .into_iter()
                                    .map(|mut i| {
                                        if i.kind == ItemKind::Playlist {
                                            if i.channel_title.trim().is_empty() {
                                                i.channel_title = channel_name.clone();
                                            }
                                        } else {
                                            i.channel_title = channel_name.clone();
                                            i.channel_id = Some(channel_id.clone());
                                        }
                                        i
                                    })
                                    .collect();
                                (items, page.next_token)
                            }
                            Err(_) => (Vec::new(), None),
                        }
                    }
                };
                let (items, next) = fetch(None);
                Some((items, next, Arc::new(fetch)))
            },
            move |app, found| {
                let Some((items, next, fetch)) = found else {
                    app.show_items(Vec::new(), move_focus, None, None);
                    return;
                };
                if next.is_some() {
                    let next = Arc::new(Mutex::new(next));
                    app.set_load_more(Some(Arc::new(move || {
                        let Some(token) = next.lock().unwrap().clone() else { return (Vec::new(), false) };
                        let (items, following) = fetch(Some(&token));
                        let more = following.is_some();
                        *next.lock().unwrap() = following;
                        (items, more)
                    })));
                }
                app.show_items(items, move_focus, None, None);
            },
        );
    }

    /// The channel's Tab box changed.
    pub(crate) fn channel_tab_changed(&mut self, tab: ChannelTab) {
        if self.browse.channel_tab == tab {
            return;
        }
        self.browse.channel_tab = tab;
        if self.browse.current() == Section::Channel {
            self.show_section(Section::Channel, false, false);
        }
    }

    // ----- A playlist -----

    pub(crate) fn open_playlist(&mut self, item: &SearchResult) {
        match item.library_playlist_id.clone() {
            Some(library_id) => self.open_playlist_page(Some(library_id), None, &item.title),
            None => self.open_playlist_page(None, Some(item.id.clone()), &item.title),
        }
    }

    pub(crate) fn open_youtube_playlist(&mut self, youtube_id: &str) {
        self.leave_player_view();
        let saved = self.library.find_saved_youtube_playlist(youtube_id);
        let title = saved.as_ref().map(|p| p.name.clone()).unwrap_or_else(|| "YouTube playlist".into());
        self.open_playlist_page(saved.map(|p| p.id), Some(youtube_id.to_owned()), &title);
    }

    fn open_playlist_page(&mut self, library_id: Option<String>, youtube_id: Option<String>, title: &str) {
        if self.browse.current() != Section::Playlist || self.browse.library_playlist != library_id || self.browse.youtube_playlist != youtube_id {
            self.push_navigation();
        }
        self.browse.youtube_playlist = youtube_id.or_else(|| library_id.as_deref().and_then(|id| self.library.get_playlist(id)).and_then(|p| p.youtube_id));
        self.browse.library_playlist = library_id;
        self.browse.playlist_title = title.to_owned();
        self.browse.playlist_owner = None;
        self.show_section(Section::Playlist, true, false);
    }

    fn show_playlist_page(&mut self, move_focus: bool, force_refresh: bool) {
        if let Some(library_id) = self.browse.library_playlist.clone() {
            let Some(playlist) = self.library.get_playlist(&library_id) else {
                self.browse.title = t("Playlist");
                self.browse.empty_text = t("This playlist was deleted. Press Alt+Left to go back.");
                self.show_items(Vec::new(), move_focus, None, None);
                return;
            };
            if force_refresh
                && playlist.kind == PlaylistKind::YouTube
                && let Some(youtube_id) = playlist.youtube_id.clone()
            {
                self.begin_loading(&t("Updating from YouTube..."));
                let playlists = self.playlists.clone();
                self.load_section(
                    move || playlists.all(&youtube_id, 5000, &Cancel::new()).map(|page| (youtube_id, page)),
                    move |app, result| {
                        match result {
                            Ok((youtube_id, page)) => {
                                app.library.save_youtube_playlist(&youtube_id, &page.title, page.owner.as_deref(), &page.videos);
                            }
                            Err(e) => speech::say(&tf("Could not update from YouTube. {0}", &[&e.message])),
                        }
                        app.show_playlist_page(move_focus, false);
                    },
                );
                return;
            }
            self.browse.playlist_title = playlist.name.clone();
            self.browse.title = playlist.name.clone();
            self.browse.empty_text = if playlist.kind == PlaylistKind::Liked {
                t("No liked videos yet. Press Control+K on any video, or in the player, to like it.")
            } else {
                t("This playlist is empty. Press Control+P on any video, or in the player, to add it here.")
            };
            let items = playlist.videos.iter().map(|v| v.to_search_result()).collect();
            self.ui.show_section_controls(&self.browse, self.music_mode);
            self.show_items(items, move_focus, None, None);
            return;
        }
        let Some(playlist_id) = self.browse.youtube_playlist.clone() else { return };
        self.browse.title = self.browse.playlist_title.clone();
        self.browse.empty_text = t("This playlist could not be loaded. It may be private or deleted. Press F5 to try again, or Alt+Left to go back.");
        self.begin_loading(&t("Loading playlist..."));
        let playlists = self.playlists.clone();
        let id = playlist_id.clone();
        self.load_section(
            move || playlists.page(&id, None),
            move |app, result| match result {
                Err(e) => {
                    app.show_items(Vec::new(), false, None, None);
                    speech::say(&tf("Could not load the playlist. {0}", &[&e.message]));
                }
                Ok(page) => {
                    if !page.title.trim().is_empty() {
                        app.browse.playlist_title = page.title.clone();
                    }
                    app.browse.playlist_owner = page.owner.clone();
                    app.browse.title = app.browse.playlist_title.clone();
                    let playlists = app.playlists.clone();
                    app.configure_token_paging(page.next_token, move |token| {
                        playlists.page(&playlist_id, Some(token)).map(|p| timeplayer_core::models::Page { items: p.videos, next_token: p.next_token })
                    });
                    let extra = page.owner.filter(|o| !o.is_empty()).map(|o| tf("by {0}", &[&o]));
                    app.show_items(page.videos, move_focus, extra, None);
                }
            },
        );
    }

    // ----- YouTube (the account's pages) -----

    pub(crate) fn account_features_on(&self) -> bool {
        self.account.for_account_features() && self.settings.with(|c| c.account.use_ == timeplayer_core::config::AccountUse::Full)
    }

    fn show_account_page(&mut self, move_focus: bool) {
        let page = self.browse.account_page;
        self.browse.title = tf("YouTube: {0}", &[&page.label()]);
        self.browse.empty_text = t("Nothing here on YouTube yet.");
        if !self.account_features_on() {
            self.browse.empty_text = t("Sign in to YouTube in Settings, YouTube account, and choose to use it for your home, subscriptions and history.");
            self.show_items(Vec::new(), move_focus, None, None);
            return;
        }
        self.begin_loading(&t("Loading..."));
        let inner_tube = self.sources.inner_tube.clone();
        self.load_section(
            move || match page {
                AccountPage::Home => inner_tube.account_page("FEwhat_to_watch"),
                AccountPage::Subscriptions => inner_tube.account_page("FEsubscriptions"),
                AccountPage::History => inner_tube.account_page("FEhistory"),
                AccountPage::Liked => inner_tube.account_playlist("LL").map(|p| p.videos),
                AccountPage::WatchLater => inner_tube.account_playlist("WL").map(|p| p.videos),
            },
            move |app, result| match result {
                Ok(items) => app.show_items(items, move_focus, None, None),
                Err(e) => {
                    app.browse.empty_text = if e.message.trim().is_empty() { t("YouTube could not be reached. Press F5 to try again.") } else { e.message };
                    app.show_items(Vec::new(), move_focus, None, None);
                }
            },
        );
    }

    pub(crate) fn account_page_changed(&mut self, page: AccountPage) {
        if self.browse.account_page == page {
            return;
        }
        self.browse.account_page = page;
        if self.browse.current() == Section::YouTube {
            self.show_section(Section::YouTube, false, false);
        }
    }

    // ----- Back -----

    pub(crate) fn push_navigation(&mut self) {
        let Some(section) = self.browse.section else { return };
        self.browse.back.push(NavEntry {
            section,
            selected: self.ui.list.selected_index().unwrap_or(0),
            channel_id: self.browse.channel_id.clone(),
            channel_name: self.browse.channel_name.clone(),
            channel_tab: self.browse.channel_tab,
            library_playlist: self.browse.library_playlist.clone(),
            youtube_playlist: self.browse.youtube_playlist.clone(),
            playlist_title: self.browse.playlist_title.clone(),
        });
        if self.browse.back.len() > BACK_LIMIT {
            self.browse.back.remove(0);
        }
    }

    pub(crate) fn clear_navigation(&mut self) {
        self.browse.back.clear();
    }

    /// Alt+Left, Backspace or Escape on a page opened from a list.
    pub(crate) fn go_back(&mut self) {
        if !self.browse.current().is_detail() {
            return;
        }
        let Some(entry) = self.browse.back.pop() else {
            let first = self.browse.tabs.first().copied().unwrap_or(Section::Featured);
            self.show_section(first, true, false);
            return;
        };
        self.browse.channel_id = entry.channel_id;
        self.browse.channel_name = entry.channel_name;
        self.browse.channel_tab = entry.channel_tab;
        self.browse.library_playlist = entry.library_playlist;
        self.browse.youtube_playlist = entry.youtube_playlist;
        self.browse.playlist_title = entry.playlist_title;
        self.browse.quiet_next_load = false;
        self.show_section(entry.section, false, false);
        self.reselect_after_load = Some(entry.selected);
        self.apply_reselect();
        self.ui.focus_list();
    }

    /// Selects the row Back remembered once the list it belongs to is there.
    pub(crate) fn apply_reselect(&mut self) {
        if let Some(index) = self.reselect_after_load
            && index < self.ui.list.len()
            && !self.browse.loading
        {
            self.reselect_after_load = None;
            self.ui.list.select(Some(&index));
        }
    }

    // ----- Removing from a section -----

    /// Takes a row out of the list and keeps the selection where it was.
    pub(crate) fn remove_item_keeping_position(&mut self, id: &str) {
        let Some(index) = self.browse.items.iter().position(|i| i.id == id) else { return };
        self.browse.items.remove(index);
        if self.browse.items.is_empty() {
            self.show_rows(quiet_list::Select::Start);
            speech::say_quietly(&tf("{0} is now empty.", &[&self.browse.title]));
            return;
        }
        let keep = index.min(self.browse.items.len() - 1);
        self.show_rows(quiet_list::Select::Key(keep));
    }
}

/// A queue row, as 1.x's queue item reads.
fn queue_label(item: &timeplayer_core::playback::QueueItem, playing: bool) -> String {
    let mut label = item.video.accessible_summary();
    if playing {
        label = format!("{}. {label}", t("Now playing"));
    } else if item.played {
        label = format!("{label}. {}", t("Played"));
    }
    label
}
