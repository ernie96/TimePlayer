# Makes v2's translations.json from TimePlayer 1's Romanian, Ukrainian and Spanish tables (*.Strings.cs), so
# both versions share one set of translations:  python -I v2/build/translations/export.py
# The JSON maps each English text to {"ro": ..., "uk": ..., "es": ...}. Plural texts keep 1.x's form,
# "one|other" as the key and the language's forms separated by "|".
import json, os, re

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, '..', '..', '..'))
SOURCE = os.path.join(REPO, 'src', 'TimePlayer.Core', 'Localization')
TARGET = os.path.join(REPO, 'v2', 'crates', 'timeplayer-core', 'translations.json')

ENTRY = re.compile(r'^\s*table\["((?:[^"\\]|\\.)*)"\] = "((?:[^"\\]|\\.)*)";', re.M)


def unescape(s):
    out, i = [], 0
    while i < len(s):
        c = s[i]
        if c == '\\' and i + 1 < len(s):
            n = s[i + 1]
            out.append({'n': '\n', 't': '\t', 'r': '\r', '"': '"', '\\': '\\', "'": "'", '0': '\0'}.get(n, n))
            i += 2
        else:
            out.append(c)
            i += 1
    return ''.join(out)


table = {}
for code, name in (('ro', 'Romanian'), ('uk', 'Ukrainian'), ('es', 'Spanish')):
    text = open(os.path.join(SOURCE, name + '.Strings.cs'), encoding='utf-8').read()
    for m in ENTRY.finditer(text):
        table.setdefault(unescape(m.group(1)), {})[code] = unescape(m.group(2))

# Texts new in 2.0, translated here until 1.x's tables are retired.
with open(os.path.join(HERE, 'v2_texts.json'), encoding='utf-8') as f:
    for english, forms in json.load(f).items():
        table.setdefault(english, {}).update(forms)

with open(TARGET, 'w', encoding='utf-8', newline='\n') as f:
    json.dump(dict(sorted(table.items())), f, ensure_ascii=False, indent=1)
print(f'{len(table)} texts written to {TARGET}')
