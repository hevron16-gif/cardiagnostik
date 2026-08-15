"""
DeepSeek API клиент для AutoDiag AI.
- Поиск диагноза с защитой от галлюцинаций
- Расписание дешёвых часов: 19:00-04:00 МСК
- Fallback на локальную базу при ошибке API
"""
import os
import json
import logging
import httpx
from datetime import datetime, timezone, timedelta
from typing import Optional, Dict, Any

logger = logging.getLogger("autodiag.deepseek")

DEEPSEEK_API_KEY = os.getenv("DEEPSEEK_API_KEY", "")
DEEPSEEK_API_URL = "https://api.deepseek.com/chat/completions"

# Расписание дешёвых часов (МСК: 19:00 - 04:00)
CHEAP_HOURS_START = 19  # 19:00 МСК
CHEAP_HOURS_END = 4     # 04:00 МСК

# Модели
MODEL_CHEAP = "deepseek-chat"      # Дешёвая модель (для дешёвых часов)
MODEL_PREMIUM = "deepseek-reasoner" # Мощная модель (для обычных часов)

# Таймауты
TIMEOUT_CHEAP = 30.0
TIMEOUT_PREMIUM = 60.0


def is_cheap_hours() -> bool:
    """Проверяет, сейчас дешёвые часы (19:00-04:00 МСК)."""
    msk = timezone(timedelta(hours=3))
    now_msk = datetime.now(msk)
    hour = now_msk.hour
    # 19:00 - 23:59 или 00:00 - 04:00
    return hour >= CHEAP_HOURS_START or hour < CHEAP_HOURS_END


def get_model_and_timeout() -> tuple[str, float]:
    """Возвращает модель и таймаут в зависимости от времени."""
    if is_cheap_hours():
        return MODEL_CHEAP, TIMEOUT_CHEAP
    return MODEL_PREMIUM, TIMEOUT_PREMIUM


def build_system_prompt() -> str:
    """Системный промпт с защитой от галлюцинаций."""
    return """Ты — автомобильный диагностический ассистент AutoDiag AI.

КРИТИЧЕСКИЕ ПРАВИЛА (нарушение = отказ от ответа):
1. Отвечай ТОЛЬКО на основе фактов из автомобильной диагностики OBD2.
2. Если не уверен — честно скажи "Недостаточно данных для точного диагноза".
3. НЕ выдумывай коды ошибок, названия деталей или симптомы.
4. НЕ давай советы, которые могут повредить автомобиль или создать опасность.
5. Всегда указывай источник: "На основе данных OBD2" или "Требуется проверка специалистом".

ФОРМАТ ОТВЕТА (строго соблюдай):
1. ОБЩАЯ ОЦЕНКА — краткое описание проблемы (1-2 предложения)
2. ВЕРОЯТНЫЕ ПРИЧИНЫ — список из 3-5 пунктов, отсортированных по вероятности
3. РЕКОМЕНДАЦИИ — что проверить и в каком порядке (3-5 пунктов)
4. КРИТИЧНОСТЬ — одно из: НИЗКАЯ / СРЕДНЯЯ / ВЫСОКАЯ / КРИТИЧЕСКАЯ
5. МОЖНО ЛИ ЕХАТЬ — Да / Осторожно / Нет (с пояснением)
6. ПРИМЕРНАЯ СТОИМОСТЬ РЕМОНТА — диапазон в рублях или "Требуется диагностика"

Если для диагноза нужны дополнительные данные (freeze frame, live data) — укажи это явно."""


def build_user_prompt(
    code: str,
    brand: Optional[str],
    model: Optional[str],
    year: Optional[int],
    vin: Optional[str],
    context: Optional[str],
    local_data: Optional[Dict[str, Any]] = None,
) -> str:
    """Формирует пользовательский промпт."""
    parts = []
    parts.append(f"Код ошибки OBD2: {code}")
    if brand:
        parts.append(f"Марка: {brand}")
    if model:
        parts.append(f"Модель: {model}")
    if year:
        parts.append(f"Год: {year}")
    if vin:
        parts.append(f"VIN: {vin}")
    if context:
        parts.append(f"Дополнительный контекст:\n{context}")

    if local_data:
        parts.append("\nДанные из локальной базы (проверь и дополни):")
        desc = local_data.get("description") or local_data.get("diagnosis") or ""
        if desc:
            parts.append(f"Описание: {desc}")
        causes = local_data.get("causes") or []
        if causes:
            parts.append(f"Причины: {', '.join(str(c) for c in causes[:5])}")
        solutions = local_data.get("solutions") or local_data.get("recommendations") or []
        if solutions:
            parts.append(f"Решения: {', '.join(str(s) for s in solutions[:5])}")

    parts.append("\nДай диагноз строго по формату из системного промпта.")
    return "\n".join(parts)


async def diagnose_with_deepseek(
    code: str,
    brand: Optional[str] = None,
    model: Optional[str] = None,
    year: Optional[int] = None,
    vin: Optional[str] = None,
    context: Optional[str] = None,
    local_data: Optional[Dict[str, Any]] = None,
) -> Optional[Dict[str, Any]]:
    """
    Отправляет запрос к DeepSeek API и возвращает структурированный диагноз.
    Returns None если API недоступен или ключ не задан.
    """
    if not DEEPSEEK_API_KEY or DEEPSEEK_API_KEY == "your_deepseek_api_key_here":
        logger.warning("DEEPSEEK_API_KEY не задан — пропускаю AI-диагностику")
        return None

    model, timeout = get_model_and_timeout()
    is_cheap = is_cheap_hours()
    logger.info(f"DeepSeek запрос: {code} ({brand} {model}), модель={model}, дешёвые_часы={is_cheap}")

    system_prompt = build_system_prompt()
    user_prompt = build_user_prompt(code, brand, model, year, vin, context, local_data)

    payload = {
        "model": model,
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": user_prompt},
        ],
        "temperature": 0.3,  # Низкая температура = меньше галлюцинаций
        "max_tokens": 1500,
        "stream": False,
    }

    headers = {
        "Authorization": f"Bearer {DEEPSEEK_API_KEY}",
        "Content-Type": "application/json",
    }

    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            response = await client.post(DEEPSEEK_API_URL, json=payload, headers=headers)
            response.raise_for_status()
            data = response.json()

            if "choices" not in data or not data["choices"]:
                logger.warning("DeepSeek вернул пустой choices")
                return None

            content = data["choices"][0]["message"]["content"]
            usage = data.get("usage", {})

            # Парсим структурированный ответ
            result = parse_deepseek_response(content, code)
            result["_model"] = model
            result["_is_cheap_hours"] = is_cheap
            result["_tokens_used"] = usage.get("total_tokens", 0)
            result["_source"] = "deepseek"

            logger.info(f"DeepSeek ответ получен: {code}, токенов={usage.get('total_tokens', 0)}")
            return result

    except httpx.HTTPStatusError as e:
        logger.error(f"DeepSeek HTTP ошибка {e.response.status_code}: {e.response.text[:200]}")
        return None
    except httpx.TimeoutException:
        logger.error("DeepSeek таймаут")
        return None
    except Exception as e:
        logger.error(f"DeepSeek ошибка: {e}")
        return None


def parse_deepseek_response(content: str, code: str) -> Dict[str, Any]:
    """Парсит ответ DeepSeek в структурированный формат."""
    result = {
        "code": code,
        "description": "",
        "causes": [],
        "solutions": [],
        "severity": "СРЕДНЯЯ",
        "can_drive": "Осторожно",
        "repair_cost": "Требуется диагностика",
        "raw": content,
    }

    lines = content.split("\n")
    current_section = None

    for line in lines:
        line = line.strip()
        if not line:
            continue

        upper = line.upper()
        if "ОБЩАЯ ОЦЕНКА" in upper or "ОПИСАНИЕ" in upper:
            current_section = "description"
            continue
        elif "ПРИЧИН" in upper:
            current_section = "causes"
            continue
        elif "РЕКОМЕНДАЦИ" in upper or "РЕШЕНИ" in upper:
            current_section = "solutions"
            continue
        elif "КРИТИЧНОСТЬ" in upper:
            # Извлекаем значение из строки
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
        elif "СТОИМОСТЬ" in upper or "ЦЕНА" in upper:
            result["repair_cost"] = line.split(":")[-1].strip() if ":" in line else line
            current_section = None
            continue

        # Собираем содержимое секций
        if current_section == "description":
            result["description"] += line + " "
        elif current_section == "causes" and line.startswith(("•", "-", "*", "1.", "2.", "3.", "4.", "5.")):
            result["causes"].append(line.lstrip("•-* 1234567890.").strip())
        elif current_section == "solutions" and line.startswith(("•", "-", "*", "1.", "2.", "3.", "4.", "5.")):
            result["solutions"].append(line.lstrip("•-* 1234567890.").strip())

    result["description"] = result["description"].strip()

    # Если ничего не распарсилось — используем весь текст как описание
    if not result["description"] and not result["causes"]:
        result["description"] = content[:500]

    return result
