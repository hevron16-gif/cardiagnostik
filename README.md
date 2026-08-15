# KITDIAG — AI-диагностика автомобилей OBD2

[![Website](https://img.shields.io/badge/Website-kitdiag.ru-7c4dff)](https://kitdiag.ru)
[![API](https://img.shields.io/badge/API-api.kitdiag.ru-2196f3)](https://api.kitdiag.ru)

Офлайн/онлайн диагностика автомобилей по OBD2 (ELM327) + AI-разбор ошибок.

**Сайт:** [kitdiag.ru](https://kitdiag.ru)  
**API:** [api.kitdiag.ru](https://api.kitdiag.ru)

## Структура

| Папка | Описание |
|-------|----------|
| `server/` | Backend FastAPI (api.kitdiag.ru) |
| `mobile/` | Клиент .NET MAUI (Windows + Android) |
| `landing/` | Сайт kitdiag.ru |

## Backend (server)

### Локально

```bash
cd server
pip install -r requirements.txt
uvicorn main:app --host 0.0.0.0 --port 8000
```

Health: http://localhost:8000/health

### Production (kitdiag.ru)

- API: `https://api.kitdiag.ru`
- Документация: `https://api.kitdiag.ru/docs`
- CORS: настроен для `kitdiag.ru` и `api.kitdiag.ru`

Переменные окружения:

| Key | Описание |
|-----|----------|
| `DEEPSEEK_API_KEY` | AI-диагностика |
| `ADMIN_KEY` | панель администратора |
| `DATABASE_URL` | база данных |
| `CORS_ORIGINS` | `https://kitdiag.ru,https://api.kitdiag.ru` |

## Mobile (MAUI)

```bash
cd mobile
dotnet build -f net10.0-windows10.0.19041.0 -c Release
# или Android:
dotnet build -f net10.0-android -c Release
```

## Домен

- **kitdiag.ru** — основной сайт (лендинг + скачивание)
- **api.kitdiag.ru** — API сервер
- **www.kitdiag.ru** → редирект на kitdiag.ru

## Репозиторий

- GitHub: [hevron16-gif/cardiagnostik](https://github.com/hevron16-gif/cardiagnostik)
- 3D Telegram-бот: отдельный repo `3d-bot` (не смешивать)
