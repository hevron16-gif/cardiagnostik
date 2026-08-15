import os

# Тестовый режим: выключен в продакшене
TESTING_UNLOCK_ALL = os.getenv("TESTING_UNLOCK_ALL", "false").lower() == "true"

"""
AutoDiag AI v1.0 — Модуль разделения free/paid
Чёткое разделение бесплатной и платной версии.

 Free-версия (только):
- 📡 Чтение ошибок через ELM327
- 📖 Офлайн-расшифровка (SQLite)
- 📋 История (последние 10 записей)

 Pro-версия добавляет:
- 🔒 Схемы узлов (2D)
- 🤖 AI-диагностика (DeepSeek)
- 📈 Живые графики
- 🧠 Самообучение (ChromaDB)
- ☁️ Облачная синхронизация

 Enterprise добавляет:
- 📋 Полная история
- 🧪 Базовый симулятор OBD
- 🛡️ Панель администратора
- 🔄 Автообновление базы
"""

from fastapi import APIRouter, HTTPException, Query, Depends
from pydantic import BaseModel
from typing import Optional

from database import get_user_tier, get_user_features

router = APIRouter(prefix="/pricing", tags=["pricing"])


# ================= Модели =================

class PricingPlan(BaseModel):
    name: str
    price: str
    features: list[str]
    highlighted: bool = False


# ================= Планы =================

PLANS = {
    "free": PricingPlan(
        name="Free",
        price="0 ₽ / мес",
        features=[
            "📡 Чтение ошибок через ELM327",
            "📖 Офлайн-расшифровка (SQLite, 50+ кодов)",
            "📋 История диагностик (последние 10)",
        ],
        highlighted=False,
    ),
    "pro": PricingPlan(
        name="Pro",
        price="1 499 ₽ навсегда",
        features=[
            "✅ ВСЁ из Free",
            "🤖 AI-диагностика (DeepSeek)",
            "🔒 Схемы узлов с маркерами (91 шт)",
            "📈 AI-анализ графиков с эталоном",
            "☁️ Облачная синхронизация",
            "🔄 Авто-обновление базы",
        ],
        highlighted=True,
    ),
    "enterprise": PricingPlan(
        name="Enterprise",
        price="4 999 ₽ навсегда",
        features=[
            "✅ ВСЁ из Pro",
            "🛡️ Панель администратора",
            "👥 Мультипользовательский доступ",
            "📊 Расширенная аналитика",
            "🎯 Приоритетная поддержка",
            "🔧 Доступ к API",
        ],
        highlighted=False,
    ),
}


# ================= Feature gating =================

def require_feature(feature: str):
    """Dependency: проверить, что у пользователя есть фича."""
    async def checker(user_id: str = Query(..., description="ID пользователя")):
        features = await get_user_features(user_id)
        if feature not in features:
            raise HTTPException(
                status_code=402,
                detail={
                    "error": "payment_required",
                    "feature": feature,
                    "message": _upgrade_message(feature),
                    "upgrade_url": "/pricing/plans",
                }
            )
        return user_id
    return checker


# ВРЕМЕННО: все Pro-функции открыты для тестирования (схемы, AI, ...)

def is_paid(user_id: str = "anonymous") -> bool:
    # Testing: always paid so /schemas and AI work without license key
    return TESTING_UNLOCK_ALL
async def get_paid_features(user_id: str) -> dict:
    """Получить информацию о доступных и недоступных фичах."""
    tier = await get_user_tier(user_id)
    features = await get_user_features(user_id)
    all_features = {
        "elm327": "📡 Чтение ошибок через ELM327",
        "offline": "📖 Офлайн-расшифровка (SQLite)",
        "basic_simulator": "🧪 Базовый симулятор OBD",
        "basic_history": "📋 История (последние 10)",
        "ai": "🤖 AI-диагностика (DeepSeek)",
        "schemas": "🔒 Схемы узлов (2D)",
        "sync": "☁️ Облачная синхронизация",
        "self_learning": "🧠 Самообучение (ChromaDB)",
        "live_graphs": "📈 Живые данные + графики",
        "full_history": "📋 Полная история",
        "admin": "🛡️ Панель администратора",
        "auto_update": "🔄 Автообновление базы",
    }
    return {
        "tier": tier,
        "enabled": [{"key": k, "label": v} for k, v in all_features.items() if k in features],
        "locked": [{"key": k, "label": v} for k, v in all_features.items() if k not in features],
    }


def _upgrade_message(feature: str) -> str:
    """Сообщение о необходимости апгрейда для конкретной фичи."""
    messages = {
        "ai": "AI-диагностика доступна в версии Pro (1 499 ₽ навсегда). Умный анализ ошибок через DeepSeek.",
        "schemas": "Схемы узлов с маркерами доступны в версии Pro. Визуальные схемы с указанием датчиков.",
        "sync": "Облачная синхронизация доступна в версии Pro.",
        "self_learning": "Самообучение доступно в версии Pro. Память успешных решений.",
        "live_graphs": "AI-анализ графиков с эталоном доступен в версии Pro.",
        "full_history": "Полная история доступна в версии Enterprise.",
        "basic_simulator": "Базовый симулятор OBD доступен в версии Enterprise.",
        "admin": "Панель администратора доступна в версии Enterprise.",
        "auto_update": "Автообновление базы доступно в версии Pro.",
    }
    return messages.get(feature, "Функция доступна в платной версии.")


# ================= Эндпоинты =================

@router.get("/plans")
def get_plans():
    """Список тарифных планов с преимуществами."""
    return {
        "title": "AutoDiag AI — Тарифы",
        "description": "Выберите план. Платная версия открывает весь потенциал ИИ-диагностики.",
        "plans": [p.model_dump() for p in PLANS.values()],
    }


@router.get("/features")
async def get_features(user_id: str = Query(..., description="ID пользователя")):
    """Получить статус всех фич для пользователя."""
    return await get_paid_features(user_id)


@router.get("/status")
async def check_status(user_id: str = Query(..., description="ID пользователя")):
    """Проверить статус подписки."""
    tier = await get_user_tier(user_id)
    features = await get_user_features(user_id)
    return {
        "user_id": user_id,
        "tier": tier,
        "is_paid": tier != "free",
        "feature_count": len(features),
        "features": features,
    }

