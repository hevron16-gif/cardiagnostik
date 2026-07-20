using System.Text;
using System.Text.Json;
using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис мониторинга конкурентов.
/// Управляет БД конкурентов, отслеживает изменения, генерирует аналитику.
/// </summary>
public class CompetitorService
{
    private readonly string _dbPath;
    private SQLiteAsyncConnection? _db;

    public CompetitorService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "competitors.db");
    }

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null)
            return _db;

        _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath));
        await _db.CreateTableAsync<Competitor>();
        await _db.CreateTableAsync<CompetitorChange>();
        return _db;
    }

    // ──────────────────────────────────────────────
    // CRUD
    // ──────────────────────────────────────────────

    public async Task<List<Competitor>> GetAllAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<Competitor>()
            .OrderByDescending(c => c.Rating)
            .ToListAsync();
    }

    public async Task<Competitor?> GetByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<Competitor>(id);
    }

    public async Task<int> SaveAsync(Competitor c)
    {
        var db = await GetDbAsync();

        if (c.Id > 0)
            await db.UpdateAsync(c);
        else
            await db.InsertAsync(c);

        return c.Id;
    }

    public async Task DeleteAsync(int id)
    {
        var db = await GetDbAsync();
        await db.DeleteAsync<Competitor>(id);
    }

    public async Task<int> CountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<Competitor>().CountAsync();
    }

    // ──────────────────────────────────────────────
    // Seed: начальный список известных конкурентов
    // ──────────────────────────────────────────────

    /// <summary>
    /// Заполняет БД начальным списком конкурентов (если пустая).
    /// Вызывается при первом запуске.
    /// </summary>
    public async Task<int> SeedDefaultCompetitorsAsync()
    {
        var db = await GetDbAsync();
        var existing = await db.Table<Competitor>().CountAsync();
        if (existing > 0)
            return 0;

        var defaults = new List<Competitor>
        {
            new() {
                Name = "Torque Pro", Developer = "Ian Hawkins",
                Platform = "android", Pricing = "paid", PriceUsd = 4.95,
                KeyFeatures = "[\"Кастомные дашборды\",\"HUD-режим\",\"Запись логов\",\"Поддержка MAP-сенсоров\",\"Плагины\"]",
                Strengths = "Самое популярное OBD2-приложение; огромная база PID-ов; гибкость",
                Weaknesses = "Устаревший UI; нет облачной синхронизации; нет русского языка",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            new() {
                Name = "Car Scanner ELM OBD2", Developer = "0vZ",
                Platform = "multi", Pricing = "freemium", PriceUsd = 6.99,
                KeyFeatures = "[\"Кастомные дашборды\",\"Графики реального времени\",\"Диагностика VAG/BMW/Ford\",\"Сброс сервисных интервалов\",\"HUD\"]",
                Strengths = "Отличная поддержка VAG (VCDS-подобная); русский язык; бесплатная версия функциональна",
                Weaknesses = "Нестабильное соединение на некоторых адаптерах; нет AI",
                HasAiFeatures = false, HasRussianLanguage = true,
            },
            new() {
                Name = "OBD Fusion", Developer = "OBD Solutions",
                Platform = "multi", Pricing = "paid", PriceUsd = 9.99,
                KeyFeatures = "[\"Расширенные PID\",\"Годовая подписка на доп. пакеты\",\"Отчёты PDF\",\"GPS-логирование\",\"Эмулятор режима движения\"]",
                Strengths = "Профессиональный уровень; доп. пакеты для конкретных марок",
                Weaknesses = "Дорого; подписка на доп. функции; нет русского языка",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            new() {
                Name = "inCarDoc", Developer = "PNN Soft",
                Platform = "multi", Pricing = "freemium", PriceUsd = 4.99,
                KeyFeatures = "[\"Чтение/сброс ошибок\",\"Живые данные\",\"Мониторинг\",\"Экспорт в CSV\"]",
                Strengths = "Простой интерфейс; русский язык; работает на iOS и Android",
                Weaknesses = "Ограниченная база PID-ов; нет облака; нет AI",
                HasAiFeatures = false, HasRussianLanguage = true,
            },
            new() {
                Name = "OBD Auto Doctor", Developer = "Creosys",
                Platform = "multi", Pricing = "freemium", PriceUsd = 14.95,
                KeyFeatures = "[\"Расширенная диагностика\",\"Мониторинг в реальном времени\",\"Режим эмуляции\",\"Отчёты\",\"Графики\"]",
                Strengths = "Мощный анализ; поддержка Mac/Windows; хорошие отчёты",
                Weaknesses = "Дорогая полная версия; устаревший интерфейс; нет русского языка",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            new() {
                Name = "DashCommand", Developer = "Palmer Performance",
                Platform = "multi", Pricing = "paid", PriceUsd = 9.99,
                KeyFeatures = "[\"Спортивные дашборды\",\"Скин-система\",\"Логирование\",\"Данные OBD-II\"]",
                Strengths = "Красивые спортивные панели; популярность среди энтузиастов",
                Weaknesses = "Заброшена разработка (последнее обновление 2019); нет облака; нет русского",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            new() {
                Name = "EOBD Facile", Developer = "Outils OBD",
                Platform = "multi", Pricing = "freemium", PriceUsd = 8.99,
                KeyFeatures = "[\"Чтение ошибок\",\"Графики\",\"Диагностика\",\"Экспорт PDF\"]",
                Strengths = "Простой и надёжный; французский и английский",
                Weaknesses = "Нет русского; ограниченные функции в бесплатной версии",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            new() {
                Name = "Carista", Developer = "Prizmos",
                Platform = "multi", Pricing = "subscription", PriceUsd = 49.99,
                KeyFeatures = "[\"Диагностика\",\"Кастомизация (кодирование)\",\"Сброс сервисных интервалов\",\"Поддержка VAG/BMW/Toyota/Lexus\"]",
                Strengths = "Кодирование без VCDS; удобный UI; регулярные обновления",
                Weaknesses = "Дорогая подписка; не все марки поддерживаются; нет русского",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            // ▸ Добавлены пользователем 2026-07-12
            new() {
                Name = "OpenDiag", Developer = "OpenDiag Team",
                Platform = "android", Pricing = "freemium", PriceUsd = 0,
                KeyFeatures = "[\"Чтение/сброс ошибок\",\"Живые данные\",\"Графики\",\"Кастомные дашборды\",\"Стоп-кадр\"]",
                Strengths = "Бесплатно; русский язык; поддержка российских авто (LADA, ГАЗ, УАЗ); активное сообщество",
                Weaknesses = "Только Android; ограниченная база PID-ов; нет AI",
                HasAiFeatures = false, HasRussianLanguage = true,
            },
            new() {
                Name = "OBDAI", Developer = "OBDAI",
                Platform = "multi", Pricing = "freemium", PriceUsd = 4.99,
                KeyFeatures = "[\"AI-анализ ошибок\",\"Чтение/сброс DTC\",\"Живые данные\",\"Облачная синхронизация\",\"История диагностики\"]",
                Strengths = "Встроенный AI-анализ; мультиплатформа (iOS/Android); облачное хранение истории",
                Weaknesses = "Новый продукт (мало отзывов); AI за платной подпиской; нет русского",
                HasAiFeatures = true, HasRussianLanguage = false,
            },
            new() {
                Name = "ИИ Автодоктор", Developer = "AI AutoDoctor",
                Platform = "multi", Pricing = "freemium", PriceUsd = 2.99,
                KeyFeatures = "[\"AI-диагностика ошибок\",\"Поиск схем и решений\",\"Чтение DTC\",\"Рекомендации по ремонту\",\"Графики\"]",
                Strengths = "AI для диагностики; русский язык; рекомендации по ремонту; доступная цена",
                Weaknesses = "Малоизвестный; ограниченная база марок; только базовые OBD2 PID",
                HasAiFeatures = true, HasRussianLanguage = true,
            },
            new() {
                Name = "Carly", Developer = "Carly Solutions GmbH",
                Platform = "multi", Pricing = "subscription", PriceUsd = 59.99,
                KeyFeatures = "[\"Диагностика BMW/VAG/MB\",\"Кодирование\",\"Сброс сервисных интервалов\",\"Подержанный авто-чек\",\"DPF-регенерация\",\"Адаптации\"]",
                Strengths = "Профессиональное кодирование; охват BMW/VAG/MB; облачное хранение; регулярные обновления",
                Weaknesses = "Очень дорогая подписка (~60€/год); требует фирменный адаптер; нет русского",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
            new() {
                Name = "OBDeleven", Developer = "Voltas IT",
                Platform = "multi", Pricing = "subscription", PriceUsd = 49.99,
                KeyFeatures = "[\"Диагностика VAG\",\"Кодирование одним кликом\",\"Адаптации\",\"Long coding\",\"Сброс сервиса\",\"DPF-регенерация\"]",
                Strengths = "VAG-специалист (как VCDS но дешевле); one-click кодирование; активное сообщество",
                Weaknesses = "Только VAG; дорогая PRO-подписка; требует фирменный адаптер; нет русского",
                HasAiFeatures = false, HasRussianLanguage = false,
            },
        };

        // Проставляем дату добавления
        foreach (var c in defaults)
            c.AddedAt = DateTime.UtcNow;

        await db.InsertAllAsync(defaults);
        return defaults.Count;
    }

    // ──────────────────────────────────────────────
    // История изменений
    // ──────────────────────────────────────────────

    public async Task<List<CompetitorChange>> GetHistoryAsync(int competitorId, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<CompetitorChange>()
            .Where(c => c.CompetitorId == competitorId)
            .OrderByDescending(c => c.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<CompetitorChange>> GetAllChangesAsync(int limit = 100)
    {
        var db = await GetDbAsync();
        return await db.Table<CompetitorChange>()
            .OrderByDescending(c => c.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task RecordChangeAsync(int competitorId, string changeType,
        string oldValue, string newValue)
    {
        if (oldValue == newValue)
            return; // нет изменений

        var db = await GetDbAsync();
        await db.InsertAsync(new CompetitorChange
        {
            CompetitorId = competitorId,
            ChangeType = changeType,
            OldValue = oldValue,
            NewValue = newValue,
            DetectedAt = DateTime.UtcNow,
        });
    }

    // ──────────────────────────────────────────────
    // Мониторинг: сравнить с ранее сохранённым
    // ──────────────────────────────────────────────

    /// <summary>
    /// Сравнивает переданные данные с сохранёнными и записывает изменения.
    /// Возвращает количество найденных изменений.
    /// </summary>
    public async Task<int> CompareAndRecordAsync(Competitor saved, Competitor fresh)
    {
        int changes = 0;

        async Task CheckAsync(string type, string oldVal, string newVal)
        {
            if (oldVal != newVal)
            {
                await RecordChangeAsync(saved.Id, type, oldVal, newVal);
                Interlocked.Increment(ref changes);
            }
        }

        if (fresh.LatestVersion != saved.LatestVersion && !string.IsNullOrEmpty(fresh.LatestVersion))
            await CheckAsync("version", saved.LatestVersion, fresh.LatestVersion);

        if (Math.Abs(fresh.Rating - saved.Rating) > 0.01 && !double.IsNaN(fresh.Rating))
            await CheckAsync("rating", saved.Rating.ToString("F1"), fresh.Rating.ToString("F1"));

        if (fresh.ReviewCount != saved.ReviewCount && fresh.ReviewCount > 0)
            await CheckAsync("reviews", saved.ReviewCount.ToString(), fresh.ReviewCount.ToString());

        if (fresh.Pricing != saved.Pricing && !string.IsNullOrEmpty(fresh.Pricing))
            await CheckAsync("pricing", saved.Pricing, fresh.Pricing);

        if (fresh.PriceUsd != saved.PriceUsd)
            await CheckAsync("price", saved.PriceUsd.ToString("F2"), fresh.PriceUsd.ToString("F2"));

        if (fresh.KeyFeatures != saved.KeyFeatures && fresh.KeyFeatures != "[]")
            await CheckAsync("features", saved.KeyFeatures, fresh.KeyFeatures);

        return changes;
    }

    // ──────────────────────────────────────────────
    // Аналитика
    // ──────────────────────────────────────────────

    /// <summary>
    /// Сводка для отчёта/UI.
    /// </summary>
    public async Task<string> BuildSummaryAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<Competitor>().Where(c => c.IsActive).ToListAsync();
        var allChanges = await db.Table<CompetitorChange>().CountAsync();

        var sb = new StringBuilder();
        sb.AppendLine($"📊 Конкурентов: {all.Count} (изменений: {allChanges})");

        if (all.Count == 0)
        {
            sb.AppendLine("   Нет данных.");
            return sb.ToString();
        }

        var avgRating = all.Where(c => !double.IsNaN(c.Rating)).Average(c => c.Rating);
        sb.AppendLine($"   Средний рейтинг: {avgRating:F1} ★");

        // Топ по рейтингу
        var top = all.Where(c => !double.IsNaN(c.Rating))
            .OrderByDescending(c => c.Rating)
            .Take(3)
            .ToList();
        if (top.Count > 0)
        {
            sb.AppendLine("   Топ-3:");
            for (int i = 0; i < top.Count; i++)
                sb.AppendLine($"     {i + 1}. {top[i].Name} ★{top[i].Rating:F1} ({top[i].ReviewCount} отзывов)");
        }

        // С AI
        var withAi = all.Count(c => c.HasAiFeatures);
        var withRu = all.Count(c => c.HasRussianLanguage);
        sb.AppendLine($"   С AI: {withAi} | Рус. язык: {withRu}");

        // Ценовые модели
        var freemium = all.Count(c => c.Pricing == "freemium");
        var paid = all.Count(c => c.Pricing == "paid");
        var sub = all.Count(c => c.Pricing == "subscription");
        sb.AppendLine($"   Бесплатно/условно: {freemium} | Платно: {paid} | Подписка: {sub}");

        return sb.ToString();
    }

    /// <summary>
    /// Генерирует конкурентный анализ (текст для отчёта).
    /// </summary>
    public async Task<string> GenerateReportAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<Competitor>().Where(c => c.IsActive).ToListAsync();
        var recentChanges = await db.Table<CompetitorChange>()
            .Where(c => c.DetectedAt >= DateTime.UtcNow.AddDays(-30))
            .ToListAsync();

        var sb = new StringBuilder();
        sb.AppendLine("═══════════════════════════════════════");
        sb.AppendLine("  КОНКУРЕНТНЫЙ АНАЛИЗ");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════");
        sb.AppendLine();

        sb.AppendLine($"Всего конкурентов в базе: {all.Count}");
        sb.AppendLine($"Активных: {all.Count(c => c.IsActive)}");
        sb.AppendLine($"Изменений за 30 дней: {recentChanges.Count}");
        sb.AppendLine();

        sb.AppendLine("── Сводка по конкурентам ──");
        foreach (var c in all.OrderByDescending(c => c.Rating))
        {
            sb.AppendLine($"  • {c.Name} (v{c.LatestVersion})");
            sb.AppendLine($"    ★{c.Rating:F1} | {c.Pricing} ${c.PriceUsd:F2} | Платформа: {c.Platform}");
            sb.AppendLine($"    AI: {(c.HasAiFeatures ? "✓" : "✗")} | RU: {(c.HasRussianLanguage ? "✓" : "✗")}");
            if (!string.IsNullOrEmpty(c.Strengths))
                sb.AppendLine($"    + {c.Strengths}");
            if (!string.IsNullOrEmpty(c.Weaknesses))
                sb.AppendLine($"    - {c.Weaknesses}");
            sb.AppendLine();
        }

        if (recentChanges.Count > 0)
        {
            sb.AppendLine("── Последние изменения ──");
            foreach (var ch in recentChanges.OrderByDescending(ch => ch.DetectedAt))
            {
                var comp = all.FirstOrDefault(c => c.Id == ch.CompetitorId);
                var name = comp?.Name ?? $"ID:{ch.CompetitorId}";
                sb.AppendLine($"  [{ch.DetectedAt:yyyy-MM-dd}] {name}: {ch.ChangeType} «{ch.OldValue}» → «{ch.NewValue}»");
            }
            sb.AppendLine();
        }

        sb.AppendLine("── Рекомендации ──");
        var ourStrengths = new List<string>();
        if (all.All(c => !c.HasAiFeatures))
            ourStrengths.Add("AI-диагностика — уникальное преимущество (нет у конкурентов)");
        if (all.All(c => !c.HasRussianLanguage))
            ourStrengths.Add("Русский язык — уникальное преимущество (нет у конкурентов)");
        if (all.Count(c => c.Pricing == "paid" || c.Pricing == "subscription") >= all.Count / 2)
            ourStrengths.Add("Конкурентоспособная цена относительно платных аналогов");

        foreach (var s in ourStrengths)
            sb.AppendLine($"  ✓ {s}");

        if (ourStrengths.Count == 0)
            sb.AppendLine("  (недостаточно данных для рекомендаций)");

        return sb.ToString();
    }

    // ──────────────────────────────────────────────
    // Авто-поиск: обновление данных из веба
    // (черновой вариант — DuckDuckGo + парсинг)
    // ──────────────────────────────────────────────

    /// <summary>
    /// Пытается обновить данные (версию, рейтинг) для всех конкурентов.
    /// Возвращает количество найденных изменений.
    /// </summary>
    public async Task<int> RefreshAllFromWebAsync()
    {
        int totalChanges = 0;
        var all = await GetAllAsync();

        foreach (var c in all.Where(c => c.IsActive).Take(5)) // не больше 5 за прогон
        {
            try
            {
                var fresh = await FetchFromPlayStoreAsync(c);
                if (fresh == null)
                    continue;

                int ch = await CompareAndRecordAsync(c, fresh);

                // Обновляем сохранённого конкурента
                c.LatestVersion = fresh.LatestVersion;
                c.Rating = fresh.Rating;
                c.ReviewCount = fresh.ReviewCount;
                c.Pricing = fresh.Pricing;
                c.PriceUsd = fresh.PriceUsd;
                c.LastCheckedAt = DateTime.UtcNow;
                c.LastVersionDate = fresh.LastVersionDate;
                await SaveAsync(c);

                totalChanges += ch;
            }
            catch
            {
                c.LastCheckedAt = DateTime.UtcNow;
                await SaveAsync(c);
            }

            await Task.Delay(1500); // вежливая пауза
        }

        return totalChanges;
    }

    /// <summary>
    /// Пытается получить данные из Google Play Store (через web-scrape DuckDuckGo).
    /// </summary>
    private async Task<Competitor?> FetchFromPlayStoreAsync(Competitor c)
    {
        // Пробуем найти страницу в Play Store по названию
        var query = $"site:play.google.com \"{c.Name}\" \"{c.Developer}\" OBD2";
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

        var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
        var html = await client.GetStringAsync(url);

        // Ищем ссылку на play.google.com
        var playLink = System.Text.RegularExpressions.Regex.Match(html,
            @"https?://play\.google\.com/store/apps/details\?id=[^\s""'<>]+");
        if (!playLink.Success)
            return null;

        var storeUrl = playLink.Value;
        var storeHtml = await client.GetStringAsync(storeUrl);

        var fresh = new Competitor { Name = c.Name, Developer = c.Developer };

        // Парсим рейтинг
        var ratingMatch = System.Text.RegularExpressions.Regex.Match(storeHtml,
            @"Rated\s+([\d.]+)\s+stars");
        if (ratingMatch.Success && double.TryParse(ratingMatch.Groups[1].Value,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var rating))
        {
            fresh.Rating = rating;
        }

        // Парсим количество отзывов
        var reviewsMatch = System.Text.RegularExpressions.Regex.Match(storeHtml,
            @"(\d[\d,]*)\s+(?:ratings|reviews|отзыв|оцен)");
        if (reviewsMatch.Success &&
            long.TryParse(reviewsMatch.Groups[1].Value.Replace(",", ""), out var reviews))
        {
            fresh.ReviewCount = reviews;
        }

        // Парсим версию
        var versionMatch = System.Text.RegularExpressions.Regex.Match(storeHtml,
            @"Current Version[^<]*<[^>]*>([^<]+)");
        if (versionMatch.Success)
            fresh.LatestVersion = versionMatch.Groups[1].Value.Trim();

        // Цена
        if (storeHtml.Contains("Install") || storeHtml.Contains("Установить"))
        {
            fresh.Pricing = "free";
            fresh.PriceUsd = 0;
        }

        fresh.StoreUrl = storeUrl;
        fresh.LastCheckedAt = DateTime.UtcNow;

        return fresh;
    }
}
