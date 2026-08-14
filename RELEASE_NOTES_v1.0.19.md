# CarDiagnosticApp v1.0.19 — Schema Release

## Что нового

### Схемы датчиков (9 схем + маркеры)
- **Lada Granta 1.6 8V** — схема ЭСУД с маркерами (ДМРВ, ДТОЖ, детонация, катушка)
- **Lada Kalina/Largus 1.6 8V** — общая схема 11189 ЭСУД
- **Lada Vesta 1.8 16V** — чертежи almarka.ru (ДПКВ, детонация)
- **Lada Vesta CVT JF015E** — датчики вариатора (4 датчика оборотов)
- **KAMAZ 43253 (740.62)** — чертёж каталога autoopt.ru
- **KAMAZ 6520 Cummins 6.7 Euro-4** — система нейтрализации (NOx, мочевина, EGT)
- **MAZ 4371 (ЯМЗ-5340)** — чертёж almarka.ru
- **NefAZ 5299 (ЯМЗ-536)** — 2 вида (справа/слева)

### DTC база (12 000+ кодов)
- Полная офлайн база OBD-II кодов
- 229 RU-оверлеев (Cummins SPN/FMI, Janvar-7, chiptuner)
- Поиск по коду и описанию

### Readiness Monitors
- Статус готовности систем (Mode 01 PID 01)
- MIL лампа, количество DTC

## Файлы для скачивания

| Файл | Размер | Описание |
|------|--------|----------|
| `CarDiagnosticApp_v1.0.19.apk` | 58.5 MB | Android (API 31+) |
| `CarDiagnosticApp_v1.0.19_Windows.zip` | 114.7 MB | Windows 10/11 (x64) |

## Установка

### Android
1. Скачай APK
2. Разреши установку из неизвестных источников
3. Установи и подключи ELM327 адаптер по Bluetooth

### Windows
1. Скачай ZIP
2. Распакуй в любую папку
3. Запусти `CarDiagnosticApp.exe`
4. При необходимости установи Windows App Runtime 1.7

## Что дальше

Оставшиеся схемы (19 авто) будут добавляться автоматически через Schema Scraper.
Запустить вручную: GitHub → Actions → Schema Auto-Scraper → Run workflow.

## Поддерживаемые авто

### Легковые
- Lada: Granta, Kalina, Priora, Largus, Vesta (1.6/1.8/CVT), Niva
- UAZ: Patriot

### Грузовики
- GAZ: GAZelle NEXT
- KAMAZ: 43253, 5490, 65115, 6520
- MAZ: 4370, 4371, 5440
- MMZ: Д-245, Д-260
- Ural: 4320

### Автобусы
- PAZ: Vector, 3205
- LiAZ: 5292
- NefAZ: 5299
