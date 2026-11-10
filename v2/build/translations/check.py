"""Lists the English texts TimePlayer 2 shows that translations.json has no entry for.

Texts are taken from t("..."), tf("...", ...), plural(n, "...", "..."), wx_label("..."), menu items and the
other helpers that pass 1.x's English text through t(). Run from the v2 folder:
    python build/translations/check.py
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRANSLATIONS = json.loads((ROOT / "crates/timeplayer-core/translations.json").read_text(encoding="utf-8"))

# Calls whose first string argument is an English text.
CALLS = r"\b(?:t|tf|wx_label|t!|button|item|check|plain_check|submenu|choice|slider|document|text_block|menu_text|with_more)\(\s*(?:&?[a-z_]+,\s*)?"
STRING = r'"((?:[^"\\]|\\.)*)"'
PLURAL = re.compile(r"\bplural\([^,]+,\s*" + STRING + r",\s*" + STRING)
FIRST = re.compile(CALLS + STRING)


def unescape(text):
    return bytes(text, "utf-8").decode("unicode_escape").encode("latin-1").decode("utf-8")


def main():
    missing = {}
    for path in sorted((ROOT / "crates/timeplayer/src").glob("*.rs")):
        source = path.read_text(encoding="utf-8")
        # Rust string continuations: a backslash at a line's end drops the newline and the next line's indent.
        source = re.sub(r"\\\n\s*", "", source)
        for match in FIRST.finditer(source):
            text = unescape(match.group(1))
            if text and text not in TRANSLATIONS and any(c.isalpha() for c in text):
                missing.setdefault(text, path.name)
        for match in PLURAL.finditer(source):
            key = unescape(match.group(1)) + "|" + unescape(match.group(2))
            if key not in TRANSLATIONS:
                missing.setdefault(key, path.name)
    for text, where in sorted(missing.items(), key=lambda m: (m[1], m[0])):
        print(f"{where}: {text!r}")
    print(f"{len(missing)} texts without translations", file=sys.stderr)


if __name__ == "__main__":
    main()
