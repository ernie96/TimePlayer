//! TimePlayer's core: YouTube sources, playback logic, the library, settings and translations, with no
//! user interface. Ported from TimePlayer 1's `TimePlayer.Core` (C#).

pub mod account;
pub mod audio;
pub mod braille;
pub mod chapters;
pub mod community;
pub mod config;
pub mod discovery;
pub mod downloads;
pub mod error;
pub mod http;
pub mod i18n;
pub mod imports;
pub mod innertube;
pub mod invidious;
pub mod library;
pub mod links;
pub mod logging;
pub mod models;
pub mod mpv;
pub mod paths;
pub mod playback;
pub mod process;
pub mod text;
pub mod tuberepair;
pub mod updates;
pub mod xml;
pub mod ytdlp;

pub use error::{AppError, Result};
