//! Report a problem (Control+Shift+B) and Suggest a feature (Control+Shift+F), 1.x's `BugReportWindow`: what
//! happened, an optional contact, the log if wanted, sent privately through the relay.

use timeplayer_core::community::{self, MAX_CONTACT, MAX_DESCRIPTION, Report};
use timeplayer_core::i18n::{t, tf};
use wxdragon::prelude::*;

use crate::window::wx_label;
use crate::{App, dialogs, help, speech, with_app};

pub struct ReportWindow {
    dialog: Dialog,
    description: TextCtrl,
    contact: TextCtrl,
    include_log: CheckBox,
    send: Button,
    status: StaticText,
    feature: bool,
}

impl App {
    pub(crate) fn show_report(&mut self, feature: bool) {
        if let Some(open) = &self.report_window {
            open.dialog.raise();
            open.description.set_focus();
            return;
        }
        self.report_window = Some(ReportWindow::open(&self.ui.frame, feature));
    }

    fn report_send(&mut self) {
        let Some(window) = &self.report_window else { return };
        let description = window.description.get_value();
        if description.trim().is_empty() {
            dialogs::message(&window.dialog, &if window.feature { t("Please describe your idea.") } else { t("Please describe the problem.") }, &window.title());
            window.description.set_focus();
            return;
        }
        window.send.enable(false);
        let status = if window.feature { t("Sending your suggestion...") } else { t("Sending the report...") };
        window.status.set_label(&status);
        speech::say(&status);
        let feature = window.feature;
        let contact = window.contact.get_value();
        let include_log = !feature && window.include_log.get_value();
        let http = self.http.clone();
        self.jobs.run("report", move || {
            let report = Report {
                kind: if feature { "feature" } else { "bug" },
                description: description.clone(),
                contact: (!contact.trim().is_empty()).then_some(contact),
                log: include_log.then(community::read_recent_log),
            };
            let result = community::send_report(&http, &report);
            move |app: &mut App| app.report_sent(result, &description)
        });
    }

    fn report_sent(&mut self, result: timeplayer_core::Result<i64>, description: &str) {
        let Some(window) = &self.report_window else { return };
        window.send.enable(true);
        window.status.set_label("");
        let title = window.title();
        match result {
            Ok(number) => {
                let text = if window.feature {
                    tf("Thank you. Your suggestion was sent as number {0}.", &[&number])
                } else {
                    tf("Thank you. Your report was sent as number {0}.", &[&number])
                };
                dialogs::message(&window.dialog, &text, &title);
                self.close_report();
            }
            Err(e) => {
                let _ = Clipboard::get().set_text(description);
                let reason = if e.message.trim().is_empty() {
                    if window.feature { t("Your suggestion could not be sent.") } else { t("The report could not be sent.") }
                } else {
                    e.message
                };
                dialogs::warning(&window.dialog, &(reason + "\n\n" + &t("What you wrote is copied to the clipboard, so it isn't lost.")), &title);
            }
        }
    }

    pub(crate) fn close_report(&mut self) {
        if let Some(window) = self.report_window.take() {
            window.dialog.destroy();
            self.ui.frame.raise();
        }
    }
}

impl ReportWindow {
    fn title(&self) -> String {
        if self.feature { t("Suggest a feature") } else { t("Report a problem") }
    }

    fn open(parent: &Frame, feature: bool) -> Self {
        let title = if feature { t("Suggest a feature") } else { t("Report a problem") };
        let dialog = Dialog::builder(parent, &title).with_style(DialogStyle::DefaultDialogStyle | DialogStyle::ResizeBorder).with_size(560, 480).build();
        let panel = Panel::builder(&dialog).build();
        let question = if feature { "_What would you like TimePlayer to do? How would it help you?" } else { "_What went wrong? What were you doing when it happened?" };
        let description_label = StaticText::builder(&panel).with_label(&wx_label(question)).build();
        let description = TextCtrl::builder(&panel).with_style(TextCtrlStyle::MultiLine).with_size(Size::new(-1, 150)).build();
        description.set_max_length(MAX_DESCRIPTION);
        let contact_label = StaticText::builder(&panel).with_label(&wx_label("Your _email or name, if you would like an answer (optional):")).build();
        let contact = TextCtrl::builder(&panel).build();
        contact.set_max_length(MAX_CONTACT);
        let include_log = CheckBox::builder(&panel).with_label(&wx_label("_Include TimePlayer's log (recommended: it shows what went wrong)")).build();
        include_log.set_value(true);
        let view_log = Button::builder(&panel).with_label(&wx_label("_View the log")).build();
        let note = StaticText::builder(&panel)
            .with_label(&if feature {
                t("Suggestions go privately to TimePlayer's developer. Enter adds a new line; Tab moves on to Send.")
            } else {
                t("The log lists what TimePlayer did, such as the videos played and any errors. Your Windows user name is taken out. Reports go privately to TimePlayer's developer. Enter adds a new line; Tab moves on to Send.")
            })
            .build();
        note.wrap(500);
        let status = StaticText::builder(&panel).with_label("").build();
        let send = Button::builder(&panel).with_label(&wx_label("_Send")).build();
        let cancel = Button::builder(&panel).with_id(ID_CANCEL).with_label(&t("Cancel")).build();
        let sizer = BoxSizer::builder(Orientation::Vertical).build();
        sizer.add(&description_label, 0, SizerFlag::Left | SizerFlag::Right | SizerFlag::Top, 12);
        sizer.add(&description, 1, SizerFlag::Expand | SizerFlag::All, 12);
        sizer.add(&contact_label, 0, SizerFlag::Left | SizerFlag::Right, 12);
        sizer.add(&contact, 0, SizerFlag::Expand | SizerFlag::All, 12);
        if feature {
            include_log.show(false);
            view_log.show(false);
        } else {
            sizer.add(&include_log, 0, SizerFlag::Left | SizerFlag::Right | SizerFlag::Bottom, 12);
            sizer.add(&view_log, 0, SizerFlag::Left | SizerFlag::Right | SizerFlag::Bottom, 12);
        }
        sizer.add(&note, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right | SizerFlag::Bottom, 12);
        sizer.add(&status, 0, SizerFlag::Expand | SizerFlag::Left | SizerFlag::Right, 12);
        let buttons = BoxSizer::builder(Orientation::Horizontal).build();
        buttons.add(&send, 0, SizerFlag::Right, 8);
        buttons.add(&cancel, 0, SizerFlag::All, 0);
        sizer.add_sizer(&buttons, 0, SizerFlag::AlignRight | SizerFlag::All, 12);
        panel.set_sizer(sizer, true);
        let outer = BoxSizer::builder(Orientation::Vertical).build();
        outer.add(&panel, 1, SizerFlag::Expand, 0);
        dialog.set_sizer(outer, true);

        send.on_click(|_| with_app(App::report_send));
        cancel.on_click(|_| with_app(App::close_report));
        view_log.on_click(|_| view_log_file());
        let status_for_help = status;
        dialog.on_char_hook(move |event| {
            let WindowEventData::Keyboard(ref key) = event else { return };
            match key.get_key_code() {
                Some(WXK_F1) => {
                    status_for_help.set_label(&help::quick::report_form());
                    speech::say(&help::quick::report_form());
                    event.skip(false);
                }
                Some(WXK_ESCAPE) => {
                    with_app(App::close_report);
                    event.skip(false);
                }
                _ => event.skip(true),
            }
        });
        dialog.on_close(|event| {
            with_app(App::close_report);
            event.skip(false);
        });
        dialog.show(true);
        description.set_focus();
        ReportWindow { dialog, description, contact, include_log, send, status, feature }
    }
}

/// View the log: the anonymised log in a text file, opened in the system's text editor.
fn view_log_file() {
    let file = std::env::temp_dir().join("TimePlayer-log-for-report.txt");
    let log = community::read_recent_log();
    let text = if log.is_empty() { t("There is no log yet.") } else { log };
    if std::fs::write(&file, text).is_ok() {
        #[cfg(windows)]
        let _ = std::process::Command::new("notepad.exe").arg(&file).spawn();
        #[cfg(not(windows))]
        crate::platform::open_url(&file.display().to_string());
    }
}
