# -*- coding: utf-8 -*-
"""
Сборка русской надстройки DTC (server/data/dtc_ru.json) из источников проекта:
  1. scripts/source-data/kim_russian_codes.json — 126 записей (УАЗ, ГБО, специфика РФ)
  2. server/schemas/data.py (_SCHEMAS) — 101 код: title, description, checkpoints
  3. mobile/Data/OBD2Codes.cs — офлайн-база приложения: Description, Causes, Symptoms

Приоритет: kim > schemas > obd2codes. Запуск из корня репо: python scripts/build_dtc_ru.py
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "server" / "data" / "dtc_ru.json"

overlay: dict[str, dict] = {}


def merge(code: str, *, description=None, causes=None, solutions=None,
          symptoms=None, severity=None, ecu=None, category=None, source: str):
    """Заполняет только пустые поля — первый источник важнее."""
    entry = overlay.setdefault(code, {
        "description_ru": None, "causes": [], "solutions": [],
        "symptoms": None, "severity": None, "ecu": None, "category": None,
        "sources": [],
    })
    if description and not entry["description_ru"]:
        entry["description_ru"] = description
    if causes and not entry["causes"]:
        entry["causes"] = list(causes)
    if solutions and not entry["solutions"]:
        entry["solutions"] = list(solutions)
    if symptoms and not entry["symptoms"]:
        entry["symptoms"] = symptoms
    if severity and not entry["severity"]:
        entry["severity"] = severity
    if ecu and not entry["ecu"]:
        entry["ecu"] = ecu
    if category and not entry["category"]:
        entry["category"] = category
    entry["sources"].append(source)


# ─── 1. kim (специфика РФ: УАЗ, ГБО и др.) ──────────────────────────
kim_path = ROOT / "scripts" / "source-data" / "kim_russian_codes.json"
kim = json.loads(kim_path.read_text(encoding="utf-8"))
for code, r in kim.items():
    merge(code, description=r["description"], causes=r["causes"],
          solutions=r["solutions"], severity=r["severity"],
          ecu=r.get("ecu"), category=r.get("category"), source="kim")
print(f"kim: {len(kim)} записей")

# ─── 1.5. Грузовая техника (Cummins SPN/FMI, Январь-7 flash) ────────
truck_path = ROOT / "scripts" / "source-data" / "truck_codes.json"
if truck_path.exists():
    truck = json.loads(truck_path.read_text(encoding="utf-8"))
    for code, r in truck.items():
        merge(code, description=r["description"], causes=r.get("causes"),
              solutions=r.get("solutions"), severity=r.get("severity"),
              ecu=r.get("ecu"), source="truck")
    print(f"truck: {len(truck)} записей")

# ─── 2. schemas/data.py (_SCHEMAS) ──────────────────────────────────
sys.path.insert(0, str(ROOT / "server"))
from schemas.data import _SCHEMAS  # noqa: E402

for code, s in _SCHEMAS.items():
    merge(code, description=s.get("title"), solutions=s.get("checkpoints"),
          source="schemas")
print(f"schemas: {len(_SCHEMAS)} записей")

# ─── 3. mobile/Data/OBD2Codes.cs ────────────────────────────────────
cs_path = ROOT / "mobile" / "Data" / "OBD2Codes.cs"
cs_text = cs_path.read_text(encoding="utf-8-sig")
entry_re = re.compile(r'new\(\)\s*\{([^}]+)\}')
field_re = {
    "Code": re.compile(r'Code = "([^"]+)"'),
    "Description": re.compile(r'Description = "([^"]+)"'),
    "Causes": re.compile(r'Causes = "([^"]+)"'),
    "Symptoms": re.compile(r'Symptoms = "([^"]+)"'),
}
cs_count = 0
for m in entry_re.finditer(cs_text):
    body = m.group(1)
    fields = {k: (f.search(body).group(1) if f.search(body) else None)
              for k, f in field_re.items()}
    if not fields["Code"]:
        continue
    merge(fields["Code"], description=fields["Description"],
          causes=[fields["Causes"]] if fields["Causes"] else None,
          symptoms=fields["Symptoms"], source="obd2codes")
    cs_count += 1
print(f"obd2codes: {cs_count} записей")

# ─── 4. chiptuner.ru — русские описания generic-кодов (заполняет пробелы) ──
chip_path = ROOT / "scripts" / "source-data" / "chiptuner_generic_ru.json"
if chip_path.exists():
    chip = json.loads(chip_path.read_text(encoding="utf-8"))
    for code, desc in chip.items():
        merge(code, description=desc, source="chiptuner")
    print(f"chiptuner: {len(chip)} записей")

# ─── Итог ────────────────────────────────────────────────────────────
OUT.parent.mkdir(parents=True, exist_ok=True)
OUT.write_text(json.dumps(overlay, ensure_ascii=False, indent=1), encoding="utf-8")

full = sum(1 for e in overlay.values() if e["causes"] and e["solutions"])
print(f"\nИтого уникальных кодов в надстройке: {len(overlay)}")
print(f"  с причинами и решениями: {full}")
print(f"  файл: {OUT} ({OUT.stat().st_size} байт)")
