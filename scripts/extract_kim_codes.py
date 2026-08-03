# -*- coding: utf-8 -*-
"""One-off: извлечь русские коды из файла Desktop/kim в JSON."""
import re, json, ast

text = open(r'C:/Users/User/Desktop/kim', encoding='utf-8').read()
# Контент внутри JSON-дампа: переводы строк как \n, кавычки экранированы — разэкранируем
text = text.replace('\\n', '\n').replace("\\'", "'").replace('\\"', '"')

# ("P0237", "описание", "high", "OBD-II", "УАЗ", "модель", "ЭБУ", "категория", '[...]', '[...]', 1, 0)
pat = re.compile(
    r'\("([PBUC]\d[0-9A-F]{3})",\s*"([^"]+)",\s*"(\w+)",\s*"([^"]*)",\s*'
    r'(?:"([^"]*)"|None),\s*(?:"([^"]*)"|None),\s*"([^"]*)",\s*"([^"]*)",\s*'
    r"'(\[.*?\])',\s*'(\[.*?\])',\s*(\d+),\s*(\d+)\)",
    re.DOTALL)

records = {}
fails = 0
for m in pat.finditer(text):
    code, desc, sev, std, make, model, ecu, cat, causes, sols, ru, gas = m.groups()
    try:
        causes_l = ast.literal_eval(causes.replace('\\n', ' '))
        sols_l = ast.literal_eval(sols.replace('\\n', ' '))
    except Exception:
        fails += 1
        continue
    records[code] = {
        "description": desc, "severity": sev, "make": make, "model": model,
        "ecu": ecu, "category": cat, "causes": causes_l, "solutions": sols_l,
    }

print("извлечено:", len(records), "| ошибок парсинга:", fails)
import collections
print("марки:", dict(collections.Counter(v['make'] for v in records.values())))
print("severity:", dict(collections.Counter(v['severity'] for v in records.values())))
print("P0237:", json.dumps(records.get('P0237'), ensure_ascii=False)[:250])

out = r'C:/Users/User/source/repos/cardiagnostik/scripts/source-data/kim_russian_codes.json'
import os
os.makedirs(os.path.dirname(out), exist_ok=True)
json.dump(records, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print("записано:", out)
