//! Words for screen readers instead of shorthand like "03:45", which some voices read as a date or a
//! ratio (1.x's `AccessibleTextFormatter`). Everything is said in the user's language.

use crate::i18n::{current, plural};
use crate::t;
use chrono::{DateTime, Utc};
use regex::Regex;
use std::sync::OnceLock;
use std::time::Duration;

/// "3 minutes, 12 seconds"; zero parts are left out.
pub fn time_spoken(time: Duration) -> String {
    let total = time.as_secs();
    let hours = total / 3600;
    let minutes = (total % 3600) / 60;
    let seconds = total % 60;
    let mut parts = Vec::new();
    if hours > 0 {
        parts.push(plural(hours as i64, "{0} hour", "{0} hours"));
    }
    if minutes > 0 {
        parts.push(plural(minutes as i64, "{0} minute", "{0} minutes"));
    }
    if seconds > 0 || parts.is_empty() {
        parts.push(plural(seconds as i64, "{0} second", "{0} seconds"));
    }
    parts.join(", ")
}

pub fn time_spoken_secs(seconds: f64) -> String {
    time_spoken(Duration::from_secs_f64(seconds.max(0.0)))
}

/// "3 weeks ago" with the unit's plural form.
pub fn ago(count: i64, unit: &str) -> String {
    match unit {
        "second" => plural(count, "{0} second ago", "{0} seconds ago"),
        "minute" => plural(count, "{0} minute ago", "{0} minutes ago"),
        "hour" => plural(count, "{0} hour ago", "{0} hours ago"),
        "day" => plural(count, "{0} day ago", "{0} days ago"),
        "week" => plural(count, "{0} week ago", "{0} weeks ago"),
        "month" => plural(count, "{0} month ago", "{0} months ago"),
        _ => plural(count, "{0} year ago", "{0} years ago"),
    }
}

/// "just now", "5 minutes ago", "yesterday", "3 weeks ago", ...
pub fn age_spoken(when: DateTime<Utc>, now: DateTime<Utc>) -> String {
    let age = (now - when).num_seconds().max(0) as f64;
    let days = age / 86400.0;
    if age < 60.0 {
        t!("just now")
    } else if age < 3600.0 {
        ago((age / 60.0) as i64, "minute")
    } else if age < 86400.0 {
        ago((age / 3600.0) as i64, "hour")
    } else if days < 2.0 {
        t!("yesterday")
    } else if days < 7.0 {
        ago(days as i64, "day")
    } else if days < 31.0 {
        ago((days / 7.0) as i64, "week")
    } else if days < 365.0 {
        ago((days / 30.4) as i64, "month")
    } else {
        ago((days / 365.25) as i64, "year")
    }
}

/// YouTube's English upload ages ("2 years ago", "Streamed 3 days ago") in the user's language.
pub fn translate_age(english: &str) -> String {
    if english.trim().is_empty() || current() == "en" {
        return english.to_owned();
    }
    static RE: OnceLock<Regex> = OnceLock::new();
    let re = RE.get_or_init(|| {
        Regex::new(r"(?i)^(?:(Streamed|Premiered)\s+)?(\d+)\s+(second|minute|hour|day|week|month|year)s?\s+ago$").unwrap()
    });
    let Some(m) = re.captures(english.trim()) else { return english.to_owned() };
    let count: i64 = m[2].parse().unwrap_or(0);
    let text = ago(count, &m[3].to_lowercase());
    match m.get(1).map(|g| g.as_str().to_lowercase()) {
        Some(kind) if kind == "streamed" => t!("streamed {0}", text),
        Some(_) => t!("premiered {0}", text),
        None => text,
    }
}

pub fn volume_spoken(percent: f64) -> String {
    t!("Volume {0} percent", (percent.round() as i64).clamp(0, 200))
}

pub fn speed_spoken(speed: f64) -> String {
    t!("Playback speed {0}x", trim_number(speed, 2))
}

pub fn search_results_spoken(count: usize, query: &str) -> String {
    if count == 0 { t!("No results for {0}.", query) } else { plural(count as i64, "{0} result.", "{0} results.") }
}

/// "1.2 million views", "3 thousand views", "15 views".
pub fn view_count(count: i64) -> String {
    let c = count as f64;
    if count >= 1_000_000_000 {
        t!("{0} billion views", trim_number(c / 1e9, 1))
    } else if count >= 1_000_000 {
        t!("{0} million views", trim_number(c / 1e6, 1))
    } else if count >= 1_000 {
        t!("{0} thousand views", trim_number(c / 1e3, 1))
    } else {
        plural(count, "{0} view", "{0} views")
    }
}

/// "1.25" with at most `decimals` decimals, using the language's decimal sign.
pub fn trim_number(value: f64, decimals: usize) -> String {
    let mut s = format!("{value:.decimals$}");
    if s.contains('.') {
        s = s.trim_end_matches('0').trim_end_matches('.').to_owned();
    }
    if matches!(current(), "ro" | "uk" | "es") { s.replace('.', ",") } else { s }
}

/// "3:05" or "1:02:03" for the position slider's value.
pub fn clock(seconds: f64) -> String {
    let total = seconds.max(0.0) as u64;
    let (h, m, s) = (total / 3600, (total % 3600) / 60, total % 60);
    if h > 0 { format!("{h}:{m:02}:{s:02}") } else { format!("{m}:{s:02}") }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn speaks_times_and_counts() {
        assert_eq!(time_spoken(Duration::from_secs(192)), "3 minutes, 12 seconds");
        assert_eq!(time_spoken(Duration::from_secs(3605)), "1 hour, 5 seconds");
        assert_eq!(time_spoken(Duration::from_secs(0)), "0 seconds");
        assert_eq!(view_count(1_234_567), "1.2 million views");
        assert_eq!(view_count(1), "1 view");
        assert_eq!(clock(3723.0), "1:02:03");
    }
}
