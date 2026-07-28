"""
AutoDiag AI v1.0.13 — Главный модуль (исправленный)
CarDiagnosticAI: ИИ-диагностика автомобилей с поддержкой ELM327,
офлайн-базы SQLite, самообучения ChromaDB и облачной синхронизации.
Версия: 1.0.13 (Security & Stability fix)
"""
import asyncio
import json
import logging
import os
import sys
import threading
import time
from datetime import datetime, timezone
from typing import Optional

import httpx
from fastapi import FastAPI, HTTPException, Query, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse, Response
from pydantic import BaseModel, field_validator
from starlette.exceptions import HTTPException as StarletteHTTPException
from fastapi.exceptions import RequestValidationError

# ==================== Собственные модули ====================
import database as db
from database import (
    lookup_error,
    lookup_errors_batch,
    save_diagnosis,
    get_history,
    get_all_history,
    get_error_stats,
    save_historical_code,
    get_historical_codes,
    get_user_tier,
    get_user_features,
    lookup_ai_cache,
    save_ai_cache,
    check_ai_rate_limit,
    get_ai_rate_limit_remaining,
)
from elm327 import SimulatedELM327
from simulator import SimulatorState, RUSSIAN_CARS
from chroma_memory import chroma
from live import collector
from schemas import (
    _SCHEMAS,
    get_schema as get_schema_data,
    get_schema_or_upgrade,
    list_available_schemas,
    render_schema_svg,
    downloader_get_schema,
    get_download_stats,
    refresh_all_schemas,
)
from sync import cloud
from pricing import router as pricing_router, require_feature, is_paid, get_paid_features
from license import router as license_router
from admin import router as admin_router

# ==================== Защита от взлома ====================
import integrity
from device import get_device_id, verify_device_binding

_APP_COMPROMISED = False
_APP_TAMPER_MODE = "normal"  # normal | free_only | shutdown

# ==================== Защита ====================
from security import (
    SecurityHeadersMiddleware,
    BodySizeMiddleware,
    CloudflareMiddleware,
    WAFBypassMiddleware,
    DiagnoseWAFShield,
    general_limiter,
    ai_limiter,
    auth_limiter,
    download_limiter,
    sanitize_error_code,
    sanitize_vin,
    sanitize_car_brand,
    sanitize_user_id,
    sanitize_text,
    safe_error_message,
    log_request,
    get_cors_origins,
    detect_debugger,
)

# ==================== Обновления ====================
from updater import POLL_INTERVAL, UPDATE_SERVER, start_polling

# ==================== Фоновый агент ====================
from weekly_agent import MIN_RUN_INTERVAL

logger = logging.getLogger("autodiag")


# ==================== Глобальный симулятор ====================
class _SimRef:
    def __init__(self):
        self._inst = SimulatorState()
        self._lock = threading.Lock()

    def get(self):
        with self._lock:
            return self._inst

    def set(self, inst):
        with self._lock:
            self._inst = inst


sim_ref = _SimRef()


# ==================== Вспомогательные функции ====================
def _require_enterprise(user_id: str, feature: str = "basic_simulator"):
    """Требовать Enterprise-подписку. 402 при несоответствии."""
    if _APP_COMPROMISED:
        raise HTTPException(
            status_code=402,
            detail={
                "error": "integrity_failure",
                "feature": feature,
                "message": "Целостность приложения нарушена. Платные функции недоступны.",
            },
        )
    tier = get_user_tier(user_id)
    if tier != "enterprise":
        raise HTTPException(
            status_code=402,
            detail={
                "error": "payment_required",
                "feature": feature,
                "message": "Функция доступна только в версии Enterprise (1 990 ₽/мес).",
            },
        )


def _require_paid(user_id: str):
    """Требовать платную подписку (Pro или Enterprise)."""
    if _APP_COMPROMISED:
        raise HTTPException(
            status_code=402,
            detail={
                "error": "integrity_failure",
                "message": "Целостность приложения нарушена. Платная подписка недоступна.",
            },
        )
    if not is_paid(user_id):
        raise HTTPException(
            status_code=402,
            detail={
                "error": "payment_required",
                "message": "Требуется платная подписка (Pro или Enterprise).",
            },
        )


def _get_device_id_safe() -> str:
    try:
        return get_device_id()
    except Exception:
        return "unavailable"


def _run_sync(func, *args, **kwargs):
    """Запустить синхронную функцию в executor, чтобы не блокировать event loop."""
    loop = asyncio.get_running_loop()
    return loop.run_in_executor(None, lambda: func(*args, **kwargs))


class UTF8JSONResponse(JSONResponse):
    """JSONResponse с явным charset=utf-8."""

    media_type = "application/json; charset=utf-8"


# ==================== FastAPI App ====================
APP_VERSION = "1.0.13"

app = FastAPI(
    title="AutoDiag AI",
    description="ИИ-диагностика автомобилей. ELM327 + DeepSeek + ChromaDB + Облако.",
    version=APP_VERSION,
    default_response_class=UTF8JSONResponse,
)

# CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=get_cors_origins(),
    allow_credentials=True,
    allow_methods=["GET", "POST", "OPTIONS"],
    allow_headers=[
        "Content-Type",
        "Authorization",
        "X-Request-ID",
        "X-Timestamp",
        "X-Signature",
    ],
    max_age=600,
)
app.add_middleware(CloudflareMiddleware)
app.add_middleware(DiagnoseWAFShield)
app.add_middleware(WAFBypassMiddleware)
app.add_middleware(SecurityHeadersMiddleware)
app.add_middleware(BodySizeMiddleware)


# ==================== Exception Handlers ====================
@app.exception_handler(StarletteHTTPException)
async def http_exception_handler(request: Request, exc: StarletteHTTPException):
    if exc.status_code == 403:
        return UTF8JSONResponse(
            status_code=403,
            content={
                "error": "forbidden",
                "detail": str(exc.detail) if exc.detail else "Доступ запрещён",
                "hint": "Используйте GET /diagnose?error_code=...&car_brand=... вместо POST.",
                "cf_ray": request.headers.get("CF-Ray", ""),
            },
            headers={
                "X-Content-Type-Options": "nosniff",
                "Cache-Control": "no-store",
            },
        )
    if exc.status_code == 429:
        retry_after = 60
        if exc.detail and isinstance(exc.detail, dict):
            retry_after = exc.detail.get("retry_after", 60)
        return UTF8JSONResponse(
            status_code=429,
            content={
                "error": "rate_limited",
                "detail": str(exc.detail) if exc.detail else "Слишком много запросов",
                "retry_after_seconds": retry_after,
            },
            headers={
                "Retry-After": str(retry_after),
                "X-RateLimit-Reset": str(int(time.time() + retry_after)),
            },
        )
    if exc.status_code == 402:
        return UTF8JSONResponse(
            status_code=402,
            content=exc.detail
            if isinstance(exc.detail, dict)
            else {
                "error": "payment_required",
                "detail": str(exc.detail) if exc.detail else "Требуется платная подписка",
            },
            headers={"X-Upgrade-URL": "/pricing/plans"},
        )
    return UTF8JSONResponse(
        status_code=exc.status_code,
        content=exc.detail if isinstance(exc.detail, dict) else {"error": str(exc.detail)},
    )


@app.exception_handler(RequestValidationError)
async def validation_exception_handler(request: Request, exc: RequestValidationError):
    path = request.url.path.rstrip("/")
    params = getattr(request.state, "diagnose_params", None)
    if path == "/diagnose" and params and params.get("error_code"):
        error_code = sanitize_error_code(params.get("error_code", ""))
        car_brand = sanitize_car_brand(params.get("car_brand", ""))
        car_model = sanitize_text(params.get("car_model", ""), 200) or ""
        vin = sanitize_vin(params.get("vin", "")) if params.get("vin") else ""
        user_id = sanitize_user_id(params.get("user_id", "anonymous"))
        log_request(request, user_id)
        return _offline_diagnose(error_code, car_brand, car_model, vin, user_id)
    return UTF8JSONResponse(
        status_code=422,
        content={"detail": exc.errors()},
    )


@app.exception_handler(Exception)
async def global_exception_handler(request: Request, exc: Exception):
    if isinstance(exc, HTTPException):
        return await http_exception_handler(request, exc)
    safe_msg = safe_error_message(exc)
    logger.error(f"Unhandled error: {safe_msg}", exc_info=True)
    return UTF8JSONResponse(
        status_code=500,
        content={
            "error": "internal_error",
            "message": "Внутренняя ошибка сервера. Попробуйте позже.",
        },
    )


# ==================== Роутеры ====================
app.include_router(pricing_router)
app.include_router(license_router)
app.include_router(admin_router)


# ==================== Модели запросов ====================
class DiagnoseRequest(BaseModel):
    error_code: str
    car_brand: str
    car_model: Optional[str] = None
    context: Optional[str] = None
    vin: Optional[str] = None

    @field_validator("error_code")
    @classmethod
    def validate_code(cls, v: str) -> str:
        return sanitize_error_code(v)

    @field_validator("car_brand")
    @classmethod
    def validate_brand(cls, v: str) -> str:
        return sanitize_car_brand(v)

    @field_validator("vin")
    @classmethod
    def validate_vin(cls, v: Optional[str]) -> Optional[str]:
        return sanitize_vin(v) if v else v

    @field_validator("car_model", "context")
    @classmethod
    def validate_text(cls, v: Optional[str]) -> Optional[str]:
        return sanitize_text(v, 200) if v else v


class MemoryCaseRequest(BaseModel):
    error_code: str
    car_brand: str
    diagnosis: str
    solution: str

    @field_validator("error_code")
    @classmethod
    def validate_code(cls, v: str) -> str:
        return sanitize_error_code(v)

    @field_validator("car_brand")
    @classmethod
    def validate_brand(cls, v: str) -> str:
        return sanitize_car_brand(v)

    @field_validator("diagnosis", "solution")
    @classmethod
    def validate_text(cls, v: str) -> str:
        return sanitize_text(v, 2000) if v else v


class InjectRequest(BaseModel):
    code: str
    mode: str = "current"

    @field_validator("code")
    @classmethod
    def validate_code(cls, v: str) -> str:
        return sanitize_error_code(v)

    @field_validator("mode")
    @classmethod
    def validate_mode(cls, v: str) -> str:
        if v not in ("current", "pending", "permanent"):
            raise ValueError("Допустимые значения: current, pending, permanent")
        return v


# ==================== Конфигурация ====================
DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY")
DEEPSEEK_URL = "https://api.deepseek.com/v1/chat/completions"
elm = SimulatedELM327()


# ==================== Вспомогательные функции диагностики ====================
def _extract_diagnose_params(http_request: Request, pydantic_request=None):
    """Извлечь и санитизировать параметры диагностики."""
    params = getattr(http_request.state, "diagnose_params", None)
    if params:
        return (
            sanitize_error_code(params.get("error_code", "")),
            sanitize_car_brand(params.get("car_brand", "")),
            sanitize_text(params.get("car_model", ""), 200) or "",
            sanitize_vin(params.get("vin", "")) if params.get("vin") else "",
            sanitize_text(params.get("context", ""), 500) or "",
            sanitize_user_id(params.get("user_id", "anonymous")),
        )
    if pydantic_request:
        return (
            pydantic_request.error_code,
            pydantic_request.car_brand,
            pydantic_request.car_model or "",
            pydantic_request.vin or "",
            pydantic_request.context or "",
            "anonymous",
        )
    return ("", "", "", "", "", "anonymous")


def _offline_diagnose(
    error_code: str,
    car_brand: str,
    car_model: str = "",
    vin: str = "",
    user_id: str = "anonymous",
    note: str = None,
) -> dict:
    """Офлайн-диагностика по локальной базе SQLite."""
    info = lookup_error(error_code)
    if info:
        diag_id = save_diagnosis(
            user_id, error_code, car_brand, car_model, vin, info["description"], "offline"
        )
        save_historical_code(error_code, "03", car_brand, car_model or None)
        return {
            "error_code": error_code,
            "diagnosis": info["description"],
            "causes": [],
            "solutions": (
                info.get("recommendations", "").split("; ")
                if info.get("recommendations")
                else []
            ),
            "severity": info.get("severity", "medium"),
            "source": "offline",
            "diagnosis_id": diag_id,
            "category": info.get("category"),
            "russian_cars_only": bool(info.get("russian_cars_only")),
            "gas_equipment": bool(info.get("gas_equipment")),
            "note": note,
        }
    return {
        "error_code": error_code,
        "diagnosis": f"Код {error_code} не найден в офлайн-базе.",
        "causes": [],
        "solutions": ["Проверить код в специализированном справочнике."],
        "severity": "unknown",
        "source": "offline",
        "diagnosis_id": None,
        "note": note or "Код отсутствует в локальной базе.",
    }


async def _call_deepseek(
    error_code: str,
    car_brand: str,
    car_model: str,
    vin: str,
    context: str,
    user_id: str,
):
    """Вызвать DeepSeek API с проверками."""
    car_info = f"{car_brand} {car_model}".strip() if car_model else car_brand
    prompt = (
        f"Ошибка {error_code} на {car_info}."
        + (f" VIN:{vin}." if vin else "")
        + (f" Контекст:{context}." if context else "")
        + ' Дай диагноз и решения. JSON: {"diagnosis":"...","causes":[...],"solutions":[...],"severity":"..."}'
    )
    async with httpx.AsyncClient(timeout=30.0) as client:
        resp = await client.post(
            DEEPSEEK_URL,
            headers={"Authorization": f"Bearer {DEEPSEEK_API_KEY}"},
            json={
                "model": "deepseek-chat",
                "messages": [
                    {
                        "role": "system",
                        "content": "Ты механик по российским авто (Lada, ГАЗ, УАЗ). Отвечай кратко, JSON.",
                    },
                    {"role": "user", "content": prompt},
                ],
                "temperature": 0.1,
                "max_tokens": 500,
                "response_format": {"type": "json_object"},
            },
        )
    if resp.status_code != 200:
        logger.warning(f"DeepSeek error: {resp.status_code} {resp.text[:200]}")
        return None
    data = resp.json()
    if "choices" not in data or not data["choices"]:
        logger.warning(f"DeepSeek invalid response: {data}")
        return None
    ai_result = data["choices"][0]["message"]["content"]
    try:
        parsed = json.loads(ai_result)
    except json.JSONDecodeError:
        parsed = {"diagnosis": ai_result, "causes": [], "solutions": []}
    return {
        "diagnosis_text": parsed.get("diagnosis", ai_result),
        "causes": parsed.get("causes", []),
        "solutions": parsed.get("solutions", []),
        "severity": parsed.get("severity", "medium"),
    }


async def _process_ai_diagnosis(
    error_code, car_brand, car_model, vin, context, user_id, http_request
):
    """Общая логика AI-диагностики для POST и GET."""
    ai_limiter.is_allowed(http_request)
    log_request(http_request, user_id)
    integrity.periodic_check_if_needed()

    if not is_paid(user_id):
        return _offline_diagnose(error_code, car_brand, car_model, vin, user_id)

    if not DEEPSEEK_API_KEY:
        return _offline_diagnose(
            error_code,
            car_brand,
            car_model,
            vin,
            user_id,
            note="⚠️ AI-ключ не настроен. Использована офлайн-база.",
        )

    # Кеш AI-ответов
    cached = await _run_sync(lookup_ai_cache, error_code, car_brand, car_model)
    if cached:
        diag_id = await _run_sync(
            save_diagnosis,
            user_id,
            error_code,
            car_brand,
            car_model,
            vin,
            cached["diagnosis"],
            "ai-cache",
        )
        return {
            "error_code": error_code,
            "diagnosis": cached["diagnosis"],
            "causes": json.loads(cached.get("causes", "[]")),
            "solutions": json.loads(cached.get("solutions", "[]")),
            "severity": cached.get("severity", "medium"),
            "source": "ai-cache",
            "diagnosis_id": diag_id,
            "cached": True,
        }

    # Rate limit
    if not await _run_sync(check_ai_rate_limit, user_id):
        return _offline_diagnose(
            error_code,
            car_brand,
            car_model,
            vin,
            user_id,
            note="⚠️ Превышен лимит AI-запросов (20/час). Попробуйте позже.",
        )

    ai_data = await _call_deepseek(
        error_code, car_brand, car_model, vin, context, user_id
    )
    if ai_data is None:
        return _offline_diagnose(
            error_code,
            car_brand,
            car_model,
            vin,
            user_id,
            note="⚠️ Ошибка AI. Использована офлайн-база.",
        )

    # Сохраняем в кеш
    await _run_sync(
        save_ai_cache,
        error_code,
        car_brand,
        car_model,
        ai_data["diagnosis_text"],
        ai_data["causes"],
        ai_data["solutions"],
        ai_data["severity"],
    )

    # Сохраняем в историю
    diag_id = await _run_sync(
        save_diagnosis,
        user_id,
        error_code,
        car_brand,
        car_model,
        vin,
        ai_data["diagnosis_text"],
        "ai",
    )

    # ChromaDB
    if chroma.available:
        await _run_sync(
            chroma.add_case,
            error_code,
            car_brand,
            ai_data["diagnosis_text"],
            "; ".join(ai_data["solutions"]),
            user_id,
        )

    # Облако
    if is_paid(user_id):
        await cloud.push_diagnosis(
            user_id=user_id,
            error_code=error_code,
            car_brand=car_brand,
            diagnosis=ai_data["diagnosis_text"],
            solution="; ".join(ai_data["solutions"]),
        )

    # Исторический код
    await _run_sync(
        save_historical_code, error_code, "03", car_brand, car_model or None
    )

    return {
        "error_code": error_code,
        "diagnosis": ai_data["diagnosis_text"],
        "causes": ai_data["causes"],
        "solutions": ai_data["solutions"],
        "severity": ai_data["severity"],
        "source": "deepseek",
        "diagnosis_id": diag_id,
        "rate_limit_remaining": await _run_sync(get_ai_rate_limit_remaining, user_id),
    }


# ==================== Эндпоинты: Обновления ====================
@app.get("/updates/check")
async def updates_check(user_id: str = Query(default="admin")):
    from updater import check_for_updates

    updates = await check_for_updates()
    return {
        "available": len(updates),
        "updates": [
            {
                "type": u.type,
                "version": u.version,
                "description": u.description,
                "urgent": u.urgent,
            }
            for u in updates
        ],
    }


@app.post("/updates/apply")
async def updates_apply(user_id: str = Query(default="admin")):
    from updater import check_for_updates, apply_updates

    _require_enterprise(user_id, feature="updates_apply")
    updates = await check_for_updates()
    if not updates:
        return {"status": "ok", "message": "No updates available", "applied": 0}
    result = await apply_updates(updates)
    return result


@app.post("/updates/webhook")
async def updates_webhook(request: Request):
    signature = request.headers.get("X-Update-Signature", "")
    if not signature:
        raise HTTPException(status_code=401, detail={"error": "missing_signature"})
    try:
        body = await request.json()
    except Exception:
        raise HTTPException(status_code=400, detail={"error": "invalid_json"})
    from updater import process_webhook

    result = await process_webhook(body, signature)
    return result


@app.get("/updates/client-check")
async def updates_client_check(
    seq: int = Query(default=0),
    app_version: str = Query(default=APP_VERSION),
):
    from updater import get_client_updates

    return get_client_updates(since_seq=seq)


@app.get("/updates/status")
def updates_status():
    from updater import (
        get_current_version,
        POLL_INTERVAL,
        UPDATE_SERVER,
        AUTO_APPLY_DB,
        AUTO_APPLY_CODE,
    )

    ver = get_current_version()
    return {
        "app_version": ver.get("version"),
        "build": ver.get("build"),
        "codename": ver.get("codename"),
        "update_server": UPDATE_SERVER,
        "poll_interval_seconds": POLL_INTERVAL,
        "auto_apply_db": AUTO_APPLY_DB,
        "auto_apply_code": AUTO_APPLY_CODE,
        "device_id": _get_device_id_safe(),
    }


# ==================== Эндпоинты: Фоновый агент ====================
@app.get("/agent/status")
def agent_status():
    from weekly_agent import get_agent

    agent = get_agent()
    state = agent.state
    return {
        "last_run": (
            datetime.fromtimestamp(state.last_run, tz=timezone.utc).isoformat()
            if state.last_run
            else None
        ),
        "total_runs": state.total_runs,
        "total_found": state.total_found,
        "last_result": state.last_result,
        "next_run_in_seconds": (
            max(0, int(MIN_RUN_INTERVAL - (time.time() - state.last_run)))
            if state.last_run
            else 0
        ),
    }


@app.post("/agent/run")
async def agent_run(
    user_id: str = Query(default="admin"), force: bool = Query(default=False)
):
    _require_enterprise(user_id, feature="agent_run")
    from weekly_agent import get_agent

    agent = get_agent()
    result = await agent.run(force=force)
    return result


# ==================== События приложения ====================
@app.on_event("startup")
async def startup():
    global _APP_COMPROMISED, _APP_TAMPER_MODE
    if sys.stdout.encoding != "utf-8":
        sys.stdout = __import__("io").TextIOWrapper(
            sys.stdout.buffer, encoding="utf-8", errors="replace"
        )
    print("🚗 AutoDiag AI запускается...")
    print(f" ChromaDB: {'✅ доступна' if chroma.available else '⚠️ недоступна'}")
    print(f" SQLite: ✅ {db.DB_PATH}")
    print(f" CORS: {get_cors_origins()}")
    print(f" Security: rate limiting + headers + input validation")

    ok, mode, reason = integrity.check_on_startup()
    if mode == "shutdown":
        _APP_COMPROMISED = True
        _APP_TAMPER_MODE = "shutdown"
        print(f" 🔴 ЦЕЛОСТНОСТЬ НАРУШЕНА: {reason}")
        print(f" ⛔ КРИТИЧЕСКОЕ НАРУШЕНИЕ — завершение работы.")
        sys.exit(1)
    elif mode == "free_only":
        _APP_COMPROMISED = True
        _APP_TAMPER_MODE = "free_only"
        print(f" 🟡 ЦЕЛОСТНОСТЬ НАРУШЕНА: {reason}")
        print(f" ⚠️ Приложение работает в режиме FREE-ONLY.")
    else:
        print(f" Integrity: ✅ OK")

    dev_id = get_device_id()
    print(f" Device: {dev_id}")
    if detect_debugger():
        _APP_COMPROMISED = True
        _APP_TAMPER_MODE = "free_only"
        print(f" ⚠️ Обнаружен отладчик! Free-only режим.")

    # Фоновый тик симулятора
    t = threading.Thread(target=_sim_loop, daemon=True)
    t.start()

    # Фоновый опрос обновлений
    start_polling()
    print(
        f" Updates: polling every {POLL_INTERVAL}s → {UPDATE_SERVER}"
        if POLL_INTERVAL > 0
        else " Updates: polling disabled"
    )

    # Фоновое обновление кэша
    from updater import start_background_fetcher as _start_bg_fetcher
    from updater import refresh_update_cache as _refresh

    asyncio.create_task(_start_bg_fetcher())
    asyncio.create_task(_refresh())
    print(f" ClientCache: auto-refresh every 300s")

    # Фоновый агент
    agent_t = threading.Thread(target=_weekly_agent_loop, daemon=True)
    agent_t.start()
    print(f" Agent: weekly background search active")


@app.on_event("shutdown")
async def shutdown():
    print("=== AutoDiag AI stopped ===")


def _sim_loop():
    """Фоновый цикл симуляции двигателя."""
    while True:
        try:
            s = sim_ref.get()
            s.tick()
            collector.add_sample(s.get_live_data())
            s.generate_natural_errors()
        except Exception:
            pass
        time.sleep(1)


def _weekly_agent_loop():
    """Фоновый цикл еженедельного агента."""
    time.sleep(600)  # первый запуск через 10 минут
    while True:
        try:
            from weekly_agent import get_agent as _get_agent

            agent = _get_agent()
            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            result = loop.run_until_complete(agent.run())
            loop.close()
            if result.get("status") == "completed":
                info = (
                    f"codes: {result.get('error_codes', {}).get('stored', 0)}, "
                    f"schemas: {result.get('schemas', {}).get('new_schemas_found', 0)}, "
                    f"repairs: {result.get('repairs', {}).get('updated', 0)}"
                )
            else:
                info = result.get("reason", "")
            print(f" [WEEKLY] {result.get('status')}: {info}")
        except Exception as e:
            print(f" [WEEKLY] Error: {e}")
        time.sleep(MIN_RUN_INTERVAL)


# ==================== Root ====================
@app.get("/")
async def root():
    return {
        "status": "ok",
        "product": "AutoDiag AI",
        "version": APP_VERSION,
        "message": "Сервер работает. Агент готов.",
        "endpoints": {
            "simulator": "/sim/live, /sim/errors",
            "live_data": "/live, /live/graph",
            "errors": "/errors, /errors/03, /errors/07, /errors/0A, /errors/clear",
            "diagnose": "/diagnose (POST/GET), /diagnose/offline",
            "history": "/history",
            "memory": "/memory/search, /memory/add, /memory/count",
            "schemas": "/schemas/{code}, /schemas/{code}/image",
            "sync": "/sync/status",
            "cars": "/cars",
            "pricing": "/pricing/plans, /pricing/features, /pricing/status",
            "admin": "/admin/*",
            "health": "/health",
        },
        "chroma_available": chroma.available,
    }


# ==================== Симулятор (Enterprise) ====================
@app.get("/sim/live")
def sim_live(request: Request, user_id: str = Query(default="anonymous")):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_enterprise(user_id, feature="simulator")
    data = sim_ref.get().get_live_data()
    return {
        "rpm": data["rpm"],
        "speed": data["speed"],
        "coolant_temp": data["coolant_temp"],
        "maf": data["maf"],
    }


@app.get("/sim/errors")
def sim_errors(request: Request, user_id: str = Query(default="anonymous")):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_enterprise(user_id, feature="simulator")
    codes = sim_ref.get().get_codes()
    errors = codes["current"] + codes["pending"]
    if not errors:
        errors = ["P0171", "P0300"]
    result = []
    for code in set(errors):
        info = lookup_error(code)
        result.append(
            {
                "code": code,
                "desc": info["description"] if info else "Неизвестная ошибка",
            }
        )
    return result


# ==================== Живые данные ====================
@app.get("/live")
def live_data(request: Request, user_id: str = Query(default="anonymous")):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_paid(user_id)
    return sim_ref.get().get_live_data()


@app.get("/live/graph")
def live_graph_data(request: Request, user_id: str = Query(default="anonymous")):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_paid(user_id)
    return collector.get_graph_data()


# ==================== Чтение ошибок (ELM327) ====================
@app.get("/errors")
def read_errors():
    codes = sim_ref.get().get_codes()
    all_codes = set(codes["current"] + codes["pending"] + codes["permanent"])
    decoded = {}
    if all_codes:
        rows = lookup_errors_batch(list(all_codes))
        decoded = {r["code"]: r for r in rows}

    def enrich(code_list):
        return [{"code": c, "info": decoded.get(c)} for c in code_list]

    return {
        "check_engine": codes["check_engine"],
        "current": enrich(codes["current"]),
        "pending": enrich(codes["pending"]),
        "permanent": enrich(codes["permanent"]),
    }


@app.get("/errors/03")
def errors_mode_03():
    codes = sim_ref.get().get_codes()["current"]
    decoded = {}
    if codes:
        rows = lookup_errors_batch(codes)
        decoded = {r["code"]: r for r in rows}
    return {
        "mode": "03",
        "description": "Подтверждённые коды неисправностей",
        "codes": [{"code": c, "info": decoded.get(c)} for c in codes],
    }


@app.get("/errors/07")
def errors_mode_07():
    codes = sim_ref.get().get_codes()["pending"]
    decoded = {}
    if codes:
        rows = lookup_errors_batch(codes)
        decoded = {r["code"]: r for r in rows}
    return {
        "mode": "07",
        "description": "Ожидающие коды (pending)",
        "codes": [{"code": c, "info": decoded.get(c)} for c in codes],
    }


@app.get("/errors/0A")
def errors_mode_0A():
    codes = sim_ref.get().get_codes()["permanent"]
    decoded = {}
    if codes:
        rows = lookup_errors_batch(codes)
        decoded = {r["code"]: r for r in rows}
    return {
        "mode": "0A",
        "description": "Перманентные коды",
        "codes": [{"code": c, "info": decoded.get(c)} for c in codes],
    }


@app.post("/errors/clear")
def clear_errors(user_id: str = Query(default="anonymous")):
    sim_ref.get().clear_codes()
    collector.clear()
    return {"status": "cleared", "message": "Ошибки сброшены. Живые данные очищены."}


@app.post("/errors/inject")
def inject_error(
    request: Request,
    body: InjectRequest,
    user_id: str = Query(default="anonymous"),
):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_enterprise(user_id, feature="simulator")
    sim_ref.get().inject_code(body.code, body.mode)
    return {
        "status": "injected",
        "code": body.code,
        "mode": body.mode,
    }


# ==================== Диагностика ====================
@app.post("/diagnose")
async def diagnose(http_request: Request, user_id: str = Query(default="anonymous")):
    """AI-диагностика через DeepSeek. Параметры из request.state (DiagnoseWAFShield)."""
    e, b, m, v, c, u = _extract_diagnose_params(http_request)
    user_id = u if u != "anonymous" else user_id
    return await _process_ai_diagnosis(e, b, m, v, c, user_id, http_request)


@app.get("/diagnose")
async def diagnose_get(
    http_request: Request,
    error_code: str = Query(default="", description="Код ошибки OBD2"),
    car_brand: str = Query(default="", description="Марка авто"),
    car_model: str = Query(default="", description="Модель"),
    vin: str = Query(default="", description="VIN"),
    context: str = Query(default="", description="Доп. контекст"),
    user_id: str = Query(default="anonymous"),
):
    """AI-диагностика через GET (WAF-safe, для мобильных клиентов)."""
    e, b, m, v, c, u = _extract_diagnose_params(http_request)
    error_code = e or sanitize_error_code(error_code)
    car_brand = b or sanitize_car_brand(car_brand)
    car_model = m or sanitize_text(car_model, 200) or ""
    vin = v or (sanitize_vin(vin) if vin else "")
    context = c or sanitize_text(context, 500) or ""
    user_id = u if u != "anonymous" else user_id
    return await _process_ai_diagnosis(
        error_code, car_brand, car_model, vin, context, user_id, http_request
    )


@app.get("/diagnose/offline")
def offline_lookup(request: Request, code: str = Query(..., description="Код ошибки")):
    general_limiter.is_allowed(request)
    log_request(request)
    code = sanitize_error_code(code)
    info = lookup_error(code)
    if info:
        return {"found": True, "data": info}
    return {"found": False, "message": f"Код {code} не найден."}


# ==================== История диагностик ====================
@app.get("/history")
def diagnostic_history(
    request: Request,
    user_id: str = Query(default="anonymous"),
    limit: int = Query(default=50, le=200),
):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    rows = get_history(user_id, limit)
    return {"user_id": user_id, "count": len(rows), "diagnostics": rows}


@app.get("/history/stats")
def history_stats():
    return {"stats": get_error_stats()}


@app.get("/history/codes")
def historical_codes_analysis(
    car_brand: Optional[str] = None,
    mode: Optional[str] = None,
):
    return {"historical_codes": get_historical_codes(car_brand, mode)}


# ==================== Самообучение (ChromaDB) ====================
@app.get("/memory/search")
def memory_search(
    request: Request,
    q: str = Query(..., description="Поисковый запрос или код ошибки"),
    n: int = Query(default=5, le=20),
    user_id: str = Query(default="anonymous"),
):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_paid(user_id)
    if not chroma.available:
        return {
            "available": False,
            "message": "ChromaDB не установлена. Установите: pip install chromadb",
        }
    results = chroma.search(q, n)
    return {"available": True, "query": q, "count": len(results), "results": results}


@app.post("/memory/add")
def memory_add(
    request: Request,
    body: MemoryCaseRequest,
    user_id: str = Query(default="anonymous"),
):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_paid(user_id)
    if not chroma.available:
        raise HTTPException(status_code=503, detail="ChromaDB недоступна")
    case_id = chroma.add_case(
        error_code=body.error_code,
        car_brand=body.car_brand,
        diagnosis=body.diagnosis,
        solution=body.solution,
        user_id=user_id,
    )
    return {"status": "added", "case_id": case_id}


@app.get("/memory/count")
def memory_count(request: Request, user_id: str = Query(default="anonymous")):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    _require_paid(user_id)
    return {"available": chroma.available, "count": chroma.count()}


# ==================== Схемы узлов ====================
@app.get("/schemas")
def list_schemas():
    return {"schemas": list_available_schemas(), "total": len(_SCHEMAS)}


@app.post("/schemas/refresh")
async def refresh_schemas(
    request: Request,
    user_id: str = Query(default="admin"),
):
    download_limiter.is_allowed(request)
    log_request(request, user_id)
    asyncio.create_task(_background_refresh())
    return {
        "status": "started",
        "message": (
            f"Запущено пополнение библиотеки для {len(_SCHEMAS)} кодов. "
            f"Это займёт несколько минут. Проверьте /schemas/stats позже."
        ),
        "total_codes": len(_SCHEMAS),
        "codes": list(_SCHEMAS.keys()),
    }


@app.get("/schemas/stats")
def get_schemas_stats():
    return get_download_stats()


@app.get("/schemas/{code}")
def get_schema_endpoint(
    request: Request,
    code: str,
    user_id: str = Query(default="anonymous"),
):
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    code = sanitize_error_code(code)
    result = get_schema_or_upgrade(code, is_paid=True)
    if result.get("available"):
        stats = get_download_stats()
        result["data"]["_downloaded_images"] = stats.get("codes", {}).get(code, 0)
    return result


@app.get("/schemas/{code}/image")
def get_schema_image(
    request: Request,
    code: str,
    user_id: str = Query(default="anonymous"),
):
    """Вернуть SVG-схему узла по коду ошибки."""
    general_limiter.is_allowed(request)
    log_request(request, user_id)
    code = sanitize_error_code(code)
    try:
        svg = render_schema_svg(code)
        if not svg:
            raise HTTPException(
                status_code=404,
                detail={"error": "schema_not_found", "code": code},
            )
        return Response(
            content=svg,
            media_type="image/svg+xml; charset=utf-8",
            headers={"Cache-Control": "public, max-age=3600"},
        )
    except HTTPException:
        raise
    except Exception as e:
        logger.error(f"Schema image error for {code}: {e}")
        raise HTTPException(
            status_code=500,
            detail={"error": "schema_render_failed", "code": code},
        )


async def _background_refresh():
    """Фоновое обновление схем."""
    try:
        await refresh_all_schemas()
        logger.info("Schemas refresh completed")
    except Exception as e:
        logger.error(f"Schemas refresh failed: {e}")


# ==================== Синхронизация ====================
@app.get("/sync/status")
def sync_status(user_id: str = Query(default="anonymous")):
    return {
        "user_id": user_id,
        "cloud_available": getattr(cloud, "available", False),
        "paid": is_paid(user_id),
        "tier": get_user_tier(user_id),
    }


# ==================== Автомобили ====================
@app.get("/cars")
def list_cars():
    return {
        "russian_cars": RUSSIAN_CARS,
        "total": len(RUSSIAN_CARS) if isinstance(RUSSIAN_CARS, (list, dict)) else 0,
    }


# ==================== Health ====================
@app.get("/health")
def health():
    return {
        "status": "healthy",
        "version": APP_VERSION,
        "compromised": _APP_COMPROMISED,
        "tamper_mode": _APP_TAMPER_MODE,
        "chroma": chroma.available,
        "device_id": _get_device_id_safe(),
    }


# ==================== Запуск ====================
if __name__ == "__main__":
    import uvicorn

    port = int(os.getenv("PORT", "8000"))
    uvicorn.run("autodiag_main:app", host="0.0.0.0", port=port, reload=False)
