"""
AutoDiag AI — Database module v1.0.15
SQLite с защитой от SQL-инъекций (параметризованные запросы)
"""
import asyncio
import json
import logging
import os
import sqlite3
from datetime import datetime, timezone
from typing import Optional, List, Dict, Any

logger = logging.getLogger("autodiag.db")

DB_PATH = os.getenv("DATABASE_URL", "sqlite:///data/autodiag.db").replace("sqlite:///", "")

async def init():
    os.makedirs(os.path.dirname(DB_PATH) if os.path.dirname(DB_PATH) else ".", exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.executescript("""
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code TEXT NOT NULL, brand TEXT, model TEXT, vin TEXT,
                result TEXT, ip TEXT, created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_history_code ON history(code);
            CREATE INDEX IF NOT EXISTS idx_history_created ON history(created_at);
            CREATE TABLE IF NOT EXISTS errors (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code TEXT NOT NULL UNIQUE, brand TEXT, description TEXT,
                causes TEXT, solutions TEXT, severity TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_errors_code ON errors(code);
            CREATE INDEX IF NOT EXISTS idx_errors_brand ON errors(brand);
            CREATE TABLE IF NOT EXISTS ai_cache (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                code TEXT NOT NULL, brand TEXT, result TEXT,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_ai_cache_lookup ON ai_cache(code, brand);
                        CREATE TABLE IF NOT EXISTS sync_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT,
                data TEXT,
                status TEXT DEFAULT 'pending',
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_sync_queue_device ON sync_queue(device_id, status);
            CREATE TABLE IF NOT EXISTS users (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT UNIQUE, tier TEXT DEFAULT 'free',
                features TEXT, created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
        """)
        conn.commit()
        logger.info("Database initialized")
    finally:
        conn.close()

async def close():
    logger.info("Database connections closed")

async def ping():
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.execute("SELECT 1")
    finally:
        conn.close()

async def lookup_error(code: str, brand: Optional[str] = None) -> Dict[str, Any]:
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        if brand:
            cursor.execute("SELECT * FROM errors WHERE code = ? AND (brand = ? OR brand IS NULL) ORDER BY brand DESC LIMIT 1", (code.upper(), brand))
        else:
            cursor.execute("SELECT * FROM errors WHERE code = ? LIMIT 1", (code.upper(),))
        row = cursor.fetchone()
        if row: return dict(row)
        return {"code": code, "description": "Ошибка не найдена в базе", "causes": [], "solutions": []}
    finally:
        conn.close()

async def lookup_errors_batch(codes: List[str], brand: Optional[str] = None) -> List[Dict[str, Any]]:
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        placeholders = ",".join("?" * len(codes))
        query = f"SELECT * FROM errors WHERE code IN ({placeholders})"
        params = [c.upper() for c in codes]
        if brand:
            query += " AND (brand = ? OR brand IS NULL)"
            params.append(brand)
        cursor.execute(query, params)
        return [dict(row) for row in cursor.fetchall()]
    finally:
        conn.close()

async def save_diagnosis(code: str, brand: Optional[str], model: Optional[str], vin: Optional[str], result: dict, ip: Optional[str] = None) -> int:
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("INSERT INTO history (code, brand, model, vin, result, ip) VALUES (?, ?, ?, ?, ?, ?)",
            (code.upper(), brand, model, vin, json.dumps(result, ensure_ascii=False), ip))
        conn.commit()
        return cursor.lastrowid
    finally:
        conn.close()

async def get_all_history(limit: int = 50, offset: int = 0) -> List[Dict[str, Any]]:
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        cursor.execute("SELECT * FROM history ORDER BY created_at DESC LIMIT ? OFFSET ?", (limit, offset))
        rows = cursor.fetchall()
        result = []
        for row in rows:
            d = dict(row)
            try: d["result"] = json.loads(d["result"]) if d["result"] else {}
            except: d["result"] = {"raw": d["result"]}
            result.append(d)
        return result
    finally:
        conn.close()

async def get_error_stats() -> Dict[str, Any]:
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("SELECT COUNT(*) FROM history")
        total = cursor.fetchone()[0]
        cursor.execute("SELECT code, COUNT(*) as cnt FROM history GROUP BY code ORDER BY cnt DESC LIMIT 10")
        top = [{"code": row[0], "count": row[1]} for row in cursor.fetchall()]
        return {"total_diagnoses": total, "top_codes": top}
    finally:
        conn.close()

async def save_historical_code(code: str, data: dict) -> bool:
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("INSERT OR REPLACE INTO errors (code, description, causes, solutions) VALUES (?, ?, ?, ?)",
            (code.upper(), data.get("description"), json.dumps(data.get("causes", [])), json.dumps(data.get("solutions", []))))
        conn.commit()
        return True
    finally:
        conn.close()

async def get_historical_codes(limit: int = 100) -> List[Dict[str, Any]]:
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        cursor.execute("SELECT * FROM errors LIMIT ?", (limit,))
        return [dict(row) for row in cursor.fetchall()]
    finally:
        conn.close()

async def lookup_ai_cache(code: str, brand: Optional[str] = None) -> Optional[dict]:
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        cursor.execute("SELECT result FROM ai_cache WHERE code = ? AND (brand = ? OR brand IS NULL) ORDER BY created_at DESC LIMIT 1",
            (code.upper(), brand))
        row = cursor.fetchone()
        if row:
            try: return json.loads(row["result"])
            except: return None
        return None
    finally:
        conn.close()

async def save_ai_cache(code: str, brand: Optional[str], result: dict) -> bool:
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("INSERT INTO ai_cache (code, brand, result) VALUES (?, ?, ?)",
            (code.upper(), brand, json.dumps(result, ensure_ascii=False)))
        conn.commit()
        return True
    finally:
        conn.close()

def check_ai_rate_limit(client_ip: str) -> bool:
    return True

def get_ai_rate_limit_remaining(client_ip: str) -> int:
    return 100

async def get_user_tier(device_id: str) -> str:
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("SELECT tier FROM users WHERE device_id = ?", (device_id,))
        row = cursor.fetchone()
        return row[0] if row else "free"
    finally:
        conn.close()

async def get_user_features(device_id: str) -> List[str]:
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("SELECT features FROM users WHERE device_id = ?", (device_id,))
        row = cursor.fetchone()
        if row and row[0]:
            try: return json.loads(row[0])
            except: return []
        return []
    finally:
        conn.close()

async def search_cars(query: str, brand: Optional[str] = None, limit: int = 20) -> List[Dict[str, Any]]:
    return [{"title": f"Результат поиска: {query}", "url": "", "source": "internal"}]


# ==================== SYNC STUBS (для совместимости) ====================
async def queue_sync(device_id: str, data: dict) -> bool:
    """Добавление данных в очередь синхронизации"""
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("INSERT INTO sync_queue (device_id, data, status) VALUES (?, ?, 'pending')",
            (device_id, json.dumps(data, ensure_ascii=False)))
        conn.commit()
        return True
    finally:
        conn.close()

async def get_sync_queue(device_id: str, limit: int = 100) -> List[Dict[str, Any]]:
    """Получение очереди синхронизации"""
    conn = sqlite3.connect(DB_PATH)
    try:
        conn.row_factory = sqlite3.Row
        cursor = conn.cursor()
        cursor.execute("SELECT * FROM sync_queue WHERE device_id = ? AND status = 'pending' LIMIT ?",
            (device_id, limit))
        return [dict(row) for row in cursor.fetchall()]
    finally:
        conn.close()

async def mark_synced(sync_id: int) -> bool:
    """Отметка элемента как синхронизированного"""
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute("UPDATE sync_queue SET status = 'synced' WHERE id = ?", (sync_id,))
        conn.commit()
        return True
    finally:
        conn.close()


# ==================== ADMIN STUBS ====================
async def auto_update_codes(codes: List[Dict[str, Any]]) -> int:
    """Автоматическое обновление кодов ошибок"""
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        updated = 0
        for code_data in codes:
            cursor.execute(
                "INSERT OR REPLACE INTO errors (code, brand, description, causes, solutions) VALUES (?, ?, ?, ?, ?)",
                (code_data.get('code', '').upper(), code_data.get('brand'), 
                 code_data.get('description'), json.dumps(code_data.get('causes', [])), 
                 json.dumps(code_data.get('solutions', [])))
            )
            updated += 1
        conn.commit()
        return updated
    finally:
        conn.close()

async def set_user_tier(device_id: str, tier: str) -> bool:
    """Установка тарифа пользователя"""
    conn = sqlite3.connect(DB_PATH)
    try:
        cursor = conn.cursor()
        cursor.execute(
            "INSERT INTO users (device_id, tier) VALUES (?, ?) ON CONFLICT(device_id) DO UPDATE SET tier=excluded.tier",
            (device_id, tier)
        )
        conn.commit()
        return True
    finally:
        conn.close()


# ==================== CONNECTION STUB ====================
def get_conn():
    """Получение соединения с БД (для совместимости)"""
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn
