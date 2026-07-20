using SQLite;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис самообучения — накапливает знания по результатам диагнозов и фидбека.
/// </summary>
public class LearningDbService
{
    private SQLiteAsyncConnection? _db;
    private bool _initialized;

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_initialized && _db != null) return _db;

        var dbPath = Path.Combine(FileSystem.AppDataDirectory, "learning.db");
        _db = await Task.Run(() =>
            new SQLiteAsyncConnection(dbPath, SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.FullMutex));
        await _db.ExecuteAsync("PRAGMA encoding = 'UTF-8';");
        await _db.CreateTableAsync<LearnedKnowledge>();
        _initialized = true;
        return _db;
    }

    // ═══════════════════════════════════════════════════
    //  ЗАПИСЬ ЗНАНИЙ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Сохраняет результат диагноза в базу знаний.
    /// Если связка уже есть — обновляет счётчик и текст.
    /// </summary>
    public async Task RecordDiagnosisAsync(
        string errorCode, string carBrand, string carModel,
        string diagnosisText, string? summary = null,
        string? likelyCause = null, string? solutions = null)
    {
        var db = await GetDbAsync();
        var now = DateTime.UtcNow;

        var existing = await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == errorCode
                     && k.CarBrand == carBrand
                     && k.CarModel == carModel)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.LastDiagnosisText = diagnosisText;
            existing.OccurrenceCount++;
            existing.LastSeenAt = now;

            if (!string.IsNullOrWhiteSpace(summary))
                existing.DiagnosisSummary = summary;
            if (!string.IsNullOrWhiteSpace(likelyCause))
                existing.LikelyCause = likelyCause;
            if (!string.IsNullOrWhiteSpace(solutions))
                existing.KnownSolutions = solutions;

            await db.UpdateAsync(existing);
        }
        else
        {
            await db.InsertAsync(new LearnedKnowledge
            {
                ErrorCode = errorCode,
                CarBrand = carBrand,
                CarModel = carModel,
                LastDiagnosisText = diagnosisText,
                DiagnosisSummary = summary ?? "",
                LikelyCause = likelyCause ?? "",
                KnownSolutions = solutions ?? "",
                FirstSeenAt = now,
                LastSeenAt = now,
                OccurrenceCount = 1,
            });
        }
    }

    /// <summary>
    /// Записывает фидбек пользователя и пересчитывает Confidence.
    /// </summary>
    public async Task RecordFeedbackAsync(
        string errorCode, string carBrand, string carModel,
        bool wasHelpful)
    {
        var db = await GetDbAsync();

        var existing = await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == errorCode
                     && k.CarBrand == carBrand
                     && k.CarModel == carModel)
            .FirstOrDefaultAsync();

        if (existing == null) return;

        if (wasHelpful)
            existing.PositiveFeedback++;
        else
            existing.NegativeFeedback++;

        existing.RecalculateConfidence();
        await db.UpdateAsync(existing);
    }

    // ═══════════════════════════════════════════════════
    //  ЧТЕНИЕ ЗНАНИЙ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Ищет накопленные знания по связке ошибка+авто.
    /// Строгий поиск: сначала точное совпадение, потом частичное.
    /// </summary>
    public async Task<LearnedKnowledge?> GetKnowledgeAsync(string errorCode, string carBrand, string carModel)
    {
        var db = await GetDbAsync();

        // 1. Точное совпадение: код + марка + модель
        var exact = await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == errorCode
                     && k.CarBrand == carBrand
                     && k.CarModel == carModel)
            .FirstOrDefaultAsync();

        if (exact != null) return exact;

        // 2. Частичное: код + марка (любая модель)
        var partial = await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == errorCode
                     && k.CarBrand == carBrand)
            .OrderByDescending(k => k.Confidence)
            .FirstOrDefaultAsync();

        if (partial != null) return partial;

        // 3. Код ошибки (любая марка)
        return await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == errorCode)
            .OrderByDescending(k => k.Confidence)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Формирует enrichment-текст для AI-промпта из накопленных знаний.
    /// </summary>
    public async Task<string> BuildEnrichmentAsync(string errorCode, string carBrand, string carModel)
    {
        var knowledge = await GetKnowledgeAsync(errorCode, carBrand, carModel);
        if (knowledge == null) return "";

        var enrichment = knowledge.ToEnrichmentText();
        if (string.IsNullOrWhiteSpace(enrichment)) return "";

        return $"[ИСТОРИЯ ДИАГНОСТИКИ] {enrichment}";
    }

    // ═══════════════════════════════════════════════════
    //  СВЯЗАННЫЕ ОШИБКИ (корреляции)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Записывает, какие ошибки появляются вместе.
    /// </summary>
    public async Task RecordErrorCorrelationAsync(
        string primaryErrorCode, string carBrand, string carModel,
        List<string> otherErrorCodes)
    {
        if (otherErrorCodes.Count == 0) return;

        var db = await GetDbAsync();

        var knowledge = await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == primaryErrorCode
                     && k.CarBrand == carBrand
                     && k.CarModel == carModel)
            .FirstOrDefaultAsync();

        if (knowledge == null) return;

        var existingRelated = knowledge.RelatedErrors
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToHashSet();

        foreach (var code in otherErrorCodes)
        {
            if (code != primaryErrorCode)
                existingRelated.Add(code);
        }

        knowledge.RelatedErrors = string.Join(";", existingRelated.OrderBy(x => x));
        await db.UpdateAsync(knowledge);
    }

    /// <summary>
    /// Статистика по всем знаниям (для UI).
    /// </summary>
    public async Task<(int totalKnowledge, int highConfidence, int totalDiagnoses)> GetStatsAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<LearnedKnowledge>().ToListAsync();
        var highConf = all.Count(k => k.Confidence >= 0.7);
        var totalDiag = all.Sum(k => k.OccurrenceCount);
        return (all.Count, highConf, totalDiag);
    }

    /// <summary>
    /// Возвращает все записи знаний, подходящие для обогащения:
    /// с низкой достоверностью или давно не обновлявшиеся.
    /// </summary>
    public async Task<List<LearnedKnowledge>> GetStaleKnowledgeAsync(
        double maxConfidence = 0.6, int staleDays = 30)
    {
        var db = await GetDbAsync();
        var cutoff = DateTime.UtcNow.AddDays(-staleDays);

        var all = await db.Table<LearnedKnowledge>().ToListAsync();

        // Отбираем: низкая уверенность ИЛИ давно не обновлялись
        return all
            .Where(k => k.Confidence < maxConfidence || k.LastSeenAt < cutoff)
            .OrderBy(k => k.Confidence)    // сначала самые неуверенные
            .ThenBy(k => k.LastSeenAt)     // потом самые старые
            .ToList();
    }

    /// <summary>
    /// Обновляет решения (KnownSolutions) и повышает Confidence для записи.
    /// </summary>
    public async Task EnrichKnowledgeAsync(int id, string solutions, double confidenceBoost)
    {
        var db = await GetDbAsync();
        var record = await db.Table<LearnedKnowledge>().Where(k => k.Id == id).FirstOrDefaultAsync();
        if (record == null) return;

        // Дополняем решения
        if (!string.IsNullOrWhiteSpace(solutions))
        {
            var existing = (record.KnownSolutions ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            var incoming = solutions.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var s in incoming)
            {
                var trimmed = s.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && !existing.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    existing.Add(trimmed);
            }
            record.KnownSolutions = string.Join("; ", existing);
        }

        // Повышаем уверенность (но не выше 0.85 — чтобы фидбек пользователя оставался решающим)
        record.Confidence = Math.Min(0.85, record.Confidence + confidenceBoost);
        record.LastSeenAt = DateTime.UtcNow;

        await db.UpdateAsync(record);
    }

    /// <summary>
    /// Количество уникальных кодов ошибок в базе знаний.
    /// </summary>
    public async Task<int> GetUniqueErrorCodeCountAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<LearnedKnowledge>().ToListAsync();
        return all.Select(k => k.ErrorCode).Distinct().Count();
    }

    /// <summary>
    /// Количество записей знаний, добавленных после указанной даты.
    /// </summary>
    public async Task<int> GetNewSinceAsync(DateTime since)
    {
        var db = await GetDbAsync();
        var all = await db.Table<LearnedKnowledge>().ToListAsync();
        return all.Count(k => k.FirstSeenAt >= since);
    }

    /// <summary>
    /// Количество записей знаний, обновлённых (но не созданных) после указанной даты.
    /// </summary>
    public async Task<int> GetUpdatedSinceAsync(DateTime since)
    {
        var db = await GetDbAsync();
        var all = await db.Table<LearnedKnowledge>().ToListAsync();
        return all.Count(k => k.LastSeenAt >= since && k.FirstSeenAt < since);
    }

    /// <summary>
    /// Синхронизация из облака: upsert записи знаний по (errorCode, carBrand, carModel).
    /// </summary>
    public async Task UpsertSyncKnowledgeAsync(string errorCode, string carBrand, string carModel,
        string diagnosis, string source, double confidence)
    {
        var db = await GetDbAsync();

        var existing = await db.Table<LearnedKnowledge>()
            .Where(k => k.ErrorCode == errorCode
                     && k.CarBrand == carBrand
                     && k.CarModel == carModel)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(diagnosis))
            {
                var curr = (existing.KnownSolutions ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (!curr.Contains(diagnosis, StringComparer.OrdinalIgnoreCase))
                {
                    curr.Add(diagnosis);
                    existing.KnownSolutions = string.Join("; ", curr);
                }
            }
            existing.Confidence = Math.Max(existing.Confidence, confidence);
            existing.LastSeenAt = DateTime.UtcNow;
            await db.UpdateAsync(existing);
        }
        else
        {
            await db.InsertAsync(new LearnedKnowledge
            {
                ErrorCode = errorCode,
                CarBrand = carBrand,
                CarModel = carModel,
                LastDiagnosisText = diagnosis,
                KnownSolutions = diagnosis,
                DiagnosisSummary = source,
                Confidence = confidence,
                FirstSeenAt = DateTime.UtcNow,
                LastSeenAt = DateTime.UtcNow,
            });
        }
    }
}
