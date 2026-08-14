# Руководство по поиску схем датчиков (Schema Scraper)

## Как запустить scraper

### Локально
```bash
cd server
pip install pillow httpx
python schema_image_scraper.py
```

### Через GitHub Actions
1. Открой репозиторий на GitHub
2. Перейди во вкладку **Actions**
3. Выбери **Schema Auto-Scraper**
4. Нажми **Run workflow**

## Что уже найдено (9 схем)

| Бренд | Модель | Двигатель | Файл | Маркеры |
|-------|--------|-----------|------|---------|
| Lada | Granta | 1.6 8V | `Lada/Granta_1.6_8V_1.png` | ✅ |
| Lada | Kalina | 11189 1.6 8V | `Lada/Kalina_11189_1.6_8V_1.png` | ✅ |
| Lada | Largus | 11189 1.6 8V | `Lada/Largus_11189_1.6_8V_1.png` | ✅ |
| Lada | Vesta | 1.8 16V | `Lada/Vesta_1.8_16V_1.png`, `_2.png` | ✅ |
| Lada | Vesta | CVT JF015E | `Lada/Vesta_CVT_JF015E_1.png` | ✅ |
| KAMAZ | 43253 | 740.62 | `KAMAZ/43253_740.62_1.png` | ✅ |
| KAMAZ | 6520 | Cummins 6.7 | `KAMAZ/6520_Cummins_6.7_1.png` | ✅ |
| MAZ | 4371 | YMZ-5340 | `MAZ/4371_YMZ_5340_1.png` | ✅ |
| NefAZ | 5299 | YMZ-536 | `NefAZ/5299_YMZ_536_1.png`, `_2.png` | ✅ |

## Что нужно найти (19 авто без схем)

### Приоритет 1 — Легковые (популярные)

#### 1. Lada Niva 1.7 8V (21214)
**Поисковые запросы:**
```
Lada Niva 21214 engine diagram
VAZ 21214 1.7 engine scheme
схема двигателя Нива 21214 1.7
ВАЗ 21214 схема расположения датчиков
Niva 21214 ДПКВ датчик положения коленвала схема
```
**Ожидаемые датчики:** ДМРВ, ДПКВ, ДК1, ДК2, ДТОЖ, ДПДЗ, ДД

#### 2. Lada Priora 1.6 16V (21126)
**Поисковые запросы:**
```
Lada Priora 21126 engine diagram
VAZ 21126 engine sensors scheme
схема двигателя Лада Приора 21126
ВАЗ 21126 схема датчиков ЭСУД
Priora 21126 расположение датчиков
```
**Ожидаемые датчики:** ДМРВ, ДПКВ, ДПРВ, ДК1, ДК2, ДТОЖ, форсунки, катушки

#### 3. UAZ Patriot ZMZ-409
**Поисковые запросы:**
```
UAZ Patriot ZMZ 409 engine diagram
УАЗ Патриот ЗМЗ 409 схема двигателя
ZMZ 409 engine sensors layout
UAZ Patriot расположение датчиков схема
```
**Ожидаемые датчики:** ДМРВ, ДПКВ, ДК1, ДТОЖ, ДД, ДПДЗ

### Приоритет 2 — Грузовики

#### 4. GAZelle NEXT Cummins 2.8 ISF
**Поисковые запросы:**
```
GAZelle NEXT Cummins 2.8 ISF engine diagram
ГАЗель NEXT Cummins 2.8 схема двигателя
Cummins ISF 2.8 engine sensors diagram
ГАЗон NEXT схема расположения датчиков
```

#### 5. KAMAZ 5490 Cummins ISG (Евро-5)
**Поисковые запросы:**
```
KAMAZ 5490 Cummins ISG engine diagram
КАМАЗ 5490 Cummins ISG схема двигателя
Cummins ISG 12 engine sensors layout
КАМАЗ Евро-5 схема расположения датчиков
```

#### 6. KAMAZ 65115 Cummins 6.7
**Поисковые запросы:**
```
KAMAZ 65115 Cummins 6.7 engine diagram
КАМАЗ 65115 схема двигателя
Cummins 6.7 ISB engine sensors diagram
```

#### 7. MMZ Д-260
**Поисковые запросы:**
```
MMZ D-260 engine diagram
ММЗ Д-260 схема двигателя
D-260 diesel engine cross section
Д-260 Евро-2 схема расположения датчиков
```

#### 8. MAZ 5440 ЯМЗ-238
**Поисковые запросы:**
```
MAZ 5440 YMZ 238 engine diagram
МАЗ 5440 ЯМЗ 238 схема двигателя
YMZ 238 turbo engine scheme
ЯМЗ 238 турбо схема расположения датчиков
```

#### 9. MAZ 4370 ЯМЗ-236
**Поисковые запросы:**
```
MAZ 4370 YMZ 236 engine diagram
МАЗ 4370 ЯМЗ 236 схема двигателя
YMZ 236 engine cross section
ЯМЗ 236 схема разрез двигателя
```

#### 10. Ural 4320 ЯМЗ-236
**Поисковые запросы:**
```
Ural 4320 YMZ 236 engine diagram
Урал 4320 ЯМЗ 236 схема двигателя
Ural truck engine scheme
Урал схема расположения датчиков
```

### Приоритет 3 — Автобусы

#### 11. PAZ 3205 ЗМЗ-5234
**Поисковые запросы:**
```
PAZ 3205 ZMZ 5234 engine diagram
ПАЗ 3205 ЗМЗ 5234 схема двигателя
PAZ 3205 engine scheme
ПАЗ-3205 схема двигателя
```

#### 12. LiAZ 5292 Cummins 6.7
**Поисковые запросы:**
```
LiAZ 5292 Cummins engine diagram
ЛиАЗ 5292 Cummins схема двигателя
LiAZ bus engine scheme
ЛиАЗ схема расположения датчиков
```

## Как добавить маркеры на новую схему

1. Открой найденную картинку в любом редакторе (Paint, Photoshop)
2. Определи координаты датчиков в процентах (0.0 до 1.0)
3. Добавь в `schema_markers.py`:

```python
"Brand_Model_Engine": {
    "P0xxx": {  # код ошибки
        "x": 0.45, "y": 0.35, "color": "red",
        "label": "Название датчика"
    },
    # Связанные компоненты (синие)
    "component_name": {
        "x": 0.20, "y": 0.25, "color": "blue",
        "label": "Название компонента"
    }
}
```

## Советы по поиску качественных схем

1. **Лучшие источники:**
   - `almarka.ru` — чертежи сборки с указателями
   - `autopilot.rus` — фото-схемы ЭСУД
   - `autoopt.ru` — каталоги запчастей с чертежами
   - `fast-price.ru` — схемы систем нейтрализации
   - `lada-vesta.com.ru` — схемы трансмиссии

2. **Что искать:**
   - `схема двигателя` + модель
   - `расположение датчиков` + модель
   - `чертеж сборки` + двигатель
   - `элементы системы управления двигателем` + модель

3. **Что НЕ подходит:**
   - Фото товаров с белым фоном (Ozon, Wildberries)
   - Постеры, обои, иконки
   - IT-диаграммы, блок-схемы
   - Картинки без технических деталей

## Фильтрация мусора

Scraper автоматически отсекает:
- Стоковые фото (Shutterstock, Depositphotos)
- Соцсети (VK, Instagram, Pinterest)
- Маркетплейсы (Ozon, Wildberries, Amazon)
- Картинки < 400x300 пикселей
- Картинки с тёмным фоном (не чертежи)

## После добавления схем

1. Проверь что картинка в `server/schema_images/{brand}/`
2. Добавь маркеры в `schema_markers.py`
3. Протестируй через API: `GET /admin/schemas/list`
4. Закоммить: `git add server/schema_images/ server/schema_markers.py`
