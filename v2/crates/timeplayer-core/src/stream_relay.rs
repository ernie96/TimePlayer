//! A relay between mpv and googlevideo for the addresses YouTube's apps get. mpv asks for "everything from
//! here on", and googlevideo sends those addresses at barely the video's own bitrate that way, so after a seek
//! mpv waits and waits. Asked for in pieces of a few megabytes, the same address comes at full speed. So mpv
//! reads from this relay on 127.0.0.1, and the relay fetches from googlevideo in pieces, one piece ahead
//! (as lib_YTPYP's `fetch_range` does for foo_ytpyp).

use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::mpsc::sync_channel;
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};

use crate::http::{Http, Request};

/// The first piece is small so playing and seeking start at once; later ones are larger, which googlevideo
/// still sends at full speed (measured: 4 MB in about 0.3 seconds, against 5.8 Mbit/s for an open request).
const FIRST_PIECE: u64 = 1 << 20;
const PIECE: u64 = 4 << 20;
const PIECE_TIMEOUT: Duration = Duration::from_secs(20);
const PIECE_TRIES: usize = 3;
/// Addresses kept for mpv; older ones are forgotten (a video plays from at most two at a time).
const MAX_STREAMS: usize = 32;
const MAX_REQUEST_HEAD: usize = 16 * 1024;
const USER_AGENT: &str = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

struct Relay {
    port: u16,
    http: Http,
    streams: Mutex<HashMap<String, (String, Instant)>>,
}

static RELAY: OnceLock<Option<Arc<Relay>>> = OnceLock::new();

/// The local address mpv should open instead of `upstream`, or None when the relay couldn't start (then mpv
/// gets the address as it is).
pub fn local_url(http: &Http, upstream: &str) -> Option<String> {
    let relay = RELAY.get_or_init(|| start(http.clone())).as_ref()?;
    let token = new_token();
    let mut streams = relay.streams.lock().unwrap();
    if streams.len() >= MAX_STREAMS
        && let Some(oldest) = streams.iter().min_by_key(|(_, (_, at))| *at).map(|(k, _)| k.clone())
    {
        streams.remove(&oldest);
    }
    streams.insert(token.clone(), (upstream.to_owned(), Instant::now()));
    Some(format!("http://127.0.0.1:{}/s/{token}", relay.port))
}

fn start(http: Http) -> Option<Arc<Relay>> {
    let listener = match TcpListener::bind(("127.0.0.1", 0)) {
        Ok(l) => l,
        Err(e) => {
            log::warn!("The stream relay could not start ({e}); mpv reads YouTube directly.");
            return None;
        }
    };
    let port = listener.local_addr().ok()?.port();
    let relay = Arc::new(Relay { port, http, streams: Mutex::new(HashMap::new()) });
    let serving = relay.clone();
    std::thread::Builder::new()
        .name("stream relay".into())
        .spawn(move || {
            for connection in listener.incoming().flatten() {
                let relay = serving.clone();
                let _ = std::thread::Builder::new().name("stream relay connection".into()).spawn(move || {
                    if let Err(e) = serve(&relay, connection) {
                        log::debug!("Stream relay connection ended: {e}");
                    }
                });
            }
        })
        .ok()?;
    log::info!("Stream relay listening on 127.0.0.1:{port}.");
    Some(relay)
}

/// 32 characters nobody can guess, so no other program on the computer can use the relay.
fn new_token() -> String {
    const ALPHABET: &[u8] = b"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    (0..32).map(|_| ALPHABET[(crate::discovery::random_unit() * ALPHABET.len() as f64) as usize % ALPHABET.len()] as char).collect()
}

/// What mpv asked for.
#[derive(Debug, PartialEq)]
struct Ask {
    head_only: bool,
    token: String,
    /// The first byte and, when given, the last.
    range: Option<(u64, Option<u64>)>,
}

fn parse_request(lines: &[String]) -> Option<Ask> {
    let mut first = lines.first()?.split_whitespace();
    let method = first.next()?;
    let token = first.next()?.strip_prefix("/s/")?.to_owned();
    if method != "GET" && method != "HEAD" {
        return None;
    }
    let range = lines.iter().skip(1).find_map(|l| {
        let (name, value) = l.split_once(':')?;
        if !name.trim().eq_ignore_ascii_case("range") {
            return None;
        }
        let (start, end) = value.trim().strip_prefix("bytes=")?.split_once('-')?;
        Some((start.trim().parse().ok()?, end.trim().parse().ok()))
    });
    Some(Ask { head_only: method == "HEAD", token, range })
}

fn read_head(stream: &TcpStream) -> std::io::Result<Vec<String>> {
    let mut reader = BufReader::new(stream);
    let mut lines = Vec::new();
    let mut total = 0;
    loop {
        let mut line = String::new();
        let n = reader.read_line(&mut line)?;
        total += n;
        if n == 0 || total > MAX_REQUEST_HEAD {
            break;
        }
        let line = line.trim_end().to_owned();
        if line.is_empty() {
            break;
        }
        lines.push(line);
    }
    Ok(lines)
}

/// One piece from googlevideo: its bytes, the whole file's size, and the content type.
struct Piece {
    bytes: Vec<u8>,
    total: Option<u64>,
    content_type: Option<String>,
}

enum PieceError {
    /// The address expired or was refused (mpv then fails, and playback finds a fresh one).
    Refused(u16),
    /// Past the end of the file.
    OutOfRange,
    Failed(String),
}

fn header<'a>(headers: &'a HashMap<String, String>, name: &str) -> Option<&'a str> {
    headers.iter().find(|(k, _)| k.eq_ignore_ascii_case(name)).map(|(_, v)| v.as_str())
}

/// "bytes 0-1023/80912345" gives 80912345.
fn total_from_content_range(value: &str) -> Option<u64> {
    value.rsplit('/').next()?.trim().parse().ok()
}

fn fetch_piece(http: &Http, url: &str, start: u64, end: u64) -> Result<Piece, PieceError> {
    let mut last = String::new();
    for _ in 0..PIECE_TRIES {
        let request = Request::get(url).header("Range", &format!("bytes={start}-{end}")).header("User-Agent", USER_AGENT).timeout(PIECE_TIMEOUT);
        match http.send(request) {
            Ok(r) if r.status == 206 || r.status == 200 && start == 0 => {
                let total = header(&r.headers, "content-range").and_then(total_from_content_range).or_else(|| (r.status == 200).then_some(r.body.len() as u64));
                // A 200 is the whole file; keep only the piece asked for.
                let bytes = if r.status == 200 { r.body.into_iter().skip(start as usize).take((end - start + 1) as usize).collect() } else { r.body };
                if bytes.is_empty() {
                    last = "an empty piece".into();
                    continue;
                }
                return Ok(Piece { bytes, total, content_type: header(&r.headers, "content-type").map(str::to_owned) });
            }
            Ok(r) if r.status == 416 => return Err(PieceError::OutOfRange),
            Ok(r) if matches!(r.status, 401 | 403 | 404 | 410) => return Err(PieceError::Refused(r.status)),
            Ok(r) => last = format!("HTTP {}", r.status),
            Err(e) => last = e.to_string(),
        }
    }
    Err(PieceError::Failed(last))
}

fn serve(relay: &Relay, mut stream: TcpStream) -> std::io::Result<()> {
    let lines = read_head(&stream)?;
    let Some(ask) = parse_request(&lines) else {
        return stream.write_all(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    };
    let Some(url) = relay.streams.lock().unwrap().get(&ask.token).map(|(u, _)| u.clone()) else {
        return stream.write_all(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    };
    let start = ask.range.map(|(s, _)| s).unwrap_or(0);
    let first = match fetch_piece(&relay.http, &url, start, start + FIRST_PIECE - 1) {
        Ok(p) => p,
        Err(PieceError::OutOfRange) => return stream.write_all(b"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
        Err(PieceError::Refused(status)) => {
            log::warn!("googlevideo refused a stream address (HTTP {status}); it has probably expired.");
            return stream.write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }
        Err(PieceError::Failed(why)) => {
            log::warn!("The stream relay could not reach googlevideo: {why}");
            return stream.write_all(b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        }
    };
    let Some(total) = first.total else {
        return stream.write_all(b"HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    };
    let end = ask.range.and_then(|(_, e)| e).unwrap_or(total - 1).min(total - 1);
    let length = end + 1 - start;
    let mut head = if ask.range.is_some() {
        format!("HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{end}/{total}\r\n")
    } else {
        "HTTP/1.1 200 OK\r\n".to_owned()
    };
    head += &format!("Content-Length: {length}\r\nAccept-Ranges: bytes\r\nConnection: close\r\n");
    if let Some(kind) = &first.content_type {
        head += &format!("Content-Type: {kind}\r\n");
    }
    head += "\r\n";
    stream.write_all(head.as_bytes())?;
    if ask.head_only {
        return Ok(());
    }
    let first_bytes = &first.bytes[..first.bytes.len().min(length as usize)];
    stream.write_all(first_bytes)?;
    let mut position = start + first_bytes.len() as u64;
    if position > end {
        return Ok(());
    }

    // The next piece is fetched while mpv takes this one; when mpv stops reading (a seek, or the video
    // ended), the write fails, the receiver goes, and the fetching stops.
    let (sender, receiver) = sync_channel::<Result<Vec<u8>, String>>(1);
    let http = relay.http.clone();
    std::thread::spawn(move || {
        let mut at = position;
        while at <= end {
            let piece_end = (at + PIECE - 1).min(end);
            let result = match fetch_piece(&http, &url, at, piece_end) {
                Ok(p) => Ok(p.bytes),
                Err(PieceError::Refused(status)) => Err(format!("refused, HTTP {status}")),
                Err(PieceError::OutOfRange) => Err("past the end".into()),
                Err(PieceError::Failed(why)) => Err(why),
            };
            let stop = result.is_err();
            if let Ok(bytes) = &result {
                at += bytes.len() as u64;
            }
            if sender.send(result).is_err() || stop {
                return;
            }
        }
    });
    for piece in receiver {
        match piece {
            Ok(bytes) => {
                let bytes = &bytes[..bytes.len().min((end + 1 - position) as usize)];
                stream.write_all(bytes)?;
                position += bytes.len() as u64;
            }
            Err(why) => {
                // mpv sees the connection end early and asks again from where it got to.
                log::warn!("The stream relay stopped at byte {position}: {why}");
                return Ok(());
            }
        }
    }
    stream.flush()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::http::Response;
    use std::io::Read;

    #[test]
    fn reads_mpv_requests() {
        let ask = |lines: &[&str]| parse_request(&lines.iter().map(|l| l.to_string()).collect::<Vec<_>>());
        assert_eq!(ask(&["GET /s/abc HTTP/1.1", "Host: x", "Range: bytes=100-"]), Some(Ask { head_only: false, token: "abc".into(), range: Some((100, None)) }));
        assert_eq!(ask(&["HEAD /s/abc HTTP/1.1", "range: bytes=5-9"]).unwrap().range, Some((5, Some(9))));
        assert_eq!(ask(&["GET /s/abc HTTP/1.1"]).unwrap().range, None);
        assert_eq!(ask(&["GET /other HTTP/1.1"]), None);
        assert_eq!(ask(&["POST /s/abc HTTP/1.1"]), None);
        assert_eq!(total_from_content_range("bytes 0-1023/80912345"), Some(80_912_345));
    }

    /// A fake googlevideo serving `size` bytes (byte i is i % 251), answering only bounded ranges.
    fn fake_googlevideo(size: u64, asked: Arc<Mutex<Vec<(u64, u64)>>>) -> Http {
        Http::with_transport(move |r: Request| {
            let range = r.headers.iter().find(|(k, _)| k == "Range").map(|(_, v)| v.clone()).unwrap();
            let (a, b) = range.strip_prefix("bytes=").unwrap().split_once('-').unwrap();
            let (a, b): (u64, u64) = (a.parse().unwrap(), b.parse().unwrap());
            asked.lock().unwrap().push((a, b));
            if a >= size {
                return Ok(Response { status: 416, ..Default::default() });
            }
            let b = b.min(size - 1);
            let mut headers = HashMap::new();
            headers.insert("content-range".to_owned(), format!("bytes {a}-{b}/{size}"));
            headers.insert("content-type".to_owned(), "video/mp4".to_owned());
            Ok(Response { status: 206, headers, body: (a..=b).map(|i| (i % 251) as u8).collect() })
        })
    }

    fn get(url: &str, range: Option<&str>) -> (String, Vec<u8>) {
        let rest = url.strip_prefix("http://").unwrap();
        let (host, path) = rest.split_once('/').unwrap();
        let mut stream = TcpStream::connect(host).unwrap();
        let range = range.map(|r| format!("Range: bytes={r}\r\n")).unwrap_or_default();
        write!(stream, "GET /{path} HTTP/1.1\r\nHost: {host}\r\n{range}\r\n").unwrap();
        let mut answer = Vec::new();
        stream.read_to_end(&mut answer).unwrap();
        let split = answer.windows(4).position(|w| w == b"\r\n\r\n").unwrap();
        (String::from_utf8_lossy(&answer[..split]).into_owned(), answer[split + 4..].to_vec())
    }

    #[test]
    fn serves_any_range_in_bounded_pieces() {
        let size = 9 * 1024 * 1024 + 123;
        let asked = Arc::new(Mutex::new(Vec::new()));
        let relay = start(fake_googlevideo(size, asked.clone())).unwrap();
        let token = "testtoken".to_owned();
        relay.streams.lock().unwrap().insert(token.clone(), ("https://r1.googlevideo.com/videoplayback?x".into(), Instant::now()));
        let url = format!("http://127.0.0.1:{}/s/{token}", relay.port);

        let (head, body) = get(&url, Some("5000000-"));
        assert!(head.starts_with("HTTP/1.1 206"), "{head}");
        assert!(head.contains(&format!("Content-Range: bytes 5000000-{}/{size}", size - 1)), "{head}");
        assert_eq!(body.len() as u64, size - 5_000_000);
        assert!(body.iter().enumerate().all(|(i, b)| *b == ((5_000_000 + i as u64) % 251) as u8));
        assert!(asked.lock().unwrap().iter().all(|(a, b)| b - a < PIECE), "every request to googlevideo is bounded");

        let (head, body) = get(&url, None);
        assert!(head.starts_with("HTTP/1.1 200") && head.contains(&format!("Content-Length: {size}")), "{head}");
        assert_eq!(body.len() as u64, size);

        let (head, body) = get(&url, Some("10-19"));
        assert!(head.contains("Content-Range: bytes 10-19/"), "{head}");
        assert_eq!(body, (10..20).map(|i| (i % 251) as u8).collect::<Vec<_>>());

        assert!(get(&url, Some(&format!("{size}-"))).0.starts_with("HTTP/1.1 416"));
        assert!(get(&format!("http://127.0.0.1:{}/s/unknown", relay.port), None).0.starts_with("HTTP/1.1 404"));
    }

    /// Against YouTube with a real mpv: `MPV=path\to\mpv.exe cargo test -p timeplayer-core live_relay -- --ignored
    /// --nocapture`. Starts a 1080p video in its middle, through the relay and directly, and says how long each
    /// took to play 3 seconds.
    #[test]
    #[ignore]
    fn live_relay_seeks_quickly_in_mpv() {
        let mpv = std::env::var("MPV").expect("set MPV to mpv's path");
        let http = Http::new();
        let state = crate::innertube::player::AppPlayerState::default();
        let info = crate::innertube::player::player_info(&http, &state, "dQw4w9WgXcQ").unwrap();
        let stream = crate::innertube::player::choose_streams(&info, &Default::default()).unwrap();
        let run = |video: &str, audio: &str| {
            let started = Instant::now();
            let status = std::process::Command::new(&mpv)
                .args(["--no-config", "--vo=null", "--ao=null", "--start=50%", "--length=3", "--really-quiet", &format!("--audio-file={audio}"), video])
                .status()
                .unwrap();
            assert!(status.success());
            started.elapsed()
        };
        let video = local_url(&http, &stream.url).unwrap();
        let audio = local_url(&http, stream.audio_url.as_deref().unwrap()).unwrap();
        let relayed = run(&video, &audio);
        let direct = run(&stream.url, stream.audio_url.as_deref().unwrap());
        println!("Start at 50% and play 3 seconds: through the relay {relayed:.1?}, directly {direct:.1?}");
        assert!(relayed < Duration::from_secs(10), "{relayed:?}");
    }

    #[test]
    fn an_expired_address_is_refused_so_playback_finds_a_fresh_one() {
        let relay = start(Http::with_transport(|_r: Request| Ok(Response { status: 403, ..Default::default() }))).unwrap();
        relay.streams.lock().unwrap().insert("t".into(), ("https://r1.googlevideo.com/videoplayback".into(), Instant::now()));
        assert!(get(&format!("http://127.0.0.1:{}/s/t", relay.port), None).0.starts_with("HTTP/1.1 403"));
    }
}
