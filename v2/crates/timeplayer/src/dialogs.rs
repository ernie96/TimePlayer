//! Pop-up menus, message boxes, the text prompt (1.x's `TextPromptWindow`) and file pickers.

use std::cell::{Cell, RefCell};
use std::rc::Rc;

use timeplayer_core::i18n::t;
use wxdragon::prelude::*;

use crate::App;

/// Pop-up menu items get ids from here up.
const FIRST_POPUP_ID: i32 = 20_000;

thread_local! {
    /// A pop-up menu's choice, when it arrived while the app was busy showing the menu.
    static PICKED: Cell<Option<i32>> = const { Cell::new(None) };
}

/// Called by the frame's menu handler when the app couldn't take the command itself.
pub fn remember_pick(id: i32) {
    if id >= FIRST_POPUP_ID {
        PICKED.set(Some(id));
    }
}

/// Where a pop-up menu shows.
#[derive(Clone, Copy)]
pub enum MenuTarget {
    List,
    UpNext,
    PlayerQueue,
    Actions,
    Comments,
    Frame,
}

type MenuAction = Box<dyn FnOnce(&mut App)>;

/// A pop-up menu whose items run closures with the app.
pub struct PopupMenu {
    menu: Menu,
    actions: Rc<RefCell<Vec<Option<MenuAction>>>>,
}

impl PopupMenu {
    pub fn new() -> Self {
        Self { menu: Menu::builder().build(), actions: Rc::new(RefCell::new(Vec::new())) }
    }

    fn sub(&self) -> Self {
        Self { menu: Menu::builder().build(), actions: self.actions.clone() }
    }

    fn next_id(&self, action: MenuAction) -> i32 {
        let mut actions = self.actions.borrow_mut();
        actions.push(Some(action));
        FIRST_POPUP_ID + actions.len() as i32 - 1
    }

    /// An item; `label` uses "_" for its access key, `keys` is shown after a tab.
    pub fn item(&mut self, label: &str, keys: &str, action: impl FnOnce(&mut App) + 'static) {
        let id = self.next_id(Box::new(action));
        self.menu.append(id, &menu_text(label, keys), "", ItemKind::Normal);
    }

    /// An item already translated (a playlist's name, a caption track): no access key is made from it.
    pub fn plain_item(&mut self, text: &str, action: impl FnOnce(&mut App) + 'static) {
        let id = self.next_id(Box::new(action));
        self.menu.append(id, &text.replace('&', "&&"), "", ItemKind::Normal);
    }

    pub fn check(&mut self, label: &str, keys: &str, checked: bool, action: impl FnOnce(&mut App) + 'static) {
        let id = self.next_id(Box::new(action));
        self.menu.append(id, &menu_text(label, keys), "", ItemKind::Check);
        self.menu.check_item(id, checked);
    }

    pub fn plain_check(&mut self, text: &str, checked: bool, action: impl FnOnce(&mut App) + 'static) {
        let id = self.next_id(Box::new(action));
        self.menu.append(id, &text.replace('&', "&&"), "", ItemKind::Check);
        self.menu.check_item(id, checked);
    }

    pub fn separator(&mut self) {
        self.menu.append_separator();
    }

    /// A submenu filled by `fill`.
    pub fn submenu(&mut self, label: &str, fill: impl FnOnce(&mut PopupMenu)) {
        let mut sub = self.sub();
        fill(&mut sub);
        self.menu.append_submenu(sub.menu, &menu_text(label, ""), "");
    }

    /// Shows the menu (under a list's selected row, or on a control) and runs what was chosen.
    pub fn show_at(mut self, app: &mut App, target: MenuTarget) {
        PICKED.set(None);
        let ui = &app.ui;
        match target {
            MenuTarget::List => ui.list.popup_menu(&mut self.menu),
            MenuTarget::UpNext => ui.up_next.popup_menu(&mut self.menu),
            MenuTarget::PlayerQueue => ui.queue.popup_menu(&mut self.menu),
            MenuTarget::Actions => {
                ui.actions.popup_menu(&mut self.menu, None);
            }
            MenuTarget::Comments => {
                ui.comments.popup_menu(&mut self.menu, None);
            }
            MenuTarget::Frame => {
                ui.frame.popup_menu(&mut self.menu, None);
            }
        }
        self.run_picked(app);
    }

    fn run_picked(self, app: &mut App) {
        if let Some(id) = PICKED.take()
            && let Some(action) = self.actions.borrow_mut().get_mut((id - FIRST_POPUP_ID) as usize).and_then(Option::take)
        {
            action(app);
        }
    }
}

/// "Add to _queue\tCtrl+Q" with wxWidgets' "&" for the access key.
pub fn menu_text(label: &str, keys: &str) -> String {
    let text = crate::window::wx_label(label);
    if keys.is_empty() { text } else { format!("{text}\t{}", t(keys)) }
}

pub fn message(parent: &impl WxWidget, text: &str, title: &str) {
    MessageDialog::builder(parent, text, title).with_style(MessageDialogStyle::OK | MessageDialogStyle::IconInformation).build().show_modal();
}

pub fn warning(parent: &impl WxWidget, text: &str, title: &str) {
    MessageDialog::builder(parent, text, title).with_style(MessageDialogStyle::OK | MessageDialogStyle::IconWarning).build().show_modal();
}

/// Yes or No, No first (1.x's Confirm defaulted to No).
pub fn confirm(parent: &impl WxWidget, title: &str, text: &str) -> bool {
    let dialog = MessageDialog::builder(parent, text, title).with_style(MessageDialogStyle::YesNo | MessageDialogStyle::IconWarning | MessageDialogStyle::NoDefault).build();
    dialog.show_modal() == ID_YES
}

/// Yes or No for a question, No first.
pub fn ask(parent: &impl WxWidget, title: &str, text: &str) -> bool {
    let dialog = MessageDialog::builder(parent, text, title).with_style(MessageDialogStyle::YesNo | MessageDialogStyle::IconQuestion | MessageDialogStyle::NoDefault).build();
    dialog.show_modal() == ID_YES
}

/// Yes or No, Yes first (the update offer).
pub fn ask_yes_first(parent: &impl WxWidget, title: &str, text: &str) -> bool {
    let dialog = MessageDialog::builder(parent, text, title).with_style(MessageDialogStyle::YesNo | MessageDialogStyle::IconInformation).build();
    dialog.show_modal() == ID_YES
}

/// A box for a name (1.x's TextPromptWindow): None when cancelled or left empty.
pub fn ask_text(parent: &impl WxWidget, title: &str, prompt: &str, initial: &str) -> Option<String> {
    let dialog = TextEntryDialog::builder(parent, prompt, title).with_default_value(initial).build();
    if dialog.show_modal() != ID_OK {
        return None;
    }
    let value = dialog.get_value()?.trim().to_owned();
    (!value.is_empty()).then_some(value)
}

/// A file to open; `filter` is wxWidgets' "Name (*.zip)|*.zip|..." form.
pub fn open_file(parent: &impl WxWidget, title: &str, filter: &str) -> Option<std::path::PathBuf> {
    let dialog = FileDialog::builder(parent).with_message(title).with_wildcard(filter).with_style(FileDialogStyle::Open | FileDialogStyle::FileMustExist).build();
    if dialog.show_modal() != ID_OK {
        return None;
    }
    dialog.get_path().map(std::path::PathBuf::from)
}

/// Where to save a file.
pub fn save_file(parent: &impl WxWidget, title: &str, filter: &str, default_name: &str) -> Option<std::path::PathBuf> {
    let dialog = FileDialog::builder(parent)
        .with_message(title)
        .with_wildcard(filter)
        .with_default_file(default_name)
        .with_style(FileDialogStyle::Save | FileDialogStyle::OverwritePrompt)
        .build();
    if dialog.show_modal() != ID_OK {
        return None;
    }
    dialog.get_path().map(std::path::PathBuf::from)
}
