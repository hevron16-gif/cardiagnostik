# -*- coding: utf-8 -*-
"""
Парсер таблицы расшифровки OBD-II с chiptuner.ru/content/obdcod/ (русские описания generic-кодов).
Использование: python scripts/parse_chiptuner.py <путь к скачанному html>
Результат: scripts/source-data/chiptuner_generic_ru.json  {code: description_ru}
"""
import re
import sys
import json
import html as html_lib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "scripts" / "source-data" / "chiptuner_generic_ru.json"

def main(html_path: str) -> None:
    raw = Path(html_path).read_text(encoding="utf-8", errors="ignore")
    raw = re.sub(r"<script.*?</script>", " ", raw, flags=re.S | re.I)
    raw = re.sub(r"<style.*?</style>", " ", raw, flags=re.S | re.I)
    text = re.sub(r"<[^>]+>", " ", raw)
    text = html_lib.unescape(text)
    text = re.sub(r"\s+", " ", text)

    # Пары "КОД описание" — описание тянется до следующего кода
    pairs = re.findall(
        r"\b([PBUC]\d[0-9A-F]{3})\s+([А-Яа-яЁёA-Za-z].*?)(?=\s+[PBUC]\d[0-9A-F]{3}\s|\s*$)",
        text)

    cyr = re.compile(r"[А-Яа-яЁё]")
    out: dict[str, str] = {}
    for code, desc in pairs:
        desc = desc.strip(" .;")
        if not cyr.search(desc):   # только русские описания (англ.-only = чужая специфика)
            continue
        if len(desc) < 10:         # обрывки
            continue
        # при дублях берём самое длинное описание
        if code not in out or len(desc) > len(out[code]):
            out[code] = desc

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=0), encoding="utf-8")
    print(f"извлечено RU-описаний: {len(out)}")
    print(f"записано: {OUT}")

if __name__ == "__main__":
    main(sys.argv[1])
