using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Хранит и извлекает историю ошибок по VIN автомобиля.
/// Использует SQLite той же БД, что и основная история диагностик.
/// </summary>
public class ErrorHistoryService
{
    private readonly string _dbPath;

    public ErrorHistoryService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "diagnostics.db");
    }

    private SQLiteAsyncConnection? _db;

    private bool _tableReady;

    private async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_db != null)
        {
            if (!_tableReady)
            {
                await _db.CreateTableAsync<CarErrorHistory>();
                _tableReady = true;
            }
            return _db;
        }
        _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath));
        await _db.ExecuteAsync("PRAGMA encoding = 'UTF-8';");
        await _db.CreateTableAsync<CarErrorHistory>();
        _tableReady = true;
        return _db;
    }

    /// <summary>
    /// Инициализирует таблицу car_error_history.
    /// </summary>
    public async Task InitAsync()
    {
        _ = await GetConnectionAsync();
    }

    /// <summary>
    /// Сохраняет одну или несколько ошибок для автомобиля с указанным VIN.
    /// Если ошибка уже встречалась на этом VIN — обновляет DetectedAt, но сохраняет оригинальный FirstSeenAt.
    /// </summary>
    public async Task SaveErrorsAsync(string vin, string brand, string model, List<ObdError> errors, string? scanSessionId = null)
    {
        var db = await GetConnectionAsync();
        var now = DateTime.Now;

        // Загружаем существующие ошибки по VIN, чтобы найти first seen
        var existing = await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin)
            .ToListAsync();

        foreach (var error in errors)
        {
            var match = existing.FirstOrDefault(r =>
                r.ErrorCode == error.Code && r.ErrorType == error.Type.ToString());

            if (match != null)
            {
                // Ошибка уже была: обновляем последнюю дату, но first seen не трогаем
                match.DetectedAt = now;
                match.Brand = brand;
                match.Model = model;
                match.AppearanceCount++;       // +1 появление

                // Если ошибку ранее сбрасывали — теперь она повторяющаяся
                if (match.ClearCount > 0)
                    match.IsRecurring = true;

                match.Diagnosed = false;       // сбрасываем — нужна новая диагностика
                match.ScanSessionId = scanSessionId;
                await db.UpdateAsync(match);
            }
            else
            {
                // Новая ошибка для этого VIN
                await db.InsertAsync(new CarErrorHistory
                {
                    VIN = vin,
                    Brand = brand,
                    Model = model,
                    ErrorCode = error.Code,
                    ErrorType = error.Type.ToString(),
                    DetectedAt = now,
                    FirstSeenAt = now,
                    Diagnosed = false,
                    ScanSessionId = scanSessionId
                });
            }
        }
    }

    /// <summary>
    /// Отмечает ошибку как продиагностированную и сохраняет фрагмент результата.
    /// </summary>
    public async Task MarkDiagnosedAsync(int recordId, string snippet)
    {
        var db = await GetConnectionAsync();
        var record = await db.Table<CarErrorHistory>().Where(r => r.Id == recordId).FirstOrDefaultAsync();
        if (record != null)
        {
            record.Diagnosed = true;
            record.DiagnosisSnippet = snippet?.Length > 500 ? snippet[..500] : snippet;
            await db.UpdateAsync(record);
        }
    }

    /// <summary>
    /// Возвращает все записи из истории ошибок (для синхронизации).
    /// </summary>
    public async Task<List<CarErrorHistory>> GetAllErrorsAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<CarErrorHistory>().OrderByDescending(r => r.DetectedAt).ToListAsync();
    }

    /// <summary>
    /// Сохраняет одиночную запись ошибки (для синхронизации из облака).
    /// </summary>
    public async Task SaveErrorAsync(string errorCode, string brand, string model, string errorType, string snippet)
    {
        var db = await GetConnectionAsync();
        var now = DateTime.UtcNow;
        // Проверяем дубликат
        var existing = await db.Table<CarErrorHistory>()
            .Where(r => r.ErrorCode == errorCode && r.Brand == brand && r.Model == model)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.LastSeenAt = now;
            existing.DiagnosisSnippet = snippet?.Length > 500 ? snippet[..500] : snippet;
            await db.UpdateAsync(existing);
        }
        else
        {
            await db.InsertAsync(new CarErrorHistory
            {
                VIN = "synced",
                Brand = brand,
                Model = model,
                ErrorCode = errorCode,
                ErrorType = errorType,
                DetectedAt = now,
                FirstSeenAt = now,
                LastSeenAt = now,
                DiagnosisSnippet = snippet,
                Diagnosed = false,
            });
        }
    }

    /// <summary>
    /// Возвращает список уникальных VIN, для которых есть история.
    /// </summary>
    public async Task<List<(string vin, string brand, string model, int errorCount, DateTime lastSeen)>> GetCarsAsync()
    {
        var db = await GetConnectionAsync();

        var all = await db.Table<CarErrorHistory>().OrderByDescending(r => r.DetectedAt).ToListAsync();

        return all
            .GroupBy(r => r.VIN)
            .Select(g => (
                vin: g.Key,
                brand: g.First().Brand,
                model: g.First().Model,
                errorCount: g.Select(r => r.ErrorCode).Distinct().Count(),
                lastSeen: g.Max(r => r.DetectedAt)
            ))
            .ToList();
    }

    /// <summary>
    /// Возвращает все повторяющиеся ошибки (сбрасывались и вернулись).
    /// </summary>
    public async Task<List<CarErrorHistory>> GetRecurringErrorsAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<CarErrorHistory>()
            .Where(r => r.IsRecurring)
            .OrderByDescending(r => r.DetectedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Возвращает повторяющиеся ошибки для конкретного VIN.
    /// </summary>
    public async Task<List<CarErrorHistory>> GetRecurringErrorsForVinAsync(string vin)
    {
        var db = await GetConnectionAsync();
        return await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin && r.IsRecurring)
            .OrderByDescending(r => r.DetectedAt)
            .ToListAsync();
    }
    /// <summary>
    /// Возвращает историю ошибок для конкретного VIN.
    /// </summary>
    public async Task<List<CarErrorHistory>> GetHistoryForVinAsync(string vin)
    {
        var db = await GetConnectionAsync();
        return await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin)
            .OrderByDescending(r => r.DetectedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Возвращает историю ошибок, добавленную после указанной даты.
    /// </summary>
    public async Task<List<CarErrorHistory>> GetHistorySinceAsync(DateTime since)
    {
        var db = await GetConnectionAsync();
        return await db.Table<CarErrorHistory>()
            .Where(r => r.DetectedAt > since)
            .OrderBy(r => r.DetectedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Удаляет записи истории ошибок старше указанного количества дней.
    /// Возвращает количество удалённых записей.
    /// </summary>
    public async Task<int> CleanupOldHistoryAsync(int maxAgeDays = 365)
    {
        var db = await GetConnectionAsync();
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var old = await db.Table<CarErrorHistory>()
            .Where(r => r.DetectedAt < cutoff)
            .ToListAsync();

        foreach (var r in old)
            await db.DeleteAsync(r);

        return old.Count;
    }

    /// <summary>
    /// Удаляет всю историю для конкретного VIN.
    /// </summary>
    public async Task DeleteVinAsync(string vin)
    {
        var db = await GetConnectionAsync();
        var records = await db.Table<CarErrorHistory>().Where(r => r.VIN == vin).ToListAsync();
        foreach (var r in records)
            await db.DeleteAsync(r);
    }

    /// <summary>
    /// Очищает всю историю ошибок.
    /// </summary>
    public async Task ClearAllAsync()
    {
        var db = await GetConnectionAsync();
        await db.DeleteAllAsync<CarErrorHistory>();
    }

    /// <summary>
    /// Отмечает все ошибки для VIN как сброшенные (Mode 04).
    /// Увеличивает счётчик сбросов и фиксирует время.
    /// </summary>
    public async Task<int> MarkClearedAsync(string vin)
    {
        var db = await GetConnectionAsync();
        var now = DateTime.Now;
        var records = await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin)
            .ToListAsync();

        foreach (var r in records)
        {
            r.ClearCount++;
            r.LastClearedAt = now;
            await db.UpdateAsync(r);
        }

        return records.Count;
    }

    /// <summary>
    /// Вычисляет риск ошибки по шкале 1–10 на основе
    /// категории, повторяемости, частоты появлений, сбросов, типа и давности.
    /// </summary>
    public static int CalculateRisk(CarErrorHistory error)
    {
        double score = 1;

        // ── Категория кода (0–3 балла) ──
        score += error.ErrorCode switch
        {
            string c when c.StartsWith("P00") || c.StartsWith("P01") => 3.0,
            string c when c.StartsWith("P02") => 2.5,
            string c when c.StartsWith("P03") => 2.5,
            string c when c.StartsWith("P04") => 2.0,
            string c when c.StartsWith("P05") => 1.5,
            string c when c.StartsWith("P06") => 1.5,
            string c when c.StartsWith("P07") => 2.0,
            string c when c.StartsWith("U") => 2.0,
            _ => 1.0
        };

        // ── Повторяемость (0–3 балла) ──
        if (error.IsRecurring)
        {
            score += 2.0;
            score += Math.Min(error.AppearanceCount * 0.3, 1.0);
        }

        // ── Сбросы — если вернулась после сброса ──
        if (error.ClearCount > 0)
            score += Math.Min(error.ClearCount * 0.5, 1.5);

        // ── Тип ошибки ──
        score += error.ErrorType switch
        {
            "Permanent" => 1.5,
            "Current" => 1.0,
            "Pending" => 0.5,
            _ => 0
        };

        // ── Давность: чем свежее — тем выше ──
        var days = (DateTime.Now - error.DetectedAt).TotalDays;
        if (days < 1) score += 1.0;
        else if (days < 3) score += 0.7;
        else if (days < 7) score += 0.4;
        else if (days < 30) score += 0.2;

        return Math.Clamp((int)Math.Round(score), 1, 10);
    }

    /// <summary>
    /// Пересчитывает риск для всех записей указанного VIN.
    /// </summary>
    public async Task RecalculateRiskForVinAsync(string vin)
    {
        var db = await GetConnectionAsync();
        var records = await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin)
            .ToListAsync();

        foreach (var r in records)
        {
            r.RiskScore = CalculateRisk(r);
            await db.UpdateAsync(r);
        }
    }

    /// <summary>
    /// Выявляет связки ошибок — группы кодов, которые появлялись одновременно
    /// в одних и тех же сессиях сканирования.
    /// Возвращает список связок с силой связи (0.0–1.0).
    /// </summary>
    public async Task<List<ErrorBundle>> DetectBundlesAsync(string vin, double minConfidence = 0.5)
    {
        var db = await GetConnectionAsync();
        var all = await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin && r.ScanSessionId != null)
            .ToListAsync();

        // Группируем коды по сессиям
        var sessions = all
            .GroupBy(r => r.ScanSessionId!)
            .Where(g => g.Count() >= 2) // только сессии с ≥2 ошибками
            .ToList();

        if (sessions.Count < 2) return new List<ErrorBundle>();

        // Собираем все уникальные коды
        var allCodes = all.Select(r => r.ErrorCode).Distinct().ToList();
        var bundles = new List<ErrorBundle>();

        // Для каждой пары кодов считаем совместные появления
        for (int i = 0; i < allCodes.Count; i++)
        {
            for (int j = i + 1; j < allCodes.Count; j++)
            {
                var codeA = allCodes[i];
                var codeB = allCodes[j];

                int together = 0;   // сессии, где оба кода вместе
                int onlyA = 0;      // сессии, где только A
                int onlyB = 0;      // сессии, где только B

                foreach (var session in sessions)
                {
                    var codes = session.Select(r => r.ErrorCode).ToHashSet();
                    bool hasA = codes.Contains(codeA);
                    bool hasB = codes.Contains(codeB);

                    if (hasA && hasB) together++;
                    else if (hasA) onlyA++;
                    else if (hasB) onlyB++;
                }

                int totalAppearances = together + onlyA + onlyB;
                if (totalAppearances == 0 || together < 2) continue;

                // Сила связи: Jaccard-like — сколько раз вместе / (вместе + порознь)
                double strength = (double)together / totalAppearances;

                if (strength >= minConfidence)
                {
                    bundles.Add(new ErrorBundle
                    {
                        CodeA = codeA,
                        CodeB = codeB,
                        TogetherCount = together,
                        OnlyACount = onlyA,
                        OnlyBCount = onlyB,
                        Strength = Math.Round(strength, 2),
                        Vin = vin
                    });
                }
            }
        }

        return bundles.OrderByDescending(b => b.Strength).ThenByDescending(b => b.TogetherCount).ToList();
    }

    /// <summary>
    /// Анализ тренда ошибок: растёт или падает частота появления.
    /// </summary>
    public async Task<string> GetHistoricalTrendAsync(string vin)
    {
        var db = await GetConnectionAsync();
        var all = await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin)
            .OrderBy(r => r.DetectedAt)
            .ToListAsync();

        if (all.Count < 3)
            return "Недостаточно данных для анализа тренда (нужно минимум 3 записи).";

        var parts = new List<string>();
        parts.Add("📈 Анализ тренда ошибок:");

        // Группируем по коду
        var byCode = all.GroupBy(r => r.ErrorCode);
        foreach (var group in byCode)
        {
            var sorted = group.OrderBy(r => r.DetectedAt).ToList();
            if (sorted.Count < 2) continue;

            var first = sorted.First();
            var last = sorted.Last();
            var days = (last.DetectedAt - first.DetectedAt).TotalDays;
            if (days < 0.5) days = 0.5;
            var freq = sorted.Count / days * 30; // ошибок в месяц

            // Определяем тренд: считаем средний интервал в первой половине vs второй
            var mid = sorted.Count / 2;
            var firstHalf = sorted.Take(mid).ToList();
            var secondHalf = sorted.Skip(mid).ToList();

            double firstInterval = firstHalf.Count > 1
                ? (firstHalf.Last().DetectedAt - firstHalf.First().DetectedAt).TotalDays / (firstHalf.Count - 1)
                : double.MaxValue;
            double secondInterval = secondHalf.Count > 1
                ? (secondHalf.Last().DetectedAt - secondHalf.First().DetectedAt).TotalDays / (secondHalf.Count - 1)
                : double.MaxValue;

            string trend;
            if (secondInterval < firstInterval * 0.7)
                trend = "⬆ УЧАЩАЕТСЯ";
            else if (secondInterval > firstInterval * 1.3)
                trend = "⬇ УРЕЖАЕТСЯ";
            else
                trend = "➡ СТАБИЛЬНО";

            double avgDays = days / sorted.Count;
            parts.Add($"- {group.Key}: {sorted.Count}× за {(int)days}д, ~{freq:F1}/мес, интервал ~{avgDays:F0}д — {trend}");
        }

        // Общий тренд по всем ошибкам
        var monthlyGroups = all
            .GroupBy(r => new { r.DetectedAt.Year, r.DetectedAt.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new { Month = $"{g.Key.Year}-{g.Key.Month:D2}", Count = g.Count() })
            .ToList();

        if (monthlyGroups.Count >= 3)
        {
            var firstMonths = monthlyGroups.Take(monthlyGroups.Count / 2).Sum(m => m.Count);
            var lastMonths = monthlyGroups.Skip(monthlyGroups.Count / 2).Sum(m => m.Count);
            parts.Add(string.Empty);
            if (lastMonths > firstMonths * 1.2)
                parts.Add("⚠ Общий тренд: количество ошибок РАСТЁТ. Рекомендуется углублённая диагностика.");
            else if (lastMonths < firstMonths * 0.8)
                parts.Add("✅ Общий тренд: количество ошибок СНИЖАЕТСЯ.");
            else
                parts.Add("Общий тренд: количество ошибок стабильно.");
        }

        return string.Join("\n", parts);
    }

    /// <summary>
    /// Полный исторический отчёт по VIN.
    /// </summary>
    public async Task<string> GetComprehensiveAnalysisAsync(string vin, string? brand = null, string? model = null)
    {
        var db = await GetConnectionAsync();
        var all = await db.Table<CarErrorHistory>()
            .Where(r => r.VIN == vin)
            .OrderByDescending(r => r.DetectedAt)
            .ToListAsync();

        if (all.Count == 0)
            return "Нет истории ошибок для анализа.";

        var parts = new List<string>();
        parts.Add($"# Анализ истории ошибок");
        if (!string.IsNullOrEmpty(brand))
            parts.Add($"Автомобиль: {brand} {model} (VIN: {vin})");
        else
            parts.Add($"VIN: {vin}");
        parts.Add($"Всего записей: {all.Count}");
        parts.Add(string.Empty);

        // Топ ошибок
        var topErrors = all
            .GroupBy(r => r.ErrorCode)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToList();

        parts.Add("## Топ-5 ошибок:");
        foreach (var g in topErrors)
        {
            var latest = g.First();
            parts.Add($"- {g.Key}: {g.Count()}×, впервые {latest.FirstSeenAt:dd.MM.yyyy}, риск {latest.RiskScore}/10, повтор: {(latest.IsRecurring ? "ДА" : "нет")}");
        }

        // Статистика
        var recurring = all.Count(r => r.IsRecurring);
        var permanent = all.Count(r => r.ErrorType == "Permanent");
        var avgRisk = all.Average(r => r.RiskScore);
        var maxRisk = all.Max(r => r.RiskScore);

        parts.Add(string.Empty);
        parts.Add("## Статистика:");
        parts.Add($"- Повторяющихся ошибок: {recurring}/{all.Count}");
        parts.Add($"- Постоянных (Permanent): {permanent}");
        parts.Add($"- Средний риск: {avgRisk:F1}/10");
        parts.Add($"- Максимальный риск: {maxRisk}/10");

        // Период наблюдений
        var firstDate = all.Min(r => r.DetectedAt);
        var lastDate = all.Max(r => r.DetectedAt);
        parts.Add($"- Период: {firstDate:dd.MM.yyyy} — {lastDate:dd.MM.yyyy} ({(lastDate - firstDate).Days} дн.)");

        // Тренд
        parts.Add(string.Empty);
        parts.Add(await GetHistoricalTrendAsync(vin));

        // Связки
        var bundles = await DetectBundlesAsync(vin, 0.5);
        if (bundles.Count > 0)
        {
            parts.Add(string.Empty);
            parts.Add("## Связанные ошибки (появляются вместе):");
            foreach (var b in bundles.Take(5))
                parts.Add($"- {b.CodeA} ↔ {b.CodeB}: сила {b.Strength:P0} (вместе {b.TogetherCount}×)");
        }

        return string.Join("\n", parts);
    }
}
