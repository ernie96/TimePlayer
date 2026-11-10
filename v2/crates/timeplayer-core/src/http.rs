//! HTTP for every source, blocking, on TimePlayer's own threads. One `Http` is shared; tests swap in a
//! fake transport. No cookies are kept between requests: the YouTube sign-in is only ever added by the
//! code that needs it, and only for youtube.com.

use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

use crate::error::{AppError, Result};

pub const BROWSER_USER_AGENT: &str =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

#[derive(Debug, Clone, PartialEq)]
pub struct Request {
    pub method: &'static str,
    pub url: String,
    pub headers: Vec<(String, String)>,
    pub body: Option<Vec<u8>>,
    pub timeout: Duration,
    /// False reads a redirect instead of following it.
    pub follow_redirects: bool,
    /// Only the status and headers: the body (which may be a whole video) is never read.
    pub headers_only: bool,
}

impl Request {
    pub fn get(url: impl Into<String>) -> Self {
        Self {
            method: "GET",
            url: url.into(),
            headers: Vec::new(),
            body: None,
            timeout: Duration::from_secs(15),
            follow_redirects: true,
            headers_only: false,
        }
    }

    pub fn post_json(url: impl Into<String>, body: &serde_json::Value) -> Self {
        Self {
            method: "POST",
            url: url.into(),
            headers: vec![("Content-Type".into(), "application/json".into())],
            body: Some(body.to_string().into_bytes()),
            timeout: Duration::from_secs(15),
            follow_redirects: true,
            headers_only: false,
        }
    }

    pub fn header(mut self, name: &str, value: &str) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }

    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    /// Reads where a redirect points instead of following it, without reading any body.
    pub fn redirect_only(mut self) -> Self {
        self.follow_redirects = false;
        self.headers_only = true;
        self
    }

    pub fn body_text(&self) -> String {
        self.body.as_ref().map(|b| String::from_utf8_lossy(b).into_owned()).unwrap_or_default()
    }
}

#[derive(Debug, Clone, Default)]
pub struct Response {
    pub status: u16,
    pub headers: HashMap<String, String>,
    pub body: Vec<u8>,
}

impl Response {
    pub fn ok(&self) -> bool {
        (200..300).contains(&self.status)
    }

    pub fn text(&self) -> String {
        String::from_utf8_lossy(&self.body).into_owned()
    }

    pub fn json(&self) -> Result<serde_json::Value> {
        serde_json::from_slice(&self.body).map_err(|e| AppError::new("BAD_JSON", e.to_string()))
    }
}

/// Sends a request. The real one is reqwest; tests use closures.
pub trait Transport: Send + Sync {
    fn send(&self, request: Request) -> Result<Response>;
}

impl<F: Fn(Request) -> Result<Response> + Send + Sync> Transport for F {
    fn send(&self, request: Request) -> Result<Response> {
        self(request)
    }
}

struct Reqwest {
    client: reqwest::blocking::Client,
    no_redirects: reqwest::blocking::Client,
}

impl Transport for Reqwest {
    fn send(&self, request: Request) -> Result<Response> {
        let method = reqwest::Method::from_bytes(request.method.as_bytes()).unwrap_or(reqwest::Method::GET);
        let client = if request.follow_redirects { &self.client } else { &self.no_redirects };
        let mut builder = client.request(method, &request.url).timeout(request.timeout);
        if !request.headers.iter().any(|(n, _)| n.eq_ignore_ascii_case("user-agent")) {
            builder = builder.header("User-Agent", BROWSER_USER_AGENT);
        }
        for (name, value) in &request.headers {
            builder = builder.header(name, value);
        }
        if let Some(body) = request.body {
            builder = builder.body(body);
        }
        let response = builder.send().map_err(|e| {
            if e.is_timeout() { AppError::new("TIMEOUT", "The server took too long to answer.") } else { AppError::network(e.to_string()) }
        })?;
        let status = response.status().as_u16();
        let headers = response
            .headers()
            .iter()
            .filter_map(|(k, v)| Some((k.as_str().to_lowercase(), v.to_str().ok()?.to_owned())))
            .collect();
        let body = if request.headers_only { Vec::new() } else { response.bytes().map_err(|e| AppError::network(e.to_string()))?.to_vec() };
        Ok(Response { status, headers, body })
    }
}

/// The shared HTTP client.
#[derive(Clone)]
pub struct Http {
    transport: Arc<dyn Transport>,
}

impl Http {
    /// The real network: rustls with ring, redirects followed, no cookie store.
    pub fn new() -> Self {
        let _ = rustls::crypto::ring::default_provider().install_default();
        let build = |policy| {
            reqwest::blocking::Client::builder().redirect(policy).connect_timeout(Duration::from_secs(10)).build().expect("the HTTP client starts")
        };
        let client = build(reqwest::redirect::Policy::limited(10));
        let no_redirects = build(reqwest::redirect::Policy::none());
        Self { transport: Arc::new(Reqwest { client, no_redirects }) }
    }

    pub fn with_transport(transport: impl Transport + 'static) -> Self {
        Self { transport: Arc::new(transport) }
    }

    pub fn send(&self, request: Request) -> Result<Response> {
        self.transport.send(request)
    }

    /// GET, failing on a non-2xx status.
    pub fn get_ok(&self, request: Request) -> Result<Response> {
        let url = crate::error::redact_url(&request.url);
        let response = self.send(request)?;
        if response.ok() {
            Ok(response)
        } else if response.status == 429 {
            Err(AppError::rate_limited(&url, None))
        } else {
            Err(AppError::network(format!("{url} answered {}.", response.status)))
        }
    }

    pub fn get_json(&self, url: &str, timeout: Duration) -> Result<serde_json::Value> {
        self.get_ok(Request::get(url).timeout(timeout))?.json()
    }

    pub fn get_text(&self, url: &str, timeout: Duration) -> Result<String> {
        Ok(self.get_ok(Request::get(url).timeout(timeout))?.text())
    }

    /// Downloads a file to `path` (through a temporary file, so a broken download leaves nothing).
    pub fn download(&self, url: &str, path: &std::path::Path, timeout: Duration) -> Result<()> {
        let response = self.get_ok(Request::get(url).timeout(timeout))?;
        crate::paths::write_atomic(path, &response.body)?;
        Ok(())
    }
}

impl Default for Http {
    fn default() -> Self {
        Self::new()
    }
}

/// Lets a long job (an import, a whole-list download) be stopped from another thread.
#[derive(Clone, Default, Debug)]
pub struct Cancel(Arc<AtomicBool>);

impl Cancel {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn cancel(&self) {
        self.0.store(true, Ordering::SeqCst);
    }

    pub fn is_canceled(&self) -> bool {
        self.0.load(Ordering::SeqCst)
    }

    pub fn check(&self) -> Result<()> {
        if self.is_canceled() { Err(AppError::canceled()) } else { Ok(()) }
    }
}

/// `%` escaping for query strings.
pub fn escape(text: &str) -> String {
    percent_encoding::utf8_percent_encode(text, percent_encoding::NON_ALPHANUMERIC).to_string()
}

pub fn unescape(text: &str) -> String {
    percent_encoding::percent_decode_str(text).decode_utf8_lossy().into_owned()
}
