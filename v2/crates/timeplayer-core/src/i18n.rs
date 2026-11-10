//! Translations, shared with TimePlayer 1: text is written in English in the code and looked up in
//! `translations.json` (made from 1.x's tables). Anything without a translation stays in English.
//! Counts go through [`plural`], because Romanian and Ukrainian have three forms.

use std::collections::HashMap;
use std::sync::{OnceLock, RwLock};

static TRANSLATIONS: &str = include_str!("../translations.json");

type Table = HashMap<String, HashMap<String, String>>;

fn table() -> &'static Table {
    static TABLE: OnceLock<Table> = OnceLock::new();
    TABLE.get_or_init(|| serde_json::from_str(TRANSLATIONS).unwrap_or_default())
}

static CURRENT: RwLock<&'static str> = RwLock::new("en");

/// Code, the language's own name, and its English name (translated where it's shown).
pub const LANGUAGES: &[(&str, &str, &str)] = &[
    ("", "Automatic (Windows language)", "Automatic (Windows language)"),
    ("en", "English", "English"),
    ("ro", "Română", "Romanian"),
    ("uk", "Українська", "Ukrainian"),
    ("es", "Español", "Spanish"),
];

/// Chooses the language; empty follows the system, falling back to English.
pub fn use_language(code: &str) {
    let chosen = if code.trim().is_empty() {
        sys_locale::get_locale().unwrap_or_default().to_lowercase()
    } else {
        code.trim().to_lowercase()
    };
    let language = match chosen.get(..2).unwrap_or("") {
        "ro" => "ro",
        "uk" => "uk",
        "es" => "es",
        _ => "en",
    };
    *CURRENT.write().unwrap() = language;
}

/// The language in use: "en", "ro", "uk" or "es".
pub fn current() -> &'static str {
    *CURRENT.read().unwrap()
}

/// The translation of an English text.
pub fn t(english: &str) -> String {
    let language = current();
    if language != "en"
        && let Some(found) = table().get(english).and_then(|by| by.get(language))
            && !found.is_empty() {
                return found.clone();
            }
    english.to_owned()
}

/// Translates a format string ("Skipped {0}.") and fills it in.
pub fn tf(english_format: &str, args: &[&dyn std::fmt::Display]) -> String {
    format(&t(english_format), args)
}

/// A count with the right noun form: `plural(n, "{0} video", "{0} videos")`. Translations hold their
/// forms separated by "|" (Romanian: one|few|other, Ukrainian: one|few|many, Spanish: one|other).
pub fn plural(count: i64, english_one: &str, english_other: &str) -> String {
    let key = format!("{english_one}|{english_other}");
    let language = current();
    let form = match table().get(&key).and_then(|by| by.get(language)).filter(|s| !s.is_empty() && language != "en") {
        Some(translated) => {
            let forms: Vec<&str> = translated.split('|').collect();
            forms[plural_index(language, count).min(forms.len() - 1)].to_owned()
        }
        None => (if count == 1 { english_one } else { english_other }).to_owned(),
    };
    format(&form, &[&count])
}

/// Which plural form a count takes in a language.
pub fn plural_index(language: &str, count: i64) -> usize {
    let n = count.unsigned_abs();
    match language {
        // 1 videoclip; 2-19 videoclipuri (and 0, 101-119, ...); 20+ de videoclipuri.
        "ro" => {
            if n == 1 {
                0
            } else if n == 0 || (1..=19).contains(&(n % 100)) {
                1
            } else {
                2
            }
        }
        // 1, 21, 31 відео; 2-4, 22-24 …; 5-20, 25-30 …
        "uk" => {
            if n % 10 == 1 && n % 100 != 11 {
                0
            } else if (2..=4).contains(&(n % 10)) && !(12..=14).contains(&(n % 100)) {
                1
            } else {
                2
            }
        }
        _ => usize::from(n != 1),
    }
}

/// Fills .NET-style placeholders: "{0}", "{1}", and "{0:0.#}" (at most one decimal, none if whole).
pub fn format(template: &str, args: &[&dyn std::fmt::Display]) -> String {
    let mut out = String::with_capacity(template.len() + 16);
    let mut rest = template;
    while let Some(open) = rest.find('{') {
        out.push_str(&rest[..open]);
        let after = &rest[open + 1..];
        if let Some(stripped) = after.strip_prefix('{') {
            out.push('{');
            rest = stripped;
            continue;
        }
        let Some(close) = after.find('}') else {
            out.push_str(&rest[open..]);
            return out;
        };
        let inner = &after[..close];
        let (index, spec) = match inner.split_once(':') {
            Some((i, s)) => (i, Some(s)),
            None => (inner, None),
        };
        match index.trim().parse::<usize>().ok().and_then(|i| args.get(i)) {
            Some(value) => {
                let text = value.to_string();
                match (spec, text.parse::<f64>()) {
                    (Some(spec), Ok(number)) if spec.eq_ignore_ascii_case("N0") => out.push_str(&group_digits(number.round() as i64)),
                    (Some(spec), Ok(number)) if spec.contains('.') => {
                        let decimals = spec.split('.').nth(1).map(|d| d.len()).unwrap_or(0);
                        let mut s = format!("{number:.decimals$}");
                        if s.contains('.') {
                            s = s.trim_end_matches('0').trim_end_matches('.').to_owned();
                        }
                        out.push_str(&s);
                    }
                    _ => out.push_str(&text),
                }
            }
            None => out.push_str(&rest[open..=open + 1 + close]),
        }
        rest = &after[close + 1..];
    }
    out.push_str(&rest.replace("}}", "}"));
    out
}

/// "1,234,567" with the language's thousands separator (as .NET's N0).
pub fn group_digits(n: i64) -> String {
    let separator = match current() {
        "ro" | "es" => '.',
        "uk" => '\u{a0}',
        _ => ',',
    };
    let digits = n.unsigned_abs().to_string();
    let mut out = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            out.push(separator);
        }
        out.push(c);
    }
    if n < 0 { format!("-{out}") } else { out }
}

/// Every English text that has a translation (for the checker and tests).
pub fn known_texts() -> impl Iterator<Item = &'static String> {
    table().keys()
}

/// `t!("text")` and `t!("Skipped {0}.", title)`.
#[macro_export]
macro_rules! t {
    ($text:expr) => {
        $crate::i18n::t($text)
    };
    ($text:expr, $($arg:expr),+ $(,)?) => {
        $crate::i18n::tf($text, &[$(&$arg as &dyn ::std::fmt::Display),+])
    };
}

/// `plural_t!(count, "{0} video", "{0} videos")` for any integer count.
#[macro_export]
macro_rules! plural_t {
    ($count:expr, $one:expr, $other:expr) => {
        $crate::i18n::plural(($count) as i64, $one, $other)
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn plural_forms_follow_each_language() {
        assert_eq!(plural_index("ro", 1), 0);
        assert_eq!(plural_index("ro", 5), 1);
        assert_eq!(plural_index("ro", 25), 2);
        assert_eq!(plural_index("ro", 101), 1);
        assert_eq!(plural_index("uk", 21), 0);
        assert_eq!(plural_index("uk", 3), 1);
        assert_eq!(plural_index("uk", 12), 2);
        assert_eq!(plural_index("es", 0), 1);
    }

    #[test]
    fn formats_like_dotnet() {
        assert_eq!(format("Skipped {0}, {1}.", &[&"a", &3]), "Skipped a, 3.");
        assert_eq!(format("{0:0.#} seconds", &[&2.5]), "2.5 seconds");
        assert_eq!(format("{0:0.#} seconds", &[&2.0]), "2 seconds");
        assert_eq!(format("{{literal}}", &[]), "{literal}");
    }

    #[test]
    fn translations_load_from_1x() {
        assert!(known_texts().count() > 800);
        assert!(table().get("Press F1 for help.").is_some());
    }
}
