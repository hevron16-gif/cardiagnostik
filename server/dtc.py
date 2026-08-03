"""
AutoDiag AI — справочник DTC (OBD-II Diagnostic Trouble Codes).

Данные:
  - data/dtc_codes.db — Wal33D/dtc-database (MIT, см. data/dtc_codes.LICENSE.txt):
    18 805 определений, 12 128 уникальных кодов, 33 марки, англ.
  - data/dtc_ru.json — русская надстройка проекта (описания, причины, решения),
    собирается scripts/build_dtc_ru.py.

Работает read-only, без записи в БД — точечные SELECT по индексу кода.
"""
import json
import sqlite3
from pathlib import Path
from typing import Optional

BASE_DIR = Path(__file__).resolve().parent
DB_PATH = BASE_DIR / "data" / "dtc_codes.db"
RU_PATH = BASE_DIR / "data" / "dtc_ru.json"

_RU: dict = {}
if RU_PATH.exists():
    _RU = json.loads(RU_PATH.read_text(encoding="utf-8"))


def _conn() -> sqlite3.Connection:
    """Read-only подключение к справочнику (файл не меняется в рантайме)."""
    return sqlite3.connect(f"file:{DB_PATH}?mode=ro", uri=True)


def get_code(code: str, manufacturer: Optional[str] = None) -> Optional[dict]:
    """
    Расшифровка кода. Приоритет описания: русская надстройка → англ. GENERIC.
    Возвращает None, если код неизвестен ни в одном источнике.
    """
    code = code.strip().upper()
    ru = _RU.get(code)
    with _conn() as conn:
        rows = conn.execute(
            "SELECT manufacturer, description, is_generic FROM dtc_definitions"
            " WHERE code = ? ORDER BY is_generic DESC, manufacturer",
            (code,),
        ).fetchall()

    if not ru and not rows:
        return None

    generic = next((d for m, d, g in rows if g == 1), None)
    variants = [{"manufacturer": m, "description": d} for m, d, g in rows if g == 0]
    if manufacturer:
        want = manufacturer.strip().upper()
        variants.sort(key=lambda v: v["manufacturer"] != want)

    return {
        "code": code,
        "description_ru": ru["description_ru"] if ru else None,
        "description_en": generic or (variants[0]["description"] if variants else None),
        "causes": ru.get("causes", []) if ru else [],
        "solutions": ru.get("solutions", []) if ru else [],
        "symptoms": ru.get("symptoms") if ru else None,
        "severity": ru.get("severity") if ru else None,
        "manufacturer_variants": variants[:10],
        "source": "ru-overlay" if ru else "dtc-database",
    }


def search_codes(query: str, limit: int = 20) -> list:
    """Поиск по коду или тексту описания (generic-часть справочника)."""
    q = query.strip()
    like = f"%{q}%"
    with _conn() as conn:
        rows = conn.execute(
            "SELECT DISTINCT code, description FROM dtc_definitions"
            " WHERE is_generic = 1 AND (UPPER(code) LIKE UPPER(?) OR description LIKE ?)"
            " ORDER BY code LIMIT ?",
            (like, like, limit),
        ).fetchall()
    return [{"code": c, "description": d} for c, d in rows]


def stats() -> dict:
    """Статистика справочника для /dtc/stats."""
    with _conn() as conn:
        total = conn.execute("SELECT COUNT(*) FROM dtc_definitions").fetchone()[0]
        unique = conn.execute("SELECT COUNT(DISTINCT code) FROM dtc_definitions").fetchone()[0]
        makers = conn.execute("SELECT COUNT(DISTINCT manufacturer) FROM dtc_definitions").fetchone()[0]
    return {
        "definitions": total,
        "unique_codes": unique,
        "manufacturers": makers,
        "russian_overlay": len(_RU),
        "license": "MIT (Wal33D/dtc-database)",
    }
