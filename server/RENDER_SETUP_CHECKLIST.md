# Чеклист настройки kitdiag.ru на Render (Paid)

## ✅ Шаг 1: DNS (reg.ru) — сделано, ждём обновления

## 🔄 Шаг 2: API сервис (cardiagnostik)

### 2.1 Добавить Custom Domain
1. https://dashboard.render.com → выбери сервис `cardiagnostik`
2. Settings → Custom Domains
3. Add Custom Domain → введи: `api.kitdiag.ru`
4. Render покажет CNAME запись, например:
   ```
   CNAME  api  → cardiagnostik.onrender.com
   ```
5. **Обнови DNS в reg.ru** — замени текущую запись на ту, что дал Render

### 2.2 Переменные окружения
В Settings → Environment добавь/обнови:
```
CORS_ORIGINS=https://kitdiag.ru,https://api.kitdiag.ru
ENVIRONMENT=production
```

### 2.3 Перезапустить сервис
Deploy → Manual Deploy → Deploy latest commit

---

## 🔄 Шаг 3: Static Site (лендинг)

### 3.1 Создать новый Static Site
1. Dashboard → New + → Static Site
2. Connect GitHub repo: `hevron16-gif/cardiagnostik`
3. Настройки:
   - **Name:** `kitdiag-landing`
   - **Branch:** `main`
   - **Root Directory:** `landing`
   - **Build Command:** *(оставь пустым)*
   - **Publish Directory:** `./`
4. Create Static Site

### 3.2 Добавить Custom Domains
1. Settings → Custom Domains
2. Add `kitdiag.ru`
3. Add `www.kitdiag.ru`
4. Render покажет CNAME записи

### 3.3 Обновить DNS в reg.ru
Добавь/обнови записи по инструкции Render:
```
CNAME  @    → <kitdiag-landing>.onrender.com
CNAME  www  → <kitdiag-landing>.onrender.com
```

---

## ✅ Шаг 4: Проверка

Подожди 5-10 минут после обновления DNS, затем проверь:

```bash
# API
curl https://api.kitdiag.ru/health

# Должно вернуть:
# {"status":"healthy","version":"1.0.20",...}

# Сайт
curl -I https://kitdiag.ru

# Должен вернуть HTTP 200
```

---

## ✅ Шаг 5: Обновить приложение

Когда домены заработают, собери новую версию приложения:
- URL уже обновлён на `api.kitdiag.ru`
- Собрать APK/EXE
- Выложить на GitHub Releases

---

## Текущий статус:
- [x] Домен куплен (kitdiag.ru)
- [x] DNS настроен (reg.ru)
- [x] Render оплачен
- [ ] Custom Domain для API
- [ ] Custom Domain для сайта
- [ ] Переменные окружения
- [ ] Проверка работы
