"""
AutoDiag AI v1.0.16 — Главный модуль
CarDiagnosticAI: ИИ-диагностика автомобилей OBD2
Версия: 1.0.16 (API compat: error_code aliases, GET /diagnose, await cache)
"""
import asyncio
import hmac
import json
import logging
import os
import sys
import time
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from typing import Optional, List

import httpx
from fastapi import FastAPI, HTTPException, Query, Request, status
from starlette.staticfiles import StaticFiles
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.trustedhost import TrustedHostMiddleware
from fastapi.responses import JSONResponse, Response
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel, field_validator, Field, ConfigDict
from starlette.exceptions import HTTPException as StarletteHTTPException
from fastapi.exceptions import RequestValidationError
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
    handlers=[logging.StreamHandler(sys.stdout)]
)
logger = logging.getLogger("autodiag")

import database as db
from database import (
    lookup_error, lookup_errors_batch, save_diagnosis, get_all_history,
    get_error_stats, save_historical_code, get_historical_codes,
    get_user_tier, get_user_features, lookup_ai_cache, save_ai_cache,
    check_ai_rate_limit, get_ai_rate_limit_remaining,
)
from elm327 import SimulatedELM327
from simulator import SimulatorState, RUSSIAN_CARS
from chroma_memory import chroma
from live import collector
from schemas import (
    _SCHEMAS, get_schema as get_schema_data, get_schema_or_upgrade,
    list_available_schemas, render_schema_svg, downloader_get_schema,
    get_download_stats, refresh_all_schemas,
)
from sync import cloud
from pricing import router as pricing_router, require_feature, is_paid, get_paid_features
from license import router as license_router
from admin import router as admin_router
from schema_image_scraper import run_full_scrape, AUTO_REGISTRY
from dtc import get_code as dtc_get_code, search_codes as dtc_search_codes, stats as dtc_stats
from deepseek_client import build_system_prompt as _ds_build_system_prompt
import integrity
from device import get_device_id, verify_device_binding

_APP_COMPROMISED = False
_APP_TAMPER_MODE = "normal"
limiter = Limiter(key_func=get_remote_address)

class DiagnosisRequest(BaseModel):
    """Принимает и новые поля (code/brand/model), и старые (error_code/car_brand/car_model)."""
    model_config = ConfigDict(populate_by_name=True, extra="ignore")

    code: str = Field(..., min_length=1, max_length=10, alias="error_code")
    brand: Optional[str] = Field(None, max_length=50, alias="car_brand")
    model: Optional[str] = Field(None, max_length=100, alias="car_model")
    year: Optional[int] = Field(None, ge=1900, le=2030)
    vin: Optional[str] = Field(None, max_length=17)
    context: Optional[str] = Field(None, max_length=8000)

    @field_validator('vin')
    @classmethod
    def validate_vin(cls, v):
        if v is None: return v
        v = v.upper().strip()
        if len(v) != 17: raise ValueError('VIN должен содержать 17 символов')
        if not all(c.isalnum() and c not in 'IOQ' for c in v): raise ValueError('VIN содержит недопустимые символы')
        return v

    @field_validator('code')
    @classmethod
    def validate_code(cls, v):
        return v.upper().strip()


def _format_diagnosis_text(code: str, brand: Optional[str], model: Optional[str], result: dict) -> str:
    """Текстовый диагноз для Android/Windows-клиента (не сырой JSON)."""
    car = " ".join(x for x in (brand or "", model or "") if x).strip() or "автомобиль"
    desc = (result.get("description") or result.get("diagnosis") or "").strip()
    if not desc:
        desc = f"Код {code}: требуется диагностика по симптомам."

    def _as_list(val) -> list:
        if val is None:
            return []
        if isinstance(val, list):
            return [str(x) for x in val if x]
        if isinstance(val, str):
            try:
                parsed = json.loads(val)
                if isinstance(parsed, list):
                    return [str(x) for x in parsed if x]
            except Exception:
                pass
            return [val] if val.strip() else []
        return [str(val)]

    causes = _as_list(result.get("causes"))
    solutions = _as_list(result.get("solutions") or result.get("recommendations"))
    severity = (result.get("severity") or "").strip()

    lines = [
        f"ОБЩАЯ ОЦЕНКА",
        f"Авто: {car}",
        f"Код: {code}",
    ]
    if severity:
        lines.append(f"Критичность: {severity}")
    lines += ["", "ОПИСАНИЕ", desc]
    if causes:
        lines += ["", "ВЕРОЯТНЫЕ ПРИЧИНЫ"]
        lines += [f"• {c}" for c in causes[:12]]
    if solutions:
        lines += ["", "РЕКОМЕНДАЦИИ"]
        lines += [f"• {s}" for s in solutions[:12]]
    lines += [
        "",
        "🟢 База сервера AutoDiag. При отсутствии интернета клиент использует офлайн-справочник.",
    ]
    return "\n".join(lines)


async def _run_diagnose(data: DiagnosisRequest, client_host: Optional[str], user_tier: str = "free") -> dict:
    if _APP_TAMPER_MODE == "shutdown":
        raise HTTPException(status_code=503, detail="Сервис временно недоступен")
    if not check_ai_rate_limit(client_host or "unknown"):
        raise HTTPException(status_code=429, detail="Превышен лимит AI-запросов")

    # 1) AI-кеш (доступен всем — уже оплачен ранее)
    cached = await lookup_ai_cache(data.code, data.brand)
    if cached:
        text = _format_diagnosis_text(data.code, data.brand, data.model, cached if isinstance(cached, dict) else {"description": str(cached)})
        return {
            "source": "cache",
            "error_code": data.code,
            "car_brand": data.brand,
            "car_model": data.model,
            "diagnosis": text,
            "result": cached,
        }

    # 2) Локальная база (доступна всем — Free)
    local_result = await lookup_error(data.code, brand=data.brand)
    if not isinstance(local_result, dict):
        local_result = {"code": data.code, "description": str(local_result)}

    # 3) DeepSeek AI-диагностика — ТОЛЬКО Pro/Enterprise
    ai_result = None
    if user_tier in ("pro", "enterprise"):
        try:
            from deepseek_client import diagnose_with_deepseek
            ai_result = await diagnose_with_deepseek(
                code=data.code,
                brand=data.brand,
                model=data.model,
                year=data.year,
                vin=data.vin,
                context=data.context,
                local_data=local_result if local_result.get("description") else None,
            )
        except Exception as e:
            logger.warning(f"DeepSeek недоступен, использую локальную базу: {e}")

        if ai_result:
            try:
                await save_ai_cache(data.code, data.brand, ai_result)
            except Exception as e:
                logger.warning(f"Не удалось сохранить AI-кеш: {e}")

            await save_diagnosis(
                code=data.code,
                brand=data.brand,
                model=data.model,
                vin=data.vin,
                result=ai_result,
                ip=client_host,
            )
            text = _format_diagnosis_text(data.code, data.brand, data.model, ai_result)
            return {
                "source": "ai",
                "error_code": data.code,
                "car_brand": data.brand,
                "car_model": data.model,
                "diagnosis": text,
                "result": ai_result,
            }
    # 4) Fallback — локальная база (Free)
    # Копируем результат чтобы не модифицировать оригинал в кеше
    result_for_client = dict(local_result)
    if user_tier not in ("pro", "enterprise"):
        result_for_client["_note"] = "AI-диагностика доступна в версии Pro (1 499 ₽ навсегда). Умный анализ через DeepSeek."

    await save_diagnosis(
        code=data.code,
        brand=data.brand,
        model=data.model,
        vin=data.vin,
        result=local_result,  # чистый результат в базу
        ip=client_host,
    )
    text = _format_diagnosis_text(data.code, data.brand, data.model, result_for_client)
    return {
        "source": "database",
        "error_code": data.code,
        "car_brand": data.brand,
        "car_model": data.model,
        "diagnosis": text,
        "result": result_for_client,
    }

class BatchRequest(BaseModel):
    codes: List[str] = Field(..., min_length=1, max_length=50)
    brand: Optional[str] = Field(None, max_length=50)

    @field_validator('codes')
    @classmethod
    def validate_codes(cls, v):
        return [c.upper().strip() for c in v if c.strip()]

class SyncRequest(BaseModel):
    device_id: str = Field(..., min_length=8, max_length=128)
    data: dict = Field(default_factory=dict)
    version: str = Field(default="1.0.16", max_length=20)

@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("AutoDiag AI v1.0.16 запускается...")
    global _APP_COMPROMISED, _APP_TAMPER_MODE
    try:
        integrity_result = integrity.verify()
        if not integrity_result.ok:
            _APP_COMPROMISED = True
            _APP_TAMPER_MODE = integrity_result.mode
            logger.warning(f"Целостность нарушена: {integrity_result.mode}")
    except Exception as e:
        logger.error(f"Ошибка проверки целостности: {e}")
    try:
        await db.init()
        logger.info("База данных инициализирована")
    except Exception as e:
        logger.error(f"Ошибка инициализации БД: {e}")
        raise
    try:
        if chroma: logger.info("ChromaDB инициализирована")
    except Exception as e:
        logger.warning(f"ChromaDB недоступен: {e}")

    # Запуск фонового авто-обновления (weekly agent)
    try:
        import weekly_agent
        asyncio.create_task(_run_weekly_agent_loop())
        logger.info("Фоновое авто-обновление запущено (интервал: 14 дней)")
    except Exception as e:
        logger.warning(f"Не удалось запустить авто-обновление: {e}")

    yield
    logger.info("Завершение работы...")
    await db.close()
    logger.info("База данных закрыта")


async def _run_weekly_agent_loop():
    """Фоновый цикл: запускает weekly_agent раз в 14 дней."""
    await asyncio.sleep(60)  # Подождём минуту после старта сервера
    while True:
        try:
            import weekly_agent
            if weekly_agent.should_run():
                logger.info("[AutoUpdate] Запуск авто-обновления базы...")
                await weekly_agent.run()
                logger.info("[AutoUpdate] Авто-обновление завершено")
            else:
                logger.info("[AutoUpdate] Следующий запуск позже (14 дней интервал)")
        except Exception as e:
            logger.error(f"[AutoUpdate] Ошибка: {e}")
        # Спим 24 часа, потом проверяем снова
        await asyncio.sleep(24 * 3600)

app = FastAPI(
    title="AutoDiag AI", description="ИИ-диагностика автомобилей OBD2",
    version="1.0.16", lifespan=lifespan,
    docs_url="/docs" if os.getenv("ENVIRONMENT") != "production" else None,
    redoc_url="/redoc" if os.getenv("ENVIRONMENT") != "production" else None,
)

# Раздача статических схем (PNG/JPG)
app.mount("/schema_images", StaticFiles(directory="schema_images"), name="schema_images")
app.state.limiter = limiter
app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)

CORS_ORIGINS = os.getenv("CORS_ORIGINS", "http://localhost:3000,http://localhost:8080").split(",")
CORS_ORIGINS = [o.strip() for o in CORS_ORIGINS if o.strip()]

app.add_middleware(
    CORSMiddleware,
    allow_origins=CORS_ORIGINS,
    allow_credentials=True,
    allow_methods=["GET", "POST", "PUT", "DELETE"],
    allow_headers=["*"],
    max_age=3600,
)
app.add_middleware(TrustedHostMiddleware, allowed_hosts=["*"])

@app.middleware("http")
async def add_security_headers(request: Request, call_next):
    response = await call_next(request)
    response.headers["X-Content-Type-Options"] = "nosniff"
    response.headers["X-Frame-Options"] = "DENY"
    response.headers["X-XSS-Protection"] = "1; mode=block"
    response.headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains"
    response.headers["Referrer-Policy"] = "strict-origin-when-cross-origin"
    return response

@app.middleware("http")
async def log_requests(request: Request, call_next):
    start = time.time()
    response = await call_next(request)
    logger.info(f"{request.method} {request.url.path} — {response.status_code} ({time.time()-start:.3f}s)")
    return response

app.include_router(pricing_router, tags=["pricing"])
app.include_router(license_router, tags=["license"])
app.include_router(admin_router, tags=["admin"])

@app.get("/health", tags=["health"])
async def health_check():
    db_status = "ok"
    try: await db.ping()
    except: db_status = "error"
    return {
        "status": "healthy" if db_status == "ok" else "degraded",
        "version": "1.0.16", "timestamp": datetime.now(timezone.utc).isoformat(),
        "database": db_status, "compromised": _APP_COMPROMISED,
        "tamper_mode": _APP_TAMPER_MODE,
        "environment": os.getenv("ENVIRONMENT", "development")
    }

@app.exception_handler(StarletteHTTPException)
async def http_exception_handler(request: Request, exc: StarletteHTTPException):
    logger.warning(f"HTTP {exc.status_code}: {exc.detail}")
    return JSONResponse(status_code=exc.status_code, content={"detail": exc.detail, "status_code": exc.status_code})

@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    logger.warning(f"Validation error: {exc.errors()}")
    return JSONResponse(status_code=status.HTTP_422_UNPROCESSABLE_ENTITY, content={"detail": "Ошибка валидации данных", "errors": exc.errors()})

@app.exception_handler(Exception)
async def general_exception_handler(request: Request, exc: Exception):
    logger.error(f"Unhandled exception: {str(exc)}", exc_info=True)
    return JSONResponse(status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, content={"detail": "Внутренняя ошибка сервера"})

@app.get("/diagnose", tags=["diagnosis"], response_model=dict)
@limiter.limit("10/minute")
async def diagnose_get(
    request: Request,
    error_code: str = Query(default="", alias="error_code"),
    car_brand: Optional[str] = Query(default=None, alias="car_brand"),
    car_model: Optional[str] = Query(default=None, alias="car_model"),
    code: str = Query(default=""),
    brand: Optional[str] = Query(default=None),
    model: Optional[str] = Query(default=None),
    context: Optional[str] = Query(default=None),
):
    """GET для клиента / WAF-обхода: ?error_code=P0301&car_brand=ВАЗ&car_model=Granta"""
    try:
        data = DiagnosisRequest(
            code=(code or error_code or "").strip(),
            brand=brand or car_brand,
            model=model or car_model,
            context=context,
        )
        # Определяем tier пользователя (пока по заголовку, позже по JWT)
        user_tier = request.headers.get("X-User-Tier", "free")
        return await _run_diagnose(data, request.client.host if request.client else None, user_tier)
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Diagnosis GET error: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail="Ошибка при диагностике")


@app.post("/diagnose", tags=["diagnosis"], response_model=dict)
@limiter.limit("10/minute")
async def diagnose(request: Request, data: DiagnosisRequest):
    try:
        user_tier = request.headers.get("X-User-Tier", "free")
        return await _run_diagnose(data, request.client.host if request.client else None, user_tier)
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Diagnosis error: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail="Ошибка при диагностике")

@app.post("/diagnose/batch", tags=["diagnosis"], response_model=dict)
@limiter.limit("5/minute")
async def diagnose_batch(request: Request, data: BatchRequest):
    try:
        results = await lookup_errors_batch(data.codes, brand=data.brand)
        return {"results": results, "count": len(results)}
    except Exception as e:
        logger.error(f"Batch error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка пакетной обработки")

@app.get("/history", tags=["history"], response_model=dict)
@limiter.limit("30/minute")
async def get_diagnosis_history(request: Request, limit: int = Query(50, ge=1, le=200), offset: int = Query(0, ge=0)):
    try:
        items = await get_all_history(limit=limit, offset=offset)
        return {"items": items, "limit": limit, "offset": offset}
    except Exception as e:
        logger.error(f"History error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка получения истории")

@app.get("/stats", tags=["statistics"], response_model=dict)
async def get_statistics():
    try: return await get_error_stats()
    except Exception as e:
        logger.error(f"Stats error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка получения статистики")

@app.get("/live/{pid}", tags=["live"], response_model=dict)
async def get_live_data(pid: str, device_id: Optional[str] = None):
    try:
        if device_id: verify_device_binding(device_id)
        # Метода get_pid у коллектора нет — берём историю и вытаскиваем ряд по PID
        history = collector.get_history(limit=100)
        data = [{"timestamp": s["timestamp"], "value": s.get(pid)} for s in history]
        return {"pid": pid, "data": data, "timestamp": datetime.now(timezone.utc).isoformat()}
    except Exception as e:
        logger.error(f"Live data error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка получения live данных")

@app.get("/schemas", tags=["schemas"], response_model=dict)
async def list_schemas():
    return {"schemas": list_available_schemas(), "count": len(list_available_schemas())}

@app.get("/schemas/{schema_id}", tags=["schemas"])
async def get_schema(schema_id: str, format: str = Query("json", enum=["json", "svg"])):
    try:
        if format == "svg":
            schema = get_schema_data(schema_id)
            if not schema:
                raise HTTPException(status_code=404, detail="Схема не найдена")
            svg = render_schema_svg(schema_id, schema)
            return Response(content=svg, media_type="image/svg+xml")
        return get_schema_or_upgrade(schema_id)
    except Exception as e:
        logger.error(f"Schema error: {e}")
        raise HTTPException(status_code=404, detail="Схема не найдена")

@app.post("/schemas/refresh", tags=["schemas"])
async def refresh_schemas(admin_key: str = Query("")):
    expected = os.getenv("ADMIN_KEY", "")
    if not expected or not hmac.compare_digest(admin_key or "", expected):
        raise HTTPException(status_code=403, detail="Forbidden")
    try:
        result = await refresh_all_schemas(_SCHEMAS)
        return {"refreshed": result}
    except Exception as e:
        logger.error(f"Refresh error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка обновления схем")

@app.post("/sync", tags=["sync"], response_model=dict)
@limiter.limit("20/minute")
async def sync_data(request: Request, data: SyncRequest):
    try:
        result = await cloud.sync(data.data, device_id=data.device_id, version=data.version)
        return {"synced": True, "result": result}
    except Exception as e:
        logger.error(f"Sync error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка синхронизации")

@app.get("/version", tags=["version"], response_model=dict)
async def get_version():
    return {
        "version": "1.0.16", "min_app_version": "1.0.10",
        "latest_apk_url": os.getenv("LATEST_APK_URL", ""),
        "changelog": ["Исправлены SQL-инъекции", "Добавлен rate limiting", "Улучшена безопасность CORS", "Добавлен health check", "Исправлены утечки памяти"]
    }

@app.get("/search", tags=["search"], response_model=dict)
@limiter.limit("15/minute")
async def search_cars(request: Request, q: str = Query(..., min_length=2, max_length=100), brand: Optional[str] = Query(None, max_length=50), limit: int = Query(20, ge=1, le=100)):
    try:
        results = await db.search_cars(query=q, brand=brand, limit=limit)
        return {"query": q, "results": results, "count": len(results)}
    except Exception as e:
        logger.error(f"Search error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка поиска")

# ═══ DTC-справочник (12 000+ кодов OBD-II, MIT + русская надстройка) ════
@app.get("/dtc/stats", tags=["dtc"], response_model=dict)
async def dtc_statistics():
    """Статистика справочника кодов ошибок."""
    return dtc_stats()

@app.get("/dtc/search", tags=["dtc"], response_model=dict)
async def dtc_search(q: str = Query(..., min_length=2, max_length=50), limit: int = Query(20, ge=1, le=100)):
    """Поиск по кодам ошибок (по коду или тексту описания)."""
    return {"query": q, "results": dtc_search_codes(q, limit)}

@app.get("/dtc/{code}", tags=["dtc"], response_model=dict)
async def dtc_lookup(code: str, manufacturer: Optional[str] = Query(None, max_length=30)):
    """Расшифровка кода: русское описание (если есть), английское, причины и решения."""
    result = dtc_get_code(code, manufacturer)
    if not result:
        raise HTTPException(status_code=404, detail="Код не найден")
    return result

@app.post("/admin/schemas/scrape", tags=["admin"])
@limiter.limit("3/minute")
async def admin_scrape_schemas(request: Request, admin_key: str = Query(...)):
    expected = os.getenv("ADMIN_KEY", "")
    if not expected or not hmac.compare_digest(admin_key or "", expected):
        raise HTTPException(status_code=403, detail="Forbidden")
    asyncio.create_task(run_full_scrape(AUTO_REGISTRY))
    return {"status": "started", "message": "Scraping started in background. Check schema_images/ in ~5 min."}

# ═══ AI-анализ графиков (сравнение с эталоном) ═══════════════════════
class GraphDeviationItem(BaseModel):
    pid_hex: str
    pid_name: str
    unit: str
    actual_value: float
    reference_min: float
    reference_max: float
    status: str  # "warning" | "critical"
    mode: str
    deviation_percent: float

class GraphAnalysisRequest(BaseModel):
    brand: str = Field(..., min_length=1, max_length=50)
    model: str = Field(..., min_length=1, max_length=100)
    vin: Optional[str] = Field(None, max_length=17)
    deviations: List[GraphDeviationItem] = Field(..., min_length=1, max_length=20)

    @field_validator('vin')
    @classmethod
    def validate_vin(cls, v):
        if v is None: return v
        v = v.upper().strip()
        if len(v) != 17: raise ValueError('VIN должен содержать 17 символов')
        if not all(c.isalnum() and c not in 'IOQ' for c in v): raise ValueError('VIN содержит недопустимые символы')
        return v


def _build_graph_analysis_prompt(brand: str, model: str, deviations: List[GraphDeviationItem]) -> str:
    """Формирует промпт для DeepSeek на основе отклонений PID."""
    lines = [
        f"Автомобиль: {brand} {model}",
        f"Режим работы: {deviations[0].mode if deviations else 'неизвестен'}",
        "",
        "ОТКЛОНЕНИЯ ПАРАМЕТРОВ (сравнение с эталонными значениями):",
    ]
    for d in deviations:
        status_ru = "КРИТИЧНО" if d.status == "critical" else "ВНИМАНИЕ"
        lines.append(
            f"• {d.pid_name} (PID {d.pid_hex}): "
            f"фактическое {d.actual_value:.2f} {d.unit}, "
            f"эталон {d.reference_min:.2f}–{d.reference_max:.2f} {d.unit} "
            f"[{status_ru}, отклонение {d.deviation_percent:+.1f}%]"
        )
    lines += [
        "",
        "Дай диагностический анализ строго по формату:",
        "1. ОБЩАЯ ОЦЕНКА — что означают эти отклонения в совокупности (1-2 предложения)",
        "2. ВЕРОЯТНЫЕ ПРИЧИНЫ — список из 3-5 пунктов, отсортированных по вероятности",
        "3. РЕКОМЕНДАЦИИ — что проверить и в каком порядке (3-5 пунктов)",
        "4. КРИТИЧНОСТЬ — одно из: НИЗКАЯ / СРЕДНЯЯ / ВЫСОКАЯ / КРИТИЧЕСКАЯ",
        "5. МОЖНО ЛИ ЕХАТЬ — Да / Осторожно / Нет (с пояснением)",
        "",
        "ВАЖНО: отвечай ТОЛЬКО на основе фактов. Если не уверен — скажи 'Недостаточно данных'.",
        "НЕ выдумывай коды ошибок или детали. Указывай источник: 'На основе данных OBD2'.",
    ]
    return "\n".join(lines)


def _parse_graph_analysis_response(content: str) -> dict:
    """Парсит ответ DeepSeek для анализа графиков."""
    result = {
        "summary": "",
        "possible_causes": [],
        "recommendations": [],
        "severity": "СРЕДНЯЯ",
        "can_drive": "Осторожно",
    }
    lines = content.split("\n")
    current_section = None

    for line in lines:
        line = line.strip()
        if not line:
            continue
        upper = line.upper()
        if "ОБЩАЯ ОЦЕНКА" in upper or "ОПИСАНИЕ" in upper:
            current_section = "summary"
            continue
        elif "ПРИЧИН" in upper:
            current_section = "causes"
            continue
        elif "РЕКОМЕНДАЦИ" in upper or "РЕШЕНИ" in upper:
            current_section = "recommendations"
            continue
        elif "КРИТИЧНОСТЬ" in upper:
            for level in ["КРИТИЧЕСКАЯ", "ВЫСОКАЯ", "СРЕДНЯЯ", "НИЗКАЯ"]:
                if level in upper:
                    result["severity"] = level
                    break
            current_section = None
            continue
        elif "МОЖНО ЛИ ЕХАТЬ" in upper or "ЕХАТЬ" in upper:
            for val in ["Нет", "Осторожно", "Да"]:
                if val in line:
                    result["can_drive"] = val
                    break
            current_section = None
            continue

        if current_section == "summary":
            result["summary"] += line + " "
        elif current_section == "causes" and line.startswith(("•", "-", "*", "1.", "2.", "3.", "4.", "5.")):
            result["possible_causes"].append(line.lstrip("•-* 1234567890.").strip())
        elif current_section == "recommendations" and line.startswith(("•", "-", "*", "1.", "2.", "3.", "4.", "5.")):
            result["recommendations"].append(line.lstrip("•-* 1234567890.").strip())

    result["summary"] = result["summary"].strip()
    if not result["summary"] and not result["possible_causes"]:
        result["summary"] = content[:500]

    return result


@app.post("/analyze/graph", tags=["analysis"])
@limiter.limit("5/minute")
async def analyze_graph_deviations(request: Request, data: GraphAnalysisRequest):
    """
    AI-анализ отклонений графиков OBD2.
    Принимает список отклонений PID, возвращает диагностику через DeepSeek.
    Доступно только для Pro/Enterprise.
    """
    # Определяем tier по заголовку
    user_tier = "free"
    try:
        tier_header = request.headers.get("X-User-Tier", "free")
        if tier_header in ("pro", "enterprise"):
            user_tier = tier_header
    except:
        pass

    if user_tier not in ("pro", "enterprise"):
        raise HTTPException(
            status_code=403,
            detail="AI-анализ графиков доступен только в версии Pro."
        )

    if not check_ai_rate_limit(request.client.host or "unknown"):
        raise HTTPException(status_code=429, detail="Превышен лимит AI-запросов")

    # Проверяем ключ DeepSeek
    if not os.getenv("DEEPSEEK_API_KEY"):
        return {
            "source": "local",
            "summary": "AI-анализ недоступен: серверный ключ DeepSeek не настроен.",
            "possible_causes": ["Проверьте настройки сервера"],
            "recommendations": ["Обратитесь к администратору"],
            "severity": "НЕИЗВЕСТНО",
            "can_drive": "Осторожно",
        }

    try:
        from deepseek_client import get_model_and_timeout
        model, timeout = get_model_and_timeout()

        prompt = _build_graph_analysis_prompt(data.brand, data.model, data.deviations)

        import httpx
        payload = {
            "model": model,
            "messages": [
                {"role": "system", "content": _ds_build_system_prompt()},  # reuse existing
                {"role": "user", "content": prompt},
            ],
            "temperature": 0.3,
            "max_tokens": 1500,
            "stream": False,
        }
        headers = {
            "Authorization": f"Bearer {os.getenv('DEEPSEEK_API_KEY')}",
            "Content-Type": "application/json",
        }

        async with httpx.AsyncClient(timeout=timeout) as client:
            response = await client.post(
                "https://api.deepseek.com/chat/completions",
                json=payload,
                headers=headers
            )
            response.raise_for_status()
            resp_data = response.json()

            if "choices" not in resp_data or not resp_data["choices"]:
                raise HTTPException(status_code=502, detail="DeepSeek вернул пустой ответ")

            content = resp_data["choices"][0]["message"]["content"]
            result = _parse_graph_analysis_response(content)
            result["source"] = "ai"
            result["_model"] = model
            result["_tokens"] = resp_data.get("usage", {}).get("total_tokens", 0)

            return result

    except httpx.HTTPStatusError as e:
        logger.error(f"DeepSeek HTTP error: {e.response.status_code}")
        raise HTTPException(status_code=502, detail="Ошибка связи с AI-сервисом")
    except Exception as e:
        logger.error(f"Graph analysis error: {e}")
        # Fallback: локальный анализ
        return {
            "source": "local",
            "summary": f"Обнаружены отклонения в {len(data.deviations)} параметрах. "
                       f"Рекомендуется диагностика специалистом.",
            "possible_causes": [f"{d.pid_name}: отклонение {d.deviation_percent:+.1f}%" for d in data.deviations[:5]],
            "recommendations": [
                "Проверьте указанные датчики и системы",
                "Выполните компьютерную диагностику",
                "При критичных отклонениях — не эксплуатируйте авто",
            ],
            "severity": "КРИТИЧЕСКАЯ" if any(d.status == "critical" for d in data.deviations) else "ВЫСОКАЯ",
            "can_drive": "Нет" if any(d.status == "critical" for d in data.deviations) else "Осторожно",
        }


@app.post("/admin/update-db", tags=["admin"])
@limiter.limit("3/minute")
async def admin_update_db(request: Request, admin_key: str = Query(...), force: bool = Query(False)):
    """Ручной запуск авто-обновления базы ошибок (weekly agent)."""
    expected = os.getenv("ADMIN_KEY", "")
    if not expected or not hmac.compare_digest(admin_key or "", expected):
        raise HTTPException(status_code=403, detail="Forbidden")
    try:
        import weekly_agent
        if force or weekly_agent.should_run():
            asyncio.create_task(weekly_agent.run())
            return {"status": "started", "message": "Database update started in background."}
        return {"status": "skipped", "message": "Too soon (14 days interval). Use force=true to override."}
    except Exception as e:
        logger.error(f"Update DB error: {e}")
        raise HTTPException(status_code=500, detail="Ошибка запуска обновления")

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", 8000))
    uvicorn.run(app, host="0.0.0.0", port=port)
