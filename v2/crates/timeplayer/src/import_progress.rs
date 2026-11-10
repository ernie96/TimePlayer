//! What reading a YouTube account is doing, after the sign-in page (1.x's `ImportProgressWindow`): the step,
//! a progress bar, and Cancel. Each new step is said; F1 says where it is.

use std::cell::Cell;
use std::rc::Rc;

use timeplayer_core::http::Cancel;
use timeplayer_core::i18n::{t, tf};
use timeplayer_core::innertube::{ACCOUNT_IMPORT_STEPS, ImportProgress as Progress};
use wxdragon::prelude::*;

use crate::speech;

pub struct ImportProgress {
    dialog: Dialog,
    status: StaticText,
    bar: Gauge,
    cancel_button: Button,
    cancel: Cancel,
    finished: Rc<Cell<bool>>,
}

impl ImportProgress {
    pub fn open(parent: &Frame) -> Self {
        let dialog = Dialog::builder(parent, &t("Importing from YouTube")).with_size(460, 180).build();
        let panel = Panel::builder(&dialog).build();
        let status = StaticText::builder(&panel).with_label(&t("Starting...")).build();
        let bar = Gauge::builder(&panel).with_range(ACCOUNT_IMPORT_STEPS as i32).build();
        bar.set_name(&t("Import progress"));
        let cancel_button = Button::builder(&panel).with_id(ID_CANCEL).with_label(&t("Cancel")).build();
        let sizer = BoxSizer::builder(Orientation::Vertical).build();
        sizer.add(&status, 0, SizerFlag::Expand | SizerFlag::All, 12);
        sizer.add(&bar, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right | SizerFlag::Bottom, 12);
        sizer.add(&cancel_button, 0, SizerFlag::AlignRight | SizerFlag::All, 12);
        panel.set_sizer(sizer, true);
        let outer = BoxSizer::builder(Orientation::Vertical).build();
        outer.add(&panel, 1, SizerFlag::Expand, 0);
        dialog.set_sizer(outer, true);
        let cancel = Cancel::new();
        let finished = Rc::new(Cell::new(false));
        let window = ImportProgress { dialog, status, bar, cancel_button, cancel, finished };
        window.bind();
        window.dialog.show(true);
        window.cancel_button.set_focus();
        window
    }

    fn bind(&self) {
        let (cancel, button, status) = (self.cancel.clone(), self.cancel_button, self.status);
        let stop = move || {
            if cancel.is_canceled() {
                return;
            }
            cancel.cancel();
            button.enable(false);
            status.set_label(&t("Stopping..."));
        };
        let stop_click = stop.clone();
        self.cancel_button.on_click(move |_| stop_click());
        let status = self.status;
        let stop_key = stop.clone();
        self.dialog.on_char_hook(move |event| {
            let WindowEventData::Keyboard(ref key) = event else { return };
            match key.get_key_code() {
                Some(WXK_F1) => {
                    speech::say(&format!("{} {}", status.get_label(), t("Escape or Cancel stops the import; nothing is imported then.")));
                    event.skip(false);
                }
                Some(WXK_ESCAPE) => {
                    stop_key();
                    event.skip(false);
                }
                _ => event.skip(true),
            }
        });
        let finished = self.finished.clone();
        let cancel = self.cancel.clone();
        self.dialog.on_close(move |event| {
            if !finished.get() {
                // Closing stops the import; the window closes when the import has stopped.
                cancel.cancel();
                event.skip(false);
            } else {
                event.skip(true);
            }
        });
    }

    /// The switch the import checks.
    pub fn cancel(&self) -> Cancel {
        self.cancel.clone()
    }

    pub fn update(&self, progress: &Progress) {
        if self.cancel.is_canceled() {
            return;
        }
        self.status.set_label(&progress.text);
        let value = (progress.step as i32 - 1).max(0);
        self.bar.set_value(if progress.new_step { value } else { value.max(self.bar.get_value()) });
        if progress.new_step {
            speech::say_quietly(&tf("Step {0} of {1}: {2}", &[&progress.step, &progress.steps, &progress.text]));
        }
    }

    /// The import is over (done, failed or canceled): the window goes.
    pub fn finish(self) {
        self.finished.set(true);
        self.dialog.destroy();
    }
}
