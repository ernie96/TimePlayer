//! Chapters, read the way YouTube does from the timestamps in a description ("0:00 Intro",
//! "1:02:03 - Part two", "Intro (0:00)"): starting at 0:00, at least three, in order (1.x's `Chapters`).

use regex::Regex;
use std::sync::OnceLock;

use crate::t;
use crate::text::time_spoken_secs;

#[derive(Debug, Clone, PartialEq)]
pub struct Chapter {
    pub number: usize,
    pub start: f64,
    pub title: String,
}

impl Chapter {
    /// "3. Setting up, 4 minutes, 10 seconds" for lists.
    pub fn label(&self) -> String {
        t!("{0}. {1}, {2}", self.number, self.title, time_spoken_secs(self.start))
    }
}

const STAMP: &str = r"(?P<time>(?:\d{1,2}:)?\d{1,2}:\d{2})";

fn time_first() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(&format!(r"^\s*[\[(]?{STAMP}[\])]?\s*(?:[-–—:|.•]\s*)?(?P<title>.+?)\s*$")).unwrap())
}

fn title_first() -> &'static Regex {
    static RE: OnceLock<Regex> = OnceLock::new();
    RE.get_or_init(|| Regex::new(&format!(r"^\s*(?P<title>.+?)\s*(?:[-–—:|]\s*)?[\[(]?{STAMP}[\])]?\s*$")).unwrap())
}

fn has_letter(text: &str) -> bool {
    text.chars().any(char::is_alphabetic)
}

fn parse_time(text: &str) -> Option<f64> {
    let numbers: Vec<u64> = text.split(':').map(|p| p.parse().ok()).collect::<Option<_>>()?;
    if *numbers.last()? >= 60 || (numbers.len() == 3 && numbers[1] >= 60) {
        return None;
    }
    Some(match numbers.as_slice() {
        [h, m, s] => (h * 3600 + m * 60 + s) as f64,
        [m, s] => (m * 60 + s) as f64,
        _ => return None,
    })
}

/// The chapters in a description; empty when it doesn't have proper ones. `duration` 0 means unknown.
pub fn from_description(description: &str, duration: f64) -> Vec<Chapter> {
    let mut found: Vec<(f64, String)> = Vec::new();
    for raw in description.split('\n') {
        let line = raw.trim();
        if line.is_empty() || line.chars().count() > 200 {
            continue;
        }
        let caps = time_first()
            .captures(line)
            .filter(|c| has_letter(&c["title"]))
            .or_else(|| title_first().captures(line));
        let Some(caps) = caps else { continue };
        let Some(start) = parse_time(&caps["time"]) else { continue };
        let title = caps["title"].trim_matches(|c: char| " -–—:|•".contains(c)).to_owned();
        if !has_letter(&title) {
            continue;
        }
        if let Some(last) = found.last()
            && start <= last.0 {
                // A second list (say "Songs: 0:00 ...") starts over; keep the first.
                if start == 0.0 {
                    break;
                }
                continue;
            }
        if duration > 0.0 && start >= duration {
            break;
        }
        found.push((start, title));
    }
    if found.len() < 3 || found[0].0 != 0.0 {
        return Vec::new();
    }
    found.into_iter().enumerate().map(|(i, (start, title))| Chapter { number: i + 1, start, title }).collect()
}

/// The chapter playing at `position`.
pub fn at(chapters: &[Chapter], position: f64) -> Option<&Chapter> {
    chapters.iter().rev().find(|c| c.start <= position + 0.25)
}

/// Where "previous chapter" goes: the start of this one, or the one before if this just began.
pub fn previous(chapters: &[Chapter], position: f64) -> Option<&Chapter> {
    let current = at(chapters, position)?;
    if position - current.start > 3.0 {
        return Some(current);
    }
    Some(if current.number >= 2 { &chapters[current.number - 2] } else { current })
}

pub fn next(chapters: &[Chapter], position: f64) -> Option<&Chapter> {
    chapters.iter().find(|c| c.start > position + 0.25)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reads_youtube_style_chapters() {
        let d = "Great video\n0:00 Intro\n1:30 - Setting up\nThe end (12:05)\nLinks: 1:00";
        let c = from_description(d, 0.0);
        assert_eq!(c.len(), 3);
        assert_eq!(c[1].title, "Setting up");
        assert_eq!(c[2].start, 725.0);
        assert_eq!(next(&c, 100.0).unwrap().number, 3);
        assert_eq!(previous(&c, 91.0).unwrap().number, 1);
        assert_eq!(previous(&c, 200.0).unwrap().number, 2);
    }

    #[test]
    fn needs_three_from_zero() {
        assert!(from_description("0:30 a\n1:00 b\n2:00 c", 0.0).is_empty());
        assert!(from_description("0:00 a\n1:00 b", 0.0).is_empty());
    }
}
