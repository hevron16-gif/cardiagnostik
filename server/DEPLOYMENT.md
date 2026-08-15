# Развёртывание на kitdiag.ru

## Доменная структура

| Поддомен | Назначение |
|----------|-----------|
| `kitdiag.ru` | Основной сайт (лендинг, скачивание) |
| `api.kitdiag.ru` | API сервер (FastAPI) |
| `www.kitdiag.ru` | Редирект на `kitdiag.ru` |

## Настройка DNS

В панели управления доменом (регистратор) добавьте записи:

```
Тип    Имя              Значение                          TTL
A      @                <IP сервера>                      3600
A      api              <IP сервера>                      3600
CNAME  www              kitdiag.ru                        3600
```

## Настройка сервера (Nginx)

### /etc/nginx/sites-available/kitdiag.ru

```nginx
# API сервер
server {
    listen 80;
    server_name api.kitdiag.ru;
    
    location / {
        proxy_pass http://localhost:8000;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}

# Основной сайт (статика)
server {
    listen 80;
    server_name kitdiag.ru www.kitdiag.ru;
    
    root /var/www/kitdiag.ru;
    index index.html;
    
    location / {
        try_files $uri $uri/ /index.html;
    }
    
    # Редирект www → без www
    if ($host = www.kitdiag.ru) {
        return 301 https://kitdiag.ru$request_uri;
    }
}
```

### SSL (Let's Encrypt)

```bash
sudo certbot --nginx -d kitdiag.ru -d www.kitdiag.ru -d api.kitdiag.ru
```

## Переменные окружения сервера

```bash
# .env
ENVIRONMENT=production
ADMIN_KEY=<секретный ключ>
DEEPSEEK_API_KEY=<ключ DeepSeek>
DATABASE_URL=<URL базы данных>
CORS_ORIGINS=https://kitdiag.ru,https://api.kitdiag.ru
LATEST_APK_URL=https://kitdiag.ru/download/CarDiagnosticApp.apk
```

## Сборка и деплой

```bash
# 1. Клонировать репозиторий
git clone https://github.com/hevron16-gif/cardiagnostik.git
cd cardiagnostik/server

# 2. Установить зависимости
pip install -r requirements.txt

# 3. Запустить сервер
uvicorn main:app --host 127.0.0.1 --port 8000 --workers 2
```

## Обновление приложения

При изменении URL в приложении:
1. Обновить `ApiService.cs`, `MauiProgram.cs`, `ConnectivityService.cs`
2. Собрать новую версию
3. Выложить APK на `kitdiag.ru/download/`
4. Обновить `LATEST_APK_URL` в `.env`
