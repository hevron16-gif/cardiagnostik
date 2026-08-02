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
from html import unescape
from pathlib import Path
from typing import Optional, List, Tuple
from datetime import datetime, timezone

import httpx
from PIL import Image

logger = logging.getLogger("schema_scraper")

BASE_DIR = Path(__file__).parent
SCHEMA_DIR = BASE_DIR / "schema_images"
SCHEMA_DIR.mkdir(parents=True, exist_ok=True)

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY", "")
DEEPSEEK_URL = "https://api.deepseek.com/v1/chat/completions"

# ═══ ФИЛЬТРАЦИЯ МУСОРА ═════════════════════════════════════════════
# Стоки, киносайты, соцсети, обои — технических схем двигателей там нет
JUNK_DOMAINS = (
    "shutterstock.com", "dreamstime.com", "123rf.com", "depositphotos.com",
    "freepik.com", "alamy.com", "gettyimages.com", "istockphoto.com",
    "vectorstock.com", "stock.adobe.com",
    "kinopoisk.ru", "imdb.com", "filmibeat.com",
    "wallpapercave.com", "wallpapers.com", "wallpaperflare.com",
    "pinterest.com", "instagram.com", "facebook.com", "twitter.com",
    "x.com", "tiktok.com", "youtube.com", "ytimg.com",
    "vk.com", "ok.ru", "avito.ru",
    # Маркетплейсы — белый фон, но это фото товара, а не схема
    "media-amazon.com", "alicdn.com", "ebayimg.com",
    "walmartimages.com", "wildberries.ru", "ozon.ru",
)

# Минимальные требования к «схеме»: реальное изображение, не иконка,
# не вертикальный постер, светлый фон (чертежи рисуют на белом)
MIN_IMG_W, MIN_IMG_H = 400, 300
ASPECT_MIN, ASPECT_MAX = 0.4, 2.8
WHITE_BG_MIN = 0.30


def _is_junk_domain(url: str) -> bool:
    """True, если URL ведёт на сток/соцсеть/киносайт (сравнение по hostname)."""
    from urllib.parse import urlparse
    host = (urlparse(url).hostname or "").lower()
    return any(host == d or host.endswith("." + d) for d in JUNK_DOMAINS)


def validate_schema_image(raw: bytes) -> bool:
    """Эвристика «похоже на техническую схему» вместо vision-валидации."""
    try:
        img = Image.open(io.BytesIO(raw))
        img.load()
        if img.width < MIN_IMG_W or img.height < MIN_IMG_H:
            return False
        if not (ASPECT_MIN <= img.width / img.height <= ASPECT_MAX):
            return False
        # Доля почти белых пикселей: у схем/чертежей фон светлый,
        # у постеров и фотографий — нет
        small = img.convert("RGB").resize((64, 64))
        white = sum(1 for r, g, b in small.getdata()
                    if r >= 235 and g >= 235 and b >= 235)
        return (white / (64 * 64)) >= WHITE_BG_MIN
    except Exception:
        return False


# Признаки, что картинка/страница-источник вообще про авто
RELEVANCE_TERMS = (
    "engine", "dvigat", "двигат", "схема", "shema", "schema",
    "sensor", "датчик", "motor", "avto", "авто", "truck",
    "дизел", "diesel", "бензин", "lada", "ваз", "vaz",
    "kamaz", "камаз", "uaz", "уаз", "gaz",
    "mmz", "ммз", "yamz", "ямз", "zmz", "змз",
    "liaz", "лиаз", "cummins", "granta", "vesta", "niva", "patriot",
)


def _is_relevant(url: str, page: str, query: str) -> bool:
    """Отсев оффтопика: Bing часто возвращает картинки не по теме
    (инструменты, IT-диаграммы). Смотрим URL картинки и страницы-источника."""
    text = f"{url} {page}".lower()
    if any(t in text for t in RELEVANCE_TERMS):
        return True
    # Токены запроса с цифрами (коды моделей/двигателей: 21129, Д245, 409...)
    # Общие слова типа "engine"/"diagram" не считаем — они есть в любом оффтопике
    return any(any(c.isdigit() for c in t) and len(t) >= 3 and t.lower() in text
               for t in query.split())

# ═══ РЕЕСТР АВТО (добавляйте сюда новые) ═══════════════════════════
AUTO_REGISTRY = [
    # Легковые
    {"brand": "Lada", "model": "Granta", "engine": "1.6_8V", "category": "passenger",
     "queries": [
         "Lada Granta 11186 engine diagram",
         "Lada Granta 1.6 engine cross section",
         "схема двигателя Lada Granta 1.6 8V разрез",
         "Lada Granta 11186 схема"
     ]},
    {"brand": "Lada", "model": "Vesta", "engine": "1.6_16V", "category": "passenger",
     "queries": [
         "Lada Vesta 21129 engine diagram",
         "Lada Vesta 1.6 16V engine scheme",
         "схема двигателя Lada Vesta 1.6 16V",
         "Веста 21129 схема двигателя"
     ]},
    {"brand": "Lada", "model": "Niva", "engine": "1.7_8V", "category": "passenger",
     "queries": [
         "Lada Niva 21214 engine diagram",
         "VAZ 21214 1.7 engine scheme",
         "схема двигателя Нива 21214 1.7",
         "ВАЗ 21214 схема"
     ]},
    {"brand": "UAZ", "model": "Patriot", "engine": "ZMZ_409", "category": "passenger",
     "queries": [
         "UAZ Patriot ZMZ 409 engine diagram",
         "UAZ Patriot engine cross section",
         "схема двигателя УАЗ Патриот ЗМЗ 409",
         "UAZ Patriot engine scheme"
     ]},
    
    # Грузовики / дизель
    {"brand": "GAZ", "model": "Gazel_NEXT", "engine": "Cummins_2.8", "category": "truck",
     "queries": [
         "GAZelle NEXT Cummins 2.8 ISF engine diagram",
         "GAZon NEXT engine scheme",
         "схема двигателя ГАЗель NEXT Cummins 2.8 ISF",
         "ГАЗон NEXT схема двигателя"
     ]},
    {"brand": "KAMAZ", "model": "5490", "engine": "Cummins_ISG", "category": "truck",
     "queries": [
         "KAMAZ 5490 Cummins ISG engine diagram",
         "KAMAZ Euro 5 engine scheme",
         "схема двигателя КАМАЗ 5490 Cummins ISG",
         "КАМАЗ Евро-5 схема двигателя"
     ]},
    {"brand": "KAMAZ", "model": "65115", "engine": "Cummins_6.7", "category": "truck",
     "queries": [
         "KAMAZ 65115 Cummins 6.7 engine diagram",
         "KAMAZ Cummins engine scheme",
         "схема двигателя КАМАЗ 65115",
         "КАМАЗ Cummins 6.7 схема"
     ]},
    {"brand": "MMZ", "model": "D245", "engine": "diesel", "category": "truck",
     "queries": [
         "MMZ D-245 engine diagram",
         "D-245 diesel engine cross section",
         "схема двигателя ММЗ Д-245 разрез",
         "Д-245 Евро-3 схема двигателя"
     ]},
    {"brand": "MMZ", "model": "D260", "engine": "diesel", "category": "truck",
     "queries": [
         "MMZ D-260 engine diagram",
         "D-260 diesel engine scheme",
         "схема двигателя ММЗ Д-260",
         "Д-260 Евро-2 схема"
     ]},
    {"brand": "MAZ", "model": "4370", "engine": "YMZ_236", "category": "truck",
     "queries": [
         "MAZ 4370 YMZ 236 engine diagram",
         "YMZ 236 engine cross section",
         "схема двигателя МАЗ 4370 ЯМЗ 236",
         "ЯМЗ 236 схема разрез"
     ]},
    {"brand": "MAZ", "model": "5440", "engine": "YMZ_238", "category": "truck",
     "queries": [
         "MAZ 5440 YMZ 238 engine diagram",
         "YMZ 238 turbo engine scheme",
         "схема двигателя МАЗ 5440 ЯМЗ 238",
         "ЯМЗ 238 турбо схема"
     ]},
    {"brand": "Ural", "model": "4320", "engine": "YMZ_236", "category": "truck",
     "queries": [
         "Ural 4320 YMZ 236 engine diagram",
         "Ural truck engine scheme",
         "схема двигателя Урал 4320 ЯМЗ 236",
         "Урал схема двигателя"
     ]},
    
    # Автобусы
    {"brand": "PAZ", "model": "Vector", "engine": "MMZ_D245", "category": "bus",
     "queries": [
         "PAZ Vector MMZ D-245 engine diagram",
         "PAZ 3204 engine scheme",
         "схема двигателя ПАЗ Вектор ММЗ Д-245",
         "ПАЗ 3204 схема двигателя"
     ]},
    {"brand": "PAZ", "model": "3205", "engine": "ZMZ_5234", "category": "bus",
     "queries": [
         "PAZ 3205 ZMZ 5234 engine diagram",
         "PAZ 3205 engine scheme",
         "схема двигателя ПАЗ 3205 ЗМЗ 5234",
         "ПАЗ-3205 схема"
     ]},
    {"brand": "LiAZ", "model": "5292", "engine": "Cummins_6.7", "category": "bus",
     "queries": [
         "LiAZ 5292 Cummins engine diagram",
         "LiAZ bus engine scheme",
         "схема двигателя ЛиАЗ 5292 Cummins",
         "ЛиАЗ схема двигателя"
     ]},
    {"brand": "NefAZ", "model": "5299", "engine": "YMZ_536", "category": "bus",
     "queries": [
         "NefAZ 5299 YMZ 536 engine diagram",
         "NefAZ bus engine scheme",
         "схема двигателя НефАЗ 5299 ЯМЗ 536",
         "НефАЗ автобус схема двигателя"
     ]},
    
    # Спецтехника
    {"brand": "KAMAZ", "model": "6520", "engine": "Cummins_6.7", "category": "special",
     "queries": [
         "KAMAZ 6520 Cummins 6.7 engine diagram",
         "KAMAZ 6520 dump truck engine",
         "схема двигателя КАМАЗ 6520 самосвал",
         "КАМАЗ карьерный схема"
     ]},
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
    """Vision-валидация отключена: DeepSeek API не принимает изображения (400).
    Вместо неё работает эвристика validate_schema_image() в scrape_car.
    Чтобы вернуть контентную проверку — нужен провайдер с vision-моделью."""
    return True

# ═══ ПОИСК И СКАЧИВАНИЕ ═════════════════════════════════════════════

async def search_bing_images(query: str, limit: int = 5) -> List[Tuple[str, str]]:
    """Парсинг Bing Images. Возвращает пары (url картинки, url страницы-источника)."""
    from urllib.parse import quote_plus
    url = f"https://www.bing.com/images/search?q={quote_plus(query)}&first=1"
    
    headers = {
        "User-Agent": ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                      "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"),
        "Accept-Language": "ru-RU,ru;q=0.9",
    }
    
    try:
        async with httpx.AsyncClient(timeout=20, follow_redirects=True, trust_env=False) as client:
            resp = await client.get(url, headers=headers)
            resp.raise_for_status()
            html = resp.text
            
            # Метаданные результатов — JSON в атрибутах m="{...}" (HTML-encoded).
            # Берём только их: жадный regex по всему HTML выдёргивал картинки
            # из оформления страницы Bing (главный источник мусора).
            found = []  # (murl, purl)
            for block in re.findall(r'\bm="(\{&quot;.+?\})"', html):
                try:
                    meta = json.loads(unescape(block))
                except Exception:
                    continue
                murl = meta.get("murl", "")
                if murl.startswith("http"):
                    found.append((murl, meta.get("purl", "")))
            if not found:
                # Фолбэк: старый незакодированный формат murl
                found = [(u, "") for u in re.findall(
                    r'"murl"\s*:\s*"(https?://[^"]+)"', html, re.IGNORECASE)]
            
            valid = []
            seen = set()
            bad = ["/icon", "/logo", "/avatar", "/thumb", "favicon", "google", "bing", "yandex", "facebook", "twitter"]
            
            for u, page in found:
                low = u.lower()
                if any(b in low for b in bad):
                    continue
                if _is_junk_domain(u) or (page and _is_junk_domain(page)):
                    continue
                if not re.search(r'\.(jpg|jpeg|png|webp)(\?|$)', low):
                    continue
                if u not in seen:
                    seen.add(u)
                    valid.append((u, page))
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
        async with httpx.AsyncClient(timeout=30, follow_redirects=True, trust_env=False) as client:
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
            
        found = await search_bing_images(query, limit=max_images + 3)
        
        for url, page in found:
            if len(saved) >= max_images:
                break
            
            if not _is_relevant(url, page, query):
                logger.info(f"Rejected (off-topic): {url[:80]}")
                continue
                
            raw = await download_image(url)
            if not raw:
                continue
            
            if not validate_schema_image(raw):
                logger.info(f"Rejected (not schema-like): {url[:80]}")
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
