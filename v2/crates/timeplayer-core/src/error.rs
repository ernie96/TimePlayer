//! Errors with a code the app can act on, a message a person can read, and details for the log
//! (1.x's `AppError`).

use std::fmt;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppError {
    /// "AGE_RESTRICTED", "NETWORK_ERROR" and so on, as in 1.x.
    pub code: String,
    /// What the user hears.
    pub message: String,
    /// What the log gets.
    pub details: Option<String>,
    pub recoverable: bool,
}

pub type Result<T> = std::result::Result<T, AppError>;

impl AppError {
    pub fn new(code: &str, message: impl Into<String>) -> Self {
        Self { code: code.to_owned(), message: message.into(), details: None, recoverable: true }
    }

    pub fn with_details(mut self, details: impl Into<String>) -> Self {
        self.details = Some(details.into());
        self
    }

    pub fn fatal(mut self) -> Self {
        self.recoverable = false;
        self
    }

    pub fn invalid_url(details: &str) -> Self {
        Self::new("INVALID_YOUTUBE_URL", "The provided text is not a valid YouTube URL or video ID.").with_details(details)
    }

    pub fn video_not_found(video_id: &str) -> Self {
        Self::new("VIDEO_NOT_FOUND", format!("Video '{video_id}' could not be found or has been removed."))
            .with_details(format!("Video ID: {video_id}"))
            .fatal()
    }

    pub fn stream_unavailable(video_id: &str, reason: Option<&str>) -> Self {
        Self::new("STREAM_UNAVAILABLE", "Unable to extract a playable media stream for this video.")
            .with_details(reason.map(str::to_owned).unwrap_or_else(|| format!("Video ID: {video_id}")))
    }

    pub fn age_restricted(video_id: &str, details: Option<&str>) -> Self {
        Self::new("AGE_RESTRICTED", "This video is age-restricted and requires sign-in or session cookies to access.")
            .with_details(details.map(str::to_owned).unwrap_or_else(|| format!("Video ID: {video_id}")))
            .fatal()
    }

    pub fn private_video(video_id: &str, details: Option<&str>) -> Self {
        Self::new("PRIVATE_VIDEO", "This video is private, members-only, or requires special permissions to view.")
            .with_details(details.map(str::to_owned).unwrap_or_else(|| format!("Video ID: {video_id}")))
            .fatal()
    }

    pub fn cookies_required(video_id: &str, details: Option<&str>) -> Self {
        Self::new("COOKIES_REQUIRED", "YouTube requires browser cookies or authentication to access this content.")
            .with_details(details.map(str::to_owned).unwrap_or_else(|| format!("Video ID: {video_id}")))
    }

    pub fn rate_limited(provider: &str, details: Option<&str>) -> Self {
        let mut e = Self::new(
            "RATE_LIMITED",
            format!("The service ({provider}) is currently rate-limiting requests. Please try again shortly."),
        );
        e.details = details.map(str::to_owned);
        e
    }

    pub fn network(message: impl Into<String>) -> Self {
        Self::new("NETWORK_ERROR", message)
    }

    pub fn dependency_not_found(name: &str, details: &str) -> Self {
        Self::new("DEPENDENCY_NOT_FOUND", format!("The required application dependency '{name}' was not found: {details}"))
            .with_details(details)
            .fatal()
    }

    pub fn playback(message: impl Into<String>) -> Self {
        Self::new("PLAYBACK_ENGINE_ERROR", message)
    }

    pub fn canceled() -> Self {
        Self::new("CANCELED", "Canceled.")
    }

    pub fn is_canceled(&self) -> bool {
        self.code == "CANCELED"
    }
}

impl fmt::Display for AppError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for AppError {}

impl From<std::io::Error> for AppError {
    fn from(e: std::io::Error) -> Self {
        Self::new("IO_ERROR", e.to_string())
    }
}

impl From<serde_json::Error> for AppError {
    fn from(e: serde_json::Error) -> Self {
        Self::new("JSON_ERROR", e.to_string())
    }
}

/// Keeps personal details out of logs, which users share when reporting problems: a stream address
/// carries the viewer's IP address and signed tokens in its query ("https://host/path?…").
pub fn redact_url(url: &str) -> String {
    match url.find('?') {
        Some(q) => format!("{}?…", &url[..q]),
        None => url.to_owned(),
    }
}
