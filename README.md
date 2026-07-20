# CarDiagnostik (AutoDiag AI)

Офлайн/онлайн диагностика автомобилей по OBD2 (ELM327) + AI-разбор ошибок.

## Структура

| Папка | Описание |
|-------|----------|
| `server/` | Backend FastAPI (Render / локально) |
| `mobile/` | Клиент .NET MAUI (Windows + Android) |

## Backend (server)

### Локально

```bash
cd server
pip install -r requirements.txt
uvicorn main:app --host 0.0.0.0 --port 8000
```

Health: http://localhost:8000/health

### Render

- Root Directory: `server` (или Blueprint `render.yaml` в корне)
- Build: `pip install -r requirements.txt`
- Start: `uvicorn main:app --host 0.0.0.0 --port $PORT`
- Health Check: `/health`

Переменные окружения:

| Key | Описание |
|-----|----------|
| `DEEPSEEK_API_KEY` | AI-диагностика |
| `LICENSE_SECRET` | лицензии (опционально) |
| `TELEGRAM_BOT_TOKEN` | уведомления (опционально) |

## Mobile (MAUI)

```bash
cd mobile
dotnet build -f net10.0-windows10.0.19041.0 -c Release
# или Android:
dotnet build -f net10.0-android -c Release
```

## Репозитории

- Этот repo: **cardiagnostik** (сервер + мобильный клиент)
- 3D Telegram-бот: отдельный repo `3d-bot` (не смешивать)
