# Настройка кастомного домена на Render

## Шаг 1: DNS настроен (reg.ru) ✅

Ждём обновления DNS (до 24 часов).

## Шаг 2: Настройка Custom Domain на Render

### 2.1 Зайди в Dashboard Render
https://dashboard.render.com

### 2.2 Выбери свой сервис (cardiagnostik)

### 2.3 Перейди во вкладку "Settings" → "Custom Domains"

### 2.4 Добавь домены:

1. Нажми "Add Custom Domain"
2. Введи: `api.kitdiag.ru`
3. Render покажет CNAME запись, которую нужно добавить в DNS

Примерно так:
```
CNAME  api  → <service-name>.onrender.com
```

> **Важно:** Если reg.ru не принимает CNAME для `@` (корневой домен), используй A-запись с IP Render. Но лучше — CNAME для поддомена `api`.

### 2.5 Для основного сайта (kitdiag.ru)

У Render есть ограничение — бесплатные сервисы не поддерживают кастомные домены для статики.

**Варианты:**

#### Вариант A: Оставить API на Render, сайт на GitHub Pages (бесплатно)

1. Создай репозиторий `kitdiag-landing`
2. Залей `landing/index.html`
3. Включи GitHub Pages в настройках репозитория
4. В Custom Domain укажи `kitdiag.ru`
5. В DNS reg.ru добавь:
   ```
   A      @      185.199.108.153   (GitHub Pages IP)
   A      @      185.199.109.153
   A      @      185.199.110.153
   A      @      185.199.111.153
   CNAME  www    hevron16-gif.github.io
   ```

#### Вариант B: Всё на Render (платно $7/мес)

1. Апгрейд сервиса до paid plan
2. Добавить Static Site для лендинга
3. Подключить `kitdiag.ru` как custom domain

#### Вариант C: VPS (платно ~300₽/мес)

Полный контроль, но нужно настраивать самому.

## Шаг 3: Проверка

После настройки проверь:

```bash
# API
curl https://api.kitdiag.ru/health

# Сайт
curl https://kitdiag.ru
```

## Шаг 4: SSL (автоматически)

Render автоматически выпускает SSL через Let's Encrypt для кастомных доменов.

GitHub Pages тоже автоматически (HTTPS включён по умолчанию).

## Шаг 5: Обновление приложения

Когда домен заработает, обнови URL в приложении (уже сделано):
- `ApiService.cs` → `https://api.kitdiag.ru`
- `MauiProgram.cs` → `https://api.kitdiag.ru`
- `ConnectivityService.cs` → `https://api.kitdiag.ru`

Пересобери и выложи новую версию.
