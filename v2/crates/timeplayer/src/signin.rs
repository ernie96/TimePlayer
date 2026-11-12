//! Import by signing in to YouTube (1.x's `GoogleSignInWindow`): Google's own sign-in page in a web view with a
//! browser profile of its own. Every 1.5 seconds the page's youtube.com cookies are read; once they hold a
//! signed-in session they go to the account, the page closes and its profile is erased. The cookies are read
//! from WebView2 on Windows, WebKitGTK on Linux and WKWebView on macOS.

use std::cell::RefCell;
use std::rc::Rc;

use timeplayer_core::account::SignInCookie;
use timeplayer_core::i18n::t;
use wxdragon::prelude::*;
use wxdragon::widgets::{WebView, WebViewBackend};

use crate::{App, dialogs, with_app};

const SIGN_IN_URL: &str = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true\
&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26app%3Ddesktop%26next%3D%252F";

/// Google turns away sign-ins from pages embedded in apps, which it mostly tells by a WebKit name without
/// "Version/… Safari/…"; on macOS and Linux the page says it's Safari or GNOME Web. Windows' WebView2 already
/// names itself as Edge, which Google accepts.
const BROWSER_NAME: Option<&str> = if cfg!(target_os = "macos") {
    Some("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Safari/605.1.15")
} else if cfg!(target_os = "linux") {
    Some("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15")
} else {
    None
};

/// The sign-in page's own browser profile, erased after each sign-in.
pub fn browser_folder() -> std::path::PathBuf {
    timeplayer_core::paths::data_folder().join("signin-browser")
}

/// Erases the page's profile (cookies and all), trying again while the page's browser still holds it.
pub fn forget_browser() {
    let folder = browser_folder();
    for _ in 0..10 {
        if !folder.exists() || std::fs::remove_dir_all(&folder).is_ok() {
            return;
        }
        std::thread::sleep(std::time::Duration::from_millis(300));
    }
}

/// Points WebView2 at the page's own profile. Call before any web view exists.
pub fn prepare_browser_profile() {
    #[cfg(windows)]
    // SAFETY: called on the window's thread before any web view, and nothing else reads this variable.
    unsafe {
        std::env::set_var("WEBVIEW2_USER_DATA_FOLDER", browser_folder());
    }
}

pub struct SignInWindow {
    frame: Frame,
    timer: Rc<Timer<Frame>>,
}

impl App {
    /// Import by signing in to YouTube: the page, then the import, or the import at once when signed in.
    pub(crate) fn import_by_signing_in(&mut self) {
        if self.account.is_signed_in() {
            if std::mem::take(&mut self.sign_in_only) {
                crate::speech::say(&self.account_status());
            } else {
                self.import_from_account(true);
            }
            return;
        }
        if cfg!(windows) && !WebView::is_backend_available(WebViewBackend::Edge) {
            self.sign_in_only = false;
            dialogs::warning(&self.ui.frame, &t("Signing in needs the Microsoft Edge WebView2 Runtime. Install it from Microsoft, then try again."), &t("Sign in to YouTube"));
            return;
        }
        if let Some(open) = &self.sign_in_window {
            open.frame.raise();
            return;
        }
        forget_browser();
        prepare_browser_profile();
        self.sign_in_window = Some(SignInWindow::open(&self.ui.frame));
    }

    /// The page's cookies look signed in: true when the account took them.
    fn sign_in_cookies(&mut self, cookies: Vec<SignInCookie>) {
        if self.account.import_cookies(cookies).is_err() {
            return;
        }
        if let Some(window) = self.sign_in_window.take() {
            window.close();
        }
        std::thread::spawn(forget_browser);
        self.ui.frame.raise();
        if std::mem::take(&mut self.sign_in_only) {
            self.after_sign_in(Ok(()));
        } else {
            self.import_from_account(false);
        }
    }

    fn sign_in_closed(&mut self) {
        if self.sign_in_window.take().is_some() {
            self.sign_in_only = false;
            std::thread::spawn(forget_browser);
            self.ui.focus_list();
        }
    }
}

impl SignInWindow {
    fn open(parent: &Frame) -> Self {
        let frame = Frame::builder().with_parent(parent).with_title(&t("Sign in to YouTube")).with_size(Size::new(520, 760)).build();
        let view = WebView::builder(&frame).build();
        view.set_name(&t("Google sign-in page"));
        if let Some(name) = BROWSER_NAME {
            view.set_user_agent(name);
        }
        let sizer = BoxSizer::builder(Orientation::Vertical).build();
        sizer.add(&view, 1, SizerFlag::Expand, 0);
        frame.set_sizer(sizer, true);
        let state = Rc::new(Poll { view, last_tried: RefCell::new(None), busy: RefCell::new(false) });
        let timer = Rc::new(Timer::new(&frame));
        timer.on_tick(move |_| state.tick());
        timer.start(1500, false);
        let ticking = timer.clone();
        frame.on_close(move |event| {
            ticking.stop();
            with_app(App::sign_in_closed);
            event.skip(true);
        });
        view.load_url(SIGN_IN_URL);
        frame.show(true);
        view.set_focus();
        Self { frame, timer }
    }

    fn close(self) {
        self.timer.stop();
        self.frame.destroy();
    }
}

struct Poll {
    view: WebView,
    last_tried: RefCell<Option<String>>,
    busy: RefCell<bool>,
}

impl Poll {
    fn tick(self: &Rc<Self>) {
        if *self.busy.borrow() {
            return;
        }
        *self.busy.borrow_mut() = true;
        let me = self.clone();
        browser::read_cookies(&self.view, move |cookies| {
            *me.busy.borrow_mut() = false;
            let Some(cookies) = cookies else { return };
            let has = |names: &[&str]| cookies.iter().any(|c| names.contains(&c.name.as_str()));
            if !has(&["SAPISID", "__Secure-3PAPISID"]) || !has(&["LOGIN_INFO", "SID"]) {
                return;
            }
            let key: String = cookies.iter().map(|c| format!("{}={};", c.name, c.value)).collect();
            if me.last_tried.borrow().as_deref() == Some(key.as_str()) {
                return;
            }
            *me.last_tried.borrow_mut() = Some(key);
            with_app(|app| app.sign_in_cookies(cookies));
        });
    }
}

const SITE: &str = "https://www.youtube.com";

#[cfg(windows)]
mod browser {
    use std::cell::RefCell;

    use timeplayer_core::account::SignInCookie;
    use webview2_com::Microsoft::Web::WebView2::Win32::{ICoreWebView2, ICoreWebView2_2, ICoreWebView2CookieList};
    use webview2_com::{GetCookiesCompletedHandler, take_pwstr};
    use windows::core::{BOOL, HSTRING, Interface, PCWSTR, PWSTR};
    use wxdragon::widgets::WebView;

    fn core(view: &WebView) -> Option<ICoreWebView2> {
        let raw = view.get_native_backend();
        if raw.is_null() {
            return None;
        }
        // SAFETY: on Windows, wxWebView's Edge backend hands out its ICoreWebView2; borrowed, then AddRef'd by cloned().
        Some(unsafe { ICoreWebView2::from_raw_borrowed(&raw) }?.clone())
    }

    pub fn read_cookies(view: &WebView, done: impl FnOnce(Option<Vec<SignInCookie>>) + 'static) {
        // SAFETY: COM calls on the window's thread.
        let manager = core(view).and_then(|c| unsafe { c.cast::<ICoreWebView2_2>().ok()?.CookieManager().ok() });
        let Some(manager) = manager else {
            done(None);
            return;
        };
        let done = RefCell::new(Some(done));
        let handler = GetCookiesCompletedHandler::create(Box::new(move |result, list: Option<ICoreWebView2CookieList>| {
            let cookies = match (result, list) {
                (Ok(()), Some(list)) => Some(collect(&list)),
                _ => None,
            };
            if let Some(done) = done.borrow_mut().take() {
                done(cookies);
            }
            Ok(())
        }));
        let uri = HSTRING::from(super::SITE);
        // SAFETY: uri outlives the call; the handler is called back on this thread.
        if unsafe { manager.GetCookies(PCWSTR(uri.as_ptr()), &handler) }.is_err() {
            log::warn!("Couldn't ask the sign-in page for its cookies");
        }
    }

    fn collect(list: &ICoreWebView2CookieList) -> Vec<SignInCookie> {
        let mut cookies = Vec::new();
        let mut count = 0u32;
        // SAFETY: COM calls with valid out pointers; strings come back CoTaskMem-allocated and take_pwstr frees them.
        unsafe {
            if list.Count(&mut count).is_err() {
                return cookies;
            }
            for i in 0..count {
                let Ok(cookie) = list.GetValueAtIndex(i) else { continue };
                let (mut name, mut value, mut domain, mut path) = (PWSTR::null(), PWSTR::null(), PWSTR::null(), PWSTR::null());
                let (mut secure, mut session, mut expires) = (BOOL(0), BOOL(0), 0f64);
                if cookie.Name(&mut name).is_err() || cookie.Value(&mut value).is_err() || cookie.Domain(&mut domain).is_err() || cookie.Path(&mut path).is_err() {
                    continue;
                }
                let _ = cookie.IsSecure(&mut secure);
                let _ = cookie.IsSession(&mut session);
                let _ = cookie.Expires(&mut expires);
                let domain = take_pwstr(domain);
                cookies.push(SignInCookie {
                    include_subdomains: domain.starts_with('.'),
                    domain,
                    path: take_pwstr(path),
                    secure: secure.as_bool(),
                    expires: if session.as_bool() { 0 } else { expires as i64 },
                    name: take_pwstr(name),
                    value: take_pwstr(value),
                });
            }
        }
        cookies
    }
}

/// WebKitGTK 4.1's cookie manager and libsoup 3's cookies, as Tin Pan 2 reads them.
#[cfg(target_os = "linux")]
mod browser {
    use std::ffi::{CStr, CString, c_char, c_int, c_void};

    use timeplayer_core::account::SignInCookie;
    use wxdragon::widgets::WebView;

    type Callback = unsafe extern "C" fn(source: *mut c_void, result: *mut c_void, data: *mut c_void);

    #[link(name = "webkit2gtk-4.1")]
    unsafe extern "C" {
        fn webkit_web_view_get_context(view: *mut c_void) -> *mut c_void;
        fn webkit_web_context_get_cookie_manager(context: *mut c_void) -> *mut c_void;
        fn webkit_cookie_manager_get_cookies(manager: *mut c_void, uri: *const c_char, cancellable: *mut c_void, callback: Callback, data: *mut c_void);
        fn webkit_cookie_manager_get_cookies_finish(manager: *mut c_void, result: *mut c_void, error: *mut *mut c_void) -> *mut GList;
    }

    #[link(name = "soup-3.0")]
    unsafe extern "C" {
        fn soup_cookie_get_name(cookie: *mut c_void) -> *const c_char;
        fn soup_cookie_get_value(cookie: *mut c_void) -> *const c_char;
        fn soup_cookie_get_domain(cookie: *mut c_void) -> *const c_char;
        fn soup_cookie_get_path(cookie: *mut c_void) -> *const c_char;
        fn soup_cookie_get_secure(cookie: *mut c_void) -> c_int;
        fn soup_cookie_get_expires(cookie: *mut c_void) -> *mut c_void;
        fn soup_cookie_free(cookie: *mut c_void);
    }

    #[link(name = "glib-2.0")]
    unsafe extern "C" {
        fn g_list_free(list: *mut GList);
        fn g_error_free(error: *mut c_void);
        fn g_date_time_to_unix(datetime: *mut c_void) -> i64;
    }

    #[repr(C)]
    struct GList {
        data: *mut c_void,
        next: *mut GList,
        prev: *mut GList,
    }

    struct Reading {
        manager: *mut c_void,
        done: Box<dyn FnOnce(Option<Vec<SignInCookie>>)>,
    }

    unsafe fn text(s: *const c_char) -> String {
        if s.is_null() { String::new() } else { unsafe { CStr::from_ptr(s) }.to_string_lossy().into_owned() }
    }

    unsafe extern "C" fn got_cookies(_source: *mut c_void, result: *mut c_void, data: *mut c_void) {
        // SAFETY: `data` is the box made for this one request, taken back exactly once, on the main thread.
        let reading = unsafe { Box::from_raw(data.cast::<Reading>()) };
        let mut cookies = Vec::new();
        let mut error = std::ptr::null_mut();
        // SAFETY: the list and its cookies are ours to read and free, as WebKitGTK documents.
        unsafe {
            let list = webkit_cookie_manager_get_cookies_finish(reading.manager, result, &mut error);
            if !error.is_null() {
                g_error_free(error);
            }
            let mut item = list;
            while !item.is_null() {
                let cookie = (*item).data;
                let domain = text(soup_cookie_get_domain(cookie));
                let expires = soup_cookie_get_expires(cookie);
                cookies.push(SignInCookie {
                    include_subdomains: domain.starts_with('.'),
                    domain,
                    path: text(soup_cookie_get_path(cookie)),
                    secure: soup_cookie_get_secure(cookie) != 0,
                    expires: if expires.is_null() { 0 } else { g_date_time_to_unix(expires) },
                    name: text(soup_cookie_get_name(cookie)),
                    value: text(soup_cookie_get_value(cookie)),
                });
                soup_cookie_free(cookie);
                item = (*item).next;
            }
            if !list.is_null() {
                g_list_free(list);
            }
        }
        (reading.done)(Some(cookies));
    }

    pub fn read_cookies(view: &WebView, done: impl FnOnce(Option<Vec<SignInCookie>>) + 'static) {
        let raw = view.get_native_backend();
        if raw.is_null() {
            done(None);
            return;
        }
        // SAFETY: on GTK, wxWebView's backend is its WebKitWebView; the context and its manager live as long as it.
        let manager = unsafe { webkit_web_context_get_cookie_manager(webkit_web_view_get_context(raw)) };
        if manager.is_null() {
            done(None);
            return;
        }
        let Ok(uri) = CString::new(super::SITE) else { return };
        let data = Box::into_raw(Box::new(Reading { manager, done: Box::new(done) })).cast::<c_void>();
        // SAFETY: the manager lives with the view; WebKitGTK copies the URI; got_cookies takes `data` back.
        unsafe { webkit_cookie_manager_get_cookies(manager, uri.as_ptr(), std::ptr::null_mut(), got_cookies, data) };
    }
}

/// WKWebView's cookie store, as Tin Pan 2 reads it.
#[cfg(target_os = "macos")]
mod browser {
    use std::cell::RefCell;
    use std::ffi::{CStr, c_char};

    use block::ConcreteBlock;
    use objc::runtime::{BOOL, NO, Object};
    use objc::{msg_send, sel, sel_impl};
    use timeplayer_core::account::SignInCookie;
    use wxdragon::widgets::WebView;

    unsafe fn text(string: *mut Object) -> String {
        if string.is_null() {
            return String::new();
        }
        // SAFETY: a live NSString; UTF8String lives as long as it, and it's copied at once.
        unsafe {
            let utf8: *const c_char = msg_send![string, UTF8String];
            if utf8.is_null() { String::new() } else { CStr::from_ptr(utf8).to_string_lossy().into_owned() }
        }
    }

    pub fn read_cookies(view: &WebView, done: impl FnOnce(Option<Vec<SignInCookie>>) + 'static) {
        let web_view = view.get_native_backend().cast::<Object>();
        if web_view.is_null() {
            done(None);
            return;
        }
        // SAFETY: the WKWebView's own getters, on the main thread.
        let store: *mut Object = unsafe {
            let configuration: *mut Object = msg_send![web_view, configuration];
            let data: *mut Object = msg_send![configuration, websiteDataStore];
            msg_send![data, httpCookieStore]
        };
        if store.is_null() {
            done(None);
            return;
        }
        let done = RefCell::new(Some(done));
        let got = ConcreteBlock::new(move |list: *mut Object| {
            let Some(done) = done.borrow_mut().take() else { return };
            let mut cookies = Vec::new();
            // SAFETY: WebKit hands the block an NSArray of NSHTTPCookie, on the main thread.
            unsafe {
                let count: usize = if list.is_null() { 0 } else { msg_send![list, count] };
                for i in 0..count {
                    let cookie: *mut Object = msg_send![list, objectAtIndex: i];
                    let domain = text(msg_send![cookie, domain]);
                    if !domain.trim_start_matches('.').ends_with("youtube.com") {
                        continue;
                    }
                    let secure: BOOL = msg_send![cookie, isSecure];
                    let session: BOOL = msg_send![cookie, isSessionOnly];
                    let date: *mut Object = msg_send![cookie, expiresDate];
                    let expires: f64 = if date.is_null() { 0.0 } else { msg_send![date, timeIntervalSince1970] };
                    cookies.push(SignInCookie {
                        include_subdomains: domain.starts_with('.'),
                        domain,
                        path: text(msg_send![cookie, path]),
                        secure: secure != NO,
                        expires: if session != NO { 0 } else { expires as i64 },
                        name: text(msg_send![cookie, name]),
                        value: text(msg_send![cookie, value]),
                    });
                }
            }
            done(Some(cookies));
        })
        .copy();
        // SAFETY: getAllCookies: copies the block and calls it once, on the main thread.
        unsafe {
            let _: () = msg_send![store, getAllCookies: &*got];
        }
    }
}

#[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
mod browser {
    use timeplayer_core::account::SignInCookie;
    use wxdragon::widgets::WebView;

    pub fn read_cookies(_view: &WebView, done: impl FnOnce(Option<Vec<SignInCookie>>) + 'static) {
        done(None);
    }
}
