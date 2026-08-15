#!/usr/bin/env python3
"""
Объединяет 3 JSON-файла с кодами ошибок OBD2 и генерирует C# код для OBD2Codes.cs.
Правила:
- kim_russian_codes.json — приоритет, добавляем все коды (специфичны для русских авто)
- truck_codes.json — добавляем все коды SPNxxx-FMIxx и J7-xx
- chiptuner_generic_ru.json — добавляем ТОЛЬКО новые коды, которых нет в текущем OBD2Codes.cs
"""

import json
import re
import os
from collections import OrderedDict

# Пути
BASE_DIR = r"C:\Users\User\source\repos\cardiagnostik"
SOURCE_DIR = os.path.join(BASE_DIR, "scripts", "source-data")
CS_FILE = os.path.join(BASE_DIR, "mobile", "Data", "OBD2Codes.cs")

# Загружаем JSON
with open(os.path.join(SOURCE_DIR, "kim_russian_codes.json"), "r", encoding="utf-8") as f:
    kim_data = json.load(f)

with open(os.path.join(SOURCE_DIR, "truck_codes.json"), "r", encoding="utf-8") as f:
    truck_data = json.load(f)

with open(os.path.join(SOURCE_DIR, "chiptuner_generic_ru.json"), "r", encoding="utf-8") as f:
    generic_data = json.load(f)

# Читаем текущий C# файл для извлечения существующих кодов
with open(CS_FILE, "r", encoding="utf-8") as f:
    cs_content = f.read()

existing_codes = set(re.findall(r'Code\s*=\s*"([^"]+)"', cs_content))
print(f"Существующих кодов в OBD2Codes.cs: {len(existing_codes)}")

# Словарь для объединения (ключ = код, значение = dict с полями)
merged = OrderedDict()

# Приоритет 1: kim_russian_codes (все коды)
for code, info in kim_data.items():
    if code in merged:
        continue
    category_map = {
        "turbo": "Турбонаддув",
        "emission": "Выхлопная система / Экология",
        "cooling": "Система охлаждения",
        "transmission": "Трансмиссия",
        "electrical": "Электрика",
        "ecu": "ЭБУ / Процессор",
        "fuel": "Топливная система",
        "gbo": "ГБО (газобаллонное оборудование)",
    }
    raw_cat = info.get("category", "")
    category = category_map.get(raw_cat, raw_cat)
    causes = "; ".join(info.get("causes", [])) if info.get("causes") else ""
    solutions = "; ".join(info.get("solutions", [])) if info.get("solutions") else ""
    symptoms = solutions  # в C# поле Symptoms = решения/симптомы
    ecu = info.get("ecu", "")
    make = info.get("make", "")
    model = info.get("model", "")
    # Добавляем ECU/марку в описание для контекста
    desc = info.get("description", "")
    extra = []
    if make:
        extra.append(make)
    if model:
        extra.append(model)
    if ecu:
        extra.append(ecu)
    if extra:
        desc = f"{desc} ({', '.join(extra)})"
    merged[code] = {
        "Code": code,
        "Category": category,
        "Description": desc,
        "Causes": causes,
        "Symptoms": symptoms,
    }

# Приоритет 2: truck_codes (все коды)
truck_categories = {
    "ЯМЗ-536": "Грузовики ЯМЗ-536",
    "Cummins": "Грузовики Cummins",
    "Микас": "Грузовики / Микас",
}
for code, info in truck_data.items():
    if code in merged:
        continue
    ecu = info.get("ecu", "")
    category = "Грузовики (SPN/FMI)"
    for key, val in truck_categories.items():
        if key in ecu:
            category = val
            break
    causes = "; ".join(info.get("causes", [])) if info.get("causes") else ""
    solutions = "; ".join(info.get("solutions", [])) if info.get("solutions") else ""
    symptoms = solutions
    desc = info.get("description", "")
    if ecu:
        desc = f"{desc} ({ecu})"
    merged[code] = {
        "Code": code,
        "Category": category,
        "Description": desc,
        "Causes": causes,
        "Symptoms": symptoms,
    }

# Приоритет 3: chiptuner_generic_ru (только новые, которых нет в existing_codes)
new_generic = 0
for code, desc in generic_data.items():
    if code in merged:
        continue
    if code in existing_codes:
        continue
    # Определяем категорию по первым цифрам
    category = "Generic OBD2"
    m = re.match(r'P(\d)', code)
    if m:
        digit = m.group(1)
        if digit == "0":
            category = "(P0xxx) Топливно-воздушная смесь / Датчики"
        elif digit == "1":
            category = "(P1xxx) Топливно-воздушная смесь / Датчики"
        else:
            category = f"(P{digit}xxx) Generic OBD2"
    merged[code] = {
        "Code": code,
        "Category": category,
        "Description": desc,
        "Causes": "",
        "Symptoms": "",
    }
    new_generic += 1

print(f"Добавлено из kim_russian_codes: {len(kim_data)}")
print(f"Добавлено из truck_codes: {len(truck_data)}")
print(f"Новых generic кодов из chiptuner: {new_generic}")
print(f"Всего новых кодов для добавления: {len(merged)}")

# Генерируем C# строки
lines = []
lines.append("")
lines.append("        // ======== Дополнительные коды из Kimi K3 (русские авто, грузовики, generic) ========")

# Группируем по категориям для красоты
from collections import defaultdict
groups = defaultdict(list)
for code, item in merged.items():
    groups[item["Category"]].append(item)

for category in sorted(groups.keys()):
    items = groups[category]
    lines.append(f"        // --- {category} ---")
    for item in items:
        code = item["Code"]
        desc = item["Description"].replace('"', '\\"')
        causes = item["Causes"].replace('"', '\\"')
        symptoms = item["Symptoms"].replace('"', '\\"')
        causes_str = f', Causes = "{causes}"' if causes else ""
        symptoms_str = f', Symptoms = "{symptoms}"' if symptoms else ""
        line = f'        new() {{ Code = "{code}", Category = "{category}", Description = "{desc}"{causes_str}{symptoms_str} }},'
        lines.append(line)

output = "\n".join(lines)

# Сохраняем во временный файл для последующей вставки
with open(os.path.join(BASE_DIR, "scripts", "new_codes_cs.txt"), "w", encoding="utf-8") as f:
    f.write(output)

print(f"\nСгенерировано {len(lines)} строк C# кода.")
print(f"Сохранено в: {os.path.join(BASE_DIR, 'scripts', 'new_codes_cs.txt')}")

# Статистика по категориям
print("\n--- Статистика по категориям ---")
for category in sorted(groups.keys()):
    print(f"  {category}: {len(groups[category])}")
