//! Video on macOS: there a separate mpv program can't draw into TimePlayer's window, so mpv runs inside
//! TimePlayer as libmpv (bundled in TimePlayer.app/Contents/Frameworks) and hands each frame to TimePlayer,
//! which draws it with OpenGL into a canvas filling the video area (mpv's render API). mpv opens the same
//! control channel as the program, so the engine drives it as usual. When libmpv can't be loaded, the engine
//! falls back to the mpv program with sound only.

use std::ffi::{CString, c_char, c_int, c_void};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use libloading::Library;
use timeplayer_core::mpv::InProcessPlayer;
use wxdragon::prelude::*;

use crate::App;
use crate::jobs::ReplySender;

const RENDER_PARAM_INVALID: c_int = 0;
const RENDER_PARAM_API_TYPE: c_int = 1;
const RENDER_PARAM_OPENGL_INIT_PARAMS: c_int = 2;
const RENDER_PARAM_OPENGL_FBO: c_int = 3;
const RENDER_PARAM_FLIP_Y: c_int = 4;

#[repr(C)]
struct RenderParam {
    kind: c_int,
    data: *mut c_void,
}

#[repr(C)]
struct OpenGlInitParams {
    get_proc_address: extern "C" fn(*mut c_void, *const c_char) -> *mut c_void,
    get_proc_address_ctx: *mut c_void,
}

#[repr(C)]
struct OpenGlFbo {
    fbo: c_int,
    w: c_int,
    h: c_int,
    internal_format: c_int,
}

/// libmpv's functions TimePlayer uses.
struct Api {
    create: unsafe extern "C" fn() -> *mut c_void,
    set_option_string: unsafe extern "C" fn(*mut c_void, *const c_char, *const c_char) -> c_int,
    initialize: unsafe extern "C" fn(*mut c_void) -> c_int,
    terminate_destroy: unsafe extern "C" fn(*mut c_void),
    render_create: unsafe extern "C" fn(*mut *mut c_void, *mut c_void, *mut RenderParam) -> c_int,
    render_set_update_callback: unsafe extern "C" fn(*mut c_void, Option<extern "C" fn(*mut c_void)>, *mut c_void),
    render_render: unsafe extern "C" fn(*mut c_void, *mut RenderParam) -> c_int,
    render_free: unsafe extern "C" fn(*mut c_void),
    _library: Library,
}

fn library_paths() -> Vec<std::path::PathBuf> {
    let program = timeplayer_core::paths::program_folder();
    vec![
        program.join("../Frameworks/libmpv.2.dylib"),
        "/opt/homebrew/lib/libmpv.2.dylib".into(),
        "/usr/local/lib/libmpv.2.dylib".into(),
    ]
}

fn api() -> Option<&'static Api> {
    static API: OnceLock<Option<Api>> = OnceLock::new();
    API.get_or_init(|| {
        for path in library_paths().into_iter().filter(|p| p.is_file()) {
            // SAFETY: libmpv's own library and functions, with the signatures of mpv's client.h and render.h.
            let loaded = unsafe {
                Library::new(&path).and_then(|library| {
                    Ok(Api {
                        create: *library.get(b"mpv_create\0")?,
                        set_option_string: *library.get(b"mpv_set_option_string\0")?,
                        initialize: *library.get(b"mpv_initialize\0")?,
                        terminate_destroy: *library.get(b"mpv_terminate_destroy\0")?,
                        render_create: *library.get(b"mpv_render_context_create\0")?,
                        render_set_update_callback: *library.get(b"mpv_render_context_set_update_callback\0")?,
                        render_render: *library.get(b"mpv_render_context_render\0")?,
                        render_free: *library.get(b"mpv_render_context_free\0")?,
                        _library: library,
                    })
                })
            };
            match loaded {
                Ok(api) => {
                    log::info!("Using libmpv from {}", path.display());
                    return Some(api);
                }
                Err(e) => log::warn!("libmpv at {} couldn't be loaded: {e}", path.display()),
            }
        }
        log::warn!("No libmpv found; videos play their sound only.");
        None
    })
    .as_ref()
}

/// The mpv handle, kept as a number so it can cross threads; mpv's API is thread-safe.
#[derive(Clone, Copy)]
struct Handle(usize);

/// Frames drawn so far (the self-check reads it).
pub static FRAMES: AtomicU64 = AtomicU64::new(0);
static DRAW_PENDING: AtomicBool = AtomicBool::new(false);
static SENDER: OnceLock<ReplySender> = OnceLock::new();

/// The in-process player the engine starts and stops.
pub struct MacPlayer {
    handle: Mutex<Option<Handle>>,
    sender: ReplySender,
}

impl MacPlayer {
    pub fn new(sender: ReplySender) -> Arc<Self> {
        let _ = SENDER.set(sender.clone());
        Arc::new(Self { handle: Mutex::new(None), sender })
    }
}

impl InProcessPlayer for MacPlayer {
    fn start(&self, options: &[String]) -> bool {
        let Some(api) = api() else { return false };
        // SAFETY: libmpv calls on a handle created here; strings live through each call.
        unsafe {
            let mpv = (api.create)();
            if mpv.is_null() {
                return false;
            }
            for option in options {
                let (name, value) = option.split_once('=').unwrap_or((option.as_str(), "yes"));
                let (Ok(name), Ok(value)) = (CString::new(name), CString::new(value)) else { continue };
                if (api.set_option_string)(mpv, name.as_ptr(), value.as_ptr()) < 0 {
                    log::warn!("libmpv didn't take the option {}", name.to_string_lossy());
                }
            }
            if (api.initialize)(mpv) < 0 {
                (api.terminate_destroy)(mpv);
                return false;
            }
            let handle = Handle(mpv as usize);
            *self.handle.lock().unwrap() = Some(handle);
            // The drawing side lives on the window's thread.
            self.sender.send(move |app: &mut App| app.mac_video_attach(handle.0));
        }
        true
    }

    fn stop(&self) {
        if let Some(handle) = self.handle.lock().unwrap().take() {
            // The render context goes before mpv itself, on the window's thread.
            self.sender.send(move |app: &mut App| app.mac_video_detach(handle.0));
        }
    }
}

extern "C" fn proc_address(_ctx: *mut c_void, name: *const c_char) -> *mut c_void {
    // SAFETY: mpv passes a NUL-terminated function name.
    let name = unsafe { std::ffi::CStr::from_ptr(name) }.to_string_lossy();
    GLContext::proc_address(&name)
}

/// mpv has a new frame (called from mpv's own thread): the window's thread draws it.
extern "C" fn frame_ready(_ctx: *mut c_void) {
    if !DRAW_PENDING.swap(true, Ordering::SeqCst)
        && let Some(sender) = SENDER.get()
    {
        sender.send(|app: &mut App| app.mac_video_draw());
    }
}

/// The canvas and mpv's render context, on the window's thread.
#[derive(Default)]
pub struct MacVideo {
    canvas: Option<GLCanvas>,
    context: Option<GLContext>,
    mpv: usize,
    render: usize,
}

impl MacVideo {
    /// A canvas filling the video area. Call while building the window.
    pub fn build(video: &Panel) -> Self {
        let canvas = GLCanvas::builder(video).build();
        // Nothing to do in it with the keyboard; Tab passes over it.
        canvas.disable_focus_from_keyboard();
        let sizer = BoxSizer::builder(Orientation::Vertical).build();
        sizer.add(&canvas, 1, SizerFlag::Expand, 0);
        video.set_sizer(sizer, true);
        canvas.on_size(|event| {
            crate::with_app(App::mac_video_draw);
            event.skip(true);
        });
        Self { context: GLContext::new(&canvas), canvas: Some(canvas), ..Default::default() }
    }
}

impl App {
    fn mac_video_attach(&mut self, mpv: usize) {
        self.mac_video.mpv = mpv;
        self.mac_video_ensure_render();
    }

    /// mpv's render context, made once the canvas can take OpenGL calls (it shows with the player).
    pub(crate) fn mac_video_ensure_render(&mut self) {
        let video = &mut self.mac_video;
        if video.mpv == 0 || video.render != 0 {
            return;
        }
        let (Some(api), Some(canvas), Some(context)) = (api(), video.canvas.as_ref(), video.context.as_ref()) else { return };
        if !canvas.set_current(context) {
            return;
        }
        let api_type = c"opengl";
        let mut init = OpenGlInitParams { get_proc_address: proc_address, get_proc_address_ctx: std::ptr::null_mut() };
        let mut params = [
            RenderParam { kind: RENDER_PARAM_API_TYPE, data: api_type.as_ptr().cast_mut().cast() },
            RenderParam { kind: RENDER_PARAM_OPENGL_INIT_PARAMS, data: (&raw mut init).cast() },
            RenderParam { kind: RENDER_PARAM_INVALID, data: std::ptr::null_mut() },
        ];
        let mut render: *mut c_void = std::ptr::null_mut();
        // SAFETY: render.h's documented parameters, with the GL context current on this thread.
        let created = unsafe { (api.render_create)(&mut render, video.mpv as *mut c_void, params.as_mut_ptr()) };
        if created < 0 || render.is_null() {
            log::warn!("mpv's OpenGL rendering couldn't start ({created}); videos play their sound only.");
            return;
        }
        // SAFETY: the callback only posts to the window's thread.
        unsafe { (api.render_set_update_callback)(render, Some(frame_ready), std::ptr::null_mut()) };
        video.render = render as usize;
        log::info!("Video draws inside TimePlayer's window through libmpv.");
    }

    /// Draws mpv's current frame into the canvas.
    pub(crate) fn mac_video_draw(&mut self) {
        DRAW_PENDING.store(false, Ordering::SeqCst);
        self.mac_video_ensure_render();
        let video = &self.mac_video;
        let (Some(api), Some(canvas), Some(context)) = (api(), video.canvas.as_ref(), video.context.as_ref()) else { return };
        if video.render == 0 || !canvas.is_shown() || !canvas.set_current(context) {
            return;
        }
        let size = canvas.pixel_size();
        let mut fbo = OpenGlFbo { fbo: 0, w: size.width.max(1), h: size.height.max(1), internal_format: 0 };
        let mut flip: c_int = 1;
        let mut params = [
            RenderParam { kind: RENDER_PARAM_OPENGL_FBO, data: (&raw mut fbo).cast() },
            RenderParam { kind: RENDER_PARAM_FLIP_Y, data: (&raw mut flip).cast() },
            RenderParam { kind: RENDER_PARAM_INVALID, data: std::ptr::null_mut() },
        ];
        // SAFETY: render.h's documented parameters, with the GL context current on this thread.
        unsafe { (api.render_render)(video.render as *mut c_void, params.as_mut_ptr()) };
        canvas.swap_buffers();
        FRAMES.fetch_add(1, Ordering::Relaxed);
    }

    fn mac_video_detach(&mut self, mpv: usize) {
        let Some(api) = api() else { return };
        let video = &mut self.mac_video;
        // SAFETY: the render context is freed before the mpv it belongs to, both on this thread.
        unsafe {
            if video.render != 0 {
                if let (Some(canvas), Some(context)) = (video.canvas.as_ref(), video.context.as_ref()) {
                    canvas.set_current(context);
                }
                (api.render_free)(video.render as *mut c_void);
                video.render = 0;
            }
            (api.terminate_destroy)(mpv as *mut c_void);
        }
        if video.mpv == mpv {
            video.mpv = 0;
        }
    }
}
