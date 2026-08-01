"""
AutoDiag AI — Schema Image Scraper v2
Ищет, сжимает, валидирует через DeepSeek и складывает схемы по папкам.
Запуск: раз в 2 недели (GitHub Actions) или вручную через /admin/schemas/scrape
"""

import os
import io
import json
import asyncio
import logging
import base64
import re
from pathlib import Path
from typing import Optional, List
from datetime import datetime, timezone

import httpx
from PIL import Image

logger = logging.getLogger("schema_scraper")

BASE_DIR = Path(__file__).parent
SCHEMA_DIR = BASE_DIR / "schema_images"
SCHEMA_DIR.mkdir(parents=True, exist_ok=True)

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY", "")
DEEPSEEK_URL = "https://api.deepseek.com/v1/chat/completions"

# ═══ РЕЕСТР АВТО (добавляйте сюда новые) ═══════════════════════════
AUTO_REGISTRY = [
    # Легковые
    {"brand": "Lada", "model": "Granta", "engine": "1.6_8V", "category": "passenger",
     "queries": ["схема двигателя Lada Granta 1.6 8V разрез", "Lada Granta 11186 схема"]},
    {"brand": "Lada", "model": "Vesta", "engine": "1.6_16V", "category": "passenger",
     "queries": ["схема двигателя Lada Vesta 1.6 16V", "Веста 21129 схема двигателя"]},
    {"brand": "Lada", "model": "Niva", "engine": "1.7_8V", "category": "passenger",
     "queries": ["схема двигателя Нива 21214 1.7", "ВАЗ 21214 схема"]},
    {"brand": "UAZ", "model": "Patriot", "engine": "ZMZ_409", "category": "passenger",
     "queries": ["схема двигателя УАЗ Патриот ЗМЗ 409", "UAZ Patriot engine diagram"]},
    
    # Грузовики / дизель
    {"brand": "GAZ", "model": "Gazel_NEXT", "engine": "Cummins_2.8", "category": "truck",
     "queries": ["схема двигателя ГАЗель NEXT Cummins 2.8 ISF", "ГАЗон NEXT схема двигателя"]},
    {"brand": "KAMAZ", "model": "5490", "engine": "Cummins_ISG", "category": "truck",
     "queries": ["схема двигателя КАМАЗ 5490 Cummins ISG", "КАМАЗ Евро-5 схема двигателя"]},
    {"brand": "KAMAZ", "model": "65115", "engine": "Cummins_6.7", "category": "truck",
     "queries": ["схема двигателя КАМАЗ 65115", "КАМАЗ Cummins 6.7 схема"]},
    {"brand": "MMZ", "model": "D245", "engine": "diesel", "category": "truck",
     "queries": ["схема двигателя ММЗ Д-245 разрез", "Д-245 Евро-3 схема двигателя"]},
    {"brand": "MMZ", "model": "D260", "engine": "diesel", "category": "truck",
     "queries": ["схема двигателя ММЗ Д-260", "Д-260 Евро-2 схема"]},
    {"brand": "MAZ", "model": "4370", "engine": "YMZ_236", "category": "truck",
     "queries": ["схема двигателя МАЗ 4370 ЯМЗ 236", "ЯМЗ 236 схема разрез"]},
    {"brand": "MAZ", "model": "5440", "engine": "YMZ_238", "category": "truck",
     "queries": ["схема двигателя МАЗ 5440 ЯМЗ 238", "ЯМЗ 238 турбо схема"]},
    {"brand": "Ural", "model": "4320", "engine": "YMZ_236", "category": "truck",
     "queries": ["схема двигателя Урал 4320 ЯМЗ 236", "Урал схема двигателя"]},
    
    # Автобусы
    {"brand": "PAZ", "model": "Vector", "engine": "MMZ_D245", "category": "bus",
     "queries": ["схема двигателя ПАЗ Вектор ММЗ Д-245", "ПАЗ 3204 схема двигателя"]},
    {"brand": "PAZ", "model": "3205", "engine": "ZMZ_5234", "category": "bus",
     "queries": ["схема двигателя ПАЗ 3205 ЗМЗ 5234", "ПАЗ-3205 схема"]},
    {"brand": "LiAZ", "model": "5292", "engine": "Cummins_6.7", "category": "bus",
     "queries": ["схема двигателя ЛиАЗ 5292 Cummins", "ЛиАЗ схема двигателя"]},
    {"brand": "NefAZ", "model": "5299", "engine": "YMZ_536", "category": "bus",
     "queries": ["схема двигателя НефАЗ 5299 ЯМЗ 536", "НефАЗ автобус схема двигателя"]},
    
    # Спецтехника
    {"brand": "KAMAZ", "model": "6520", "engine": "Cummins_6.7", "category": "special",
     "queries": ["схема двигателя КАМАЗ 6520 самосвал", "КАМАЗ карьерный схема"]},
]

# ═══ СЖАТИЕ ════════════════════════════════════════════════════════

def optimize_image(img_bytes: bytes, max_width: int = 1200) -> Optional[bytes]:
    """Сжать PNG/JPG до мобильного размера. Возвращает PNG."""
    try:
        img = Image.open(io.BytesIO(img_bytes))
        
        if img.mode in ('RGBA', 'P', 'LA'):
            bg = Image.new('RGB', img.size, (255, 255, 255))
            if img.mode == 'P':
                img = img.convert('RGBA')
            if img.mode in ('RGBA', 'LA'):
                bg.paste(img, mask=img.split()[-1])
                img = bg
            else:
                img = img.convert('RGB')
        elif img.mode != 'RGB':
            img = img.convert('RGB')
        
        if img.width > max_width:
            ratio = max_width / img.width
            img = img.resize((max_width, int(img.height * ratio)), Image.LANCZOS)
        
        buf = io.BytesIO()
        img.save(buf, format='PNG', optimize=True, compress_level=9)
        result = buf.getvalue()
        
        if len(result) > 600 * 1024:
            buf = io.BytesIO()
            img.save(buf, format='JPEG', quality=70, optimize=True)
            result = buf.getvalue()
            
        return result
    except Exception as e:
        logger.warning(f"Optimize failed: {e}")
        return None

# ═══ DEEPSEEK ВАЛИДАЦИЯ ═════════════════════════════════════════════

async def deepseek_validate(img_bytes: bytes, query: str) -> bool:
    """Отправить картинку в DeepSeek. True = похоже на схему."""
    if not DEEPSEEK_API_KEY:
        return True
    
    try:
        header = img_bytes[:4]
        mime = "image/png"
        if header[:2] == b'\xff\xd8':
            mime = "image/jpeg"
        
        b64 = base64.b64encode(img_bytes).decode()
        
        payload = {
            "model": "deepseek-chat",
            "messages": [
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "text",
                            "text": (f"На изображении результат поиска по запросу: '{query}'. "
                                   f"Это техническая схема, чертёж или разрез двигателя/датчика автомобиля? "
                                   f"Ответь кратко: ДА или НЕТ.")
                        },
                        {
                            "type": "image_url",
                            "image_url": {"url": f"data:{mime};base64,{b64}"}
                        }
                    ]
                }
            ],
            "max_tokens": 20,
            "temperature": 0.1
        }
        
        async with httpx.AsyncClient(timeout=45) as client:
            resp = await client.post(
                DEEPSEEK_URL,
                json=payload,
                headers={"Authorization": f"Bearer {DEEPSEEK_API_KEY}", "Content-Type": "application/json"}
            )
            resp.raise_for_status()
            ans = resp.json()["choices"][0]["message"]["content"].strip().lower()
            ok = any(w in ans for w in ("да", "yes", "схема", "чертёж", "двигател"))
            logger.info(f"DeepSeek: {ans} -> {'PASS' if ok else 'REJECT'}")
            return ok
            
    except Exception as e:
        logger.warning(f"DeepSeek validation error: {e}")
        return True

# ═══ ПОИСК И СКАЧИВАНИЕ ═════════════════════════════════════════════

async def search_bing_images(query: str, limit: int = 5) -> List[str]:
    """Парсинг Bing Images."""
    from urllib.parse import quote_plus
    url = f"https://www.bing.com/images/search?q={quote_plus(query)}&first=1"
    
    headers = {
        "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"),
        "Accept-Language": "ru-RU,ru;q=0.9",
    }
    
    try:
        async with httpx.AsyncClient(timeout=20, follow_redirects=True) as client:
            resp = await client.get(url, headers=headers)
            resp.raise_for_status()
            html = resp.text
            
            urls = re.findall(r'"murl"\s*:\s*"(https?://[^"]+)"', html, re.IGNORECASE)
            urls += re.findall(r'https?://[^\s"<>\']+\.(?:jpg|jpeg|png|webp)', html, re.IGNORECASE)
            
            valid = []
            seen = set()
            bad = ["/icon", "/logo", "/avatar", "/thumb", "favicon", "google", "bing", "yandex", "facebook", "twitter"]
            
            for u in urls:
                low = u.lower()
                if any(b in low for b in bad):
                    continue
                if not re.search(r'\.(jpg|jpeg|png|webp)(\?|$)', low):
                    continue
                if u not in seen:
                    seen.add(u)
                    valid.append(u)
                if len(valid) >= limit:
                    break
            return valid
    except Exception as e:
        logger.error(f"Bing search failed: {e}")
        return []

async def download_image(url: str) -> Optional[bytes]:
    try:
        headers = {
            "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0.0.0 Safari/537.36",
            "Referer": "https://www.bing.com/"
        }
        async with httpx.AsyncClient(timeout=30, follow_redirects=True) as client:
            resp = await client.get(url, headers=headers)
            resp.raise_for_status()
            if len(resp.content) < 3000:
                return None
            return resp.content
    except Exception as e:
        logger.debug(f"Download failed: {e}")
        return None

# ═══ ОСНОВНАЯ ЛОГИКА ═════════════════════════════════════════════════

async def scrape_car(brand: str, model: str, engine: str, queries: List[str], max_images: int = 2) -> dict:
    brand_dir = SCHEMA_DIR / brand
    brand_dir.mkdir(parents=True, exist_ok=True)
    
    saved = []
    
    for query in queries:
        if len(saved) >= max_images:
            break
            
        urls = await search_bing_images(query, limit=max_images + 2)
        
        for url in urls:
            if len(saved) >= max_images:
                break
                
            raw = await download_image(url)
            if not raw:
                continue
            
            opt = optimize_image(raw)
            if not opt or len(opt) < 5000:
                continue
            
            if not await deepseek_validate(opt, query):
                continue
            
            fname = f"{model}_{engine}_{len(saved) + 1}.png"
            fpath = brand_dir / fname
            
            if fpath.exists():
                existing = fpath.read_bytes()
                if len(existing) == len(opt):
                    continue
                stem = f"{model}_{engine}_{len(saved) + 1}"
                fname = f"{stem}_alt.png"
                fpath = brand_dir / fname
            
            fpath.write_bytes(opt)
            saved.append(str(fpath.relative_to(BASE_DIR)))
            logger.info(f"Saved {fpath} ({len(opt)} bytes)")
            await asyncio.sleep(0.5)
        
        await asyncio.sleep(1)
    
    return {"brand": brand, "model": model, "engine": engine, "saved": saved}

async def run_full_scrape(registry: List[dict] = None) -> dict:
    if registry is None:
        registry = AUTO_REGISTRY
    
    results = []
    total = 0
    
    for car in registry:
        try:
            r = await scrape_car(
                car["brand"], car["model"], car["engine"],
                car.get("queries", [f"схема двигателя {car['brand']} {car['model']}"]),
                car.get("max_images", 2)
            )
            results.append(r)
            total += len(r["saved"])
            await asyncio.sleep(1.5)
        except Exception as e:
            logger.error(f"Scrape failed for {car}: {e}")
            results.append({"brand": car.get("brand"), "error": str(e)})
    
    report = {
        "time": datetime.now(timezone.utc).isoformat(),
        "total_cars": len(registry),
        "total_images": total,
        "results": results,
    }
    
    (SCHEMA_DIR / "_report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    logger.info(f"Scrape done: {total} images")
    return report

if __name__ == "__main__":
    asyncio.run(run_full_scrape())
