"""
AutoDiag AI v1.0.16 — Главный модуль
CarDiagnosticAI: ИИ-диагностика автомобилей OBD2
Версия: 1.0.16 (API compat: error_code aliases, GET /diagnose, await cache)
"""
import asyncio
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
from fastapi.middleware.cors import CORSMiddleware
from fastapi.middleware.trustedhost import TrustedHostMiddleware
from fastapi.responses import JSONResponse, Response
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


async def _run_diagnose(data: DiagnosisRequest, client_host: Optional[str]) -> dict:
    if _APP_TAMPER_MODE == "shutdown":
        raise HTTPException(status_code=503, detail="Сервис временно недоступен")
    if not check_ai_rate_limit(client_host or "unknown"):
        raise HTTPException(status_code=429, detail="Превышен лимит AI-запросов")

    # Важно: await — иначе coroutine всегда truthy и ответ падает с 500
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

    result = await lookup_error(data.code, brand=data.brand)
    if not isinstance(result, dict):
        result = {"code": data.code, "description": str(result)}

    await save_diagnosis(
        code=data.code,
        brand=data.brand,
        model=data.model,
        vin=data.vin,
        result=result,
        ip=client_host,
    )
    text = _format_diagnosis_text(data.code, data.brand, data.model, result)
    return {
        "source": "database",
        "error_code": data.code,
        "car_brand": data.brand,
        "car_model": data.model,
        "diagnosis": text,
        "result": result,
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
    yield
    logger.info("Завершение работы...")
    await db.close()
    logger.info("База данных закрыта")

app = FastAPI(
    title="AutoDiag AI", description="ИИ-диагностика автомобилей OBD2",
    version="1.0.16", lifespan=lifespan,
    docs_url="/docs" if os.getenv("ENVIRONMENT") != "production" else None,
    redoc_url="/redoc" if os.getenv("ENVIRONMENT") != "production" else None,
)
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

app.include_router(pricing_router, prefix="/pricing", tags=["pricing"])
app.include_router(license_router, prefix="/license", tags=["license"])
app.include_router(admin_router, prefix="/admin", tags=["admin"])

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
        return await _run_diagnose(data, request.client.host if request.client else None)
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Diagnosis GET error: {e}", exc_info=True)
        raise HTTPException(status_code=500, detail="Ошибка при диагностике")


@app.post("/diagnose", tags=["diagnosis"], response_model=dict)
@limiter.limit("10/minute")
async def diagnose(request: Request, data: DiagnosisRequest):
    try:
        return await _run_diagnose(data, request.client.host if request.client else None)
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
        data = await collector.get_pid(pid)
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
            svg = render_schema_svg(schema_id)
            return Response(content=svg, media_type="image/svg+xml")
        return get_schema_or_upgrade(schema_id)
    except Exception as e:
        logger.error(f"Schema error: {e}")
        raise HTTPException(status_code=404, detail="Схема не найдена")

@app.post("/schemas/refresh", tags=["schemas"])
async def refresh_schemas():
    try:
        result = refresh_all_schemas()
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

if __name__ == "__main__":
    import uvicorn
    port = int(os.getenv("PORT", 8000))
    uvicorn.run(app, host="0.0.0.0", port=port)
