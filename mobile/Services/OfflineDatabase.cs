using SQLite;

namespace CarDiagnosticApp.Services;

// ═══════════════════════════════════════════════════════
// МОДЕЛИ ТАБЛИЦ
// ═══════════════════════════════════════════════════════

/// <summary>
/// Кеш результатов диагностики — чтобы при офлайне
/// повторить диагноз без сервера.
/// </summary>
[Table("offline_cache")]
public class OfflineCacheRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки (P0300, P0171...).</summary>
    [Indexed]
    public string ErrorCode { get; set; } = "";

    /// <summary>Марка авто (ВАЗ, ГАЗ...).</summary>
    [Indexed]
    public string CarBrand { get; set; } = "";

    /// <summary>Модель авто.</summary>
    public string CarModel { get; set; } = "";

    /// <summary>Полный текст диагноза от AI.</summary>
    public string Diagnosis { get; set; } = "";

    /// <summary>Краткий сниппет (200 символов).</summary>
    public string Snippet { get; set; } = "";

    /// <summary>Серверный timestamp или локальный.</summary>
    public string Timestamp { get; set; } = "";

    /// <summary>Сколько раз запрашивали офлайн.</summary>
    public int AccessCount { get; set; }

    /// <summary>Когда последний раз обращались (ISO).</summary>
    public string LastAccessedAt { get; set; } = "";

    /// <summary>Источник: online / offline / weekly_agent.</summary>
    public string Source { get; set; } = "online";
}

/// <summary>
/// Отзывы, ожидающие отправки на сервер (офлайн-очередь).
/// </summary>
[Table("pending_feedback")]
public class PendingFeedbackRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string ErrorCode { get; set; } = "";

    public bool Helpful { get; set; }
    public string? CarBrand { get; set; }
    public string? CarModel { get; set; }
    public string? Diagnosis { get; set; }
    public string? Comment { get; set; }

    /// <summary>Когда создан (ISO).</summary>
    public string CreatedAt { get; set; } = "";

    /// <summary>Счётчик попыток отправки.</summary>
    public int RetryCount { get; set; }
}

/// <summary>
/// Метаданные синхронизации: когда последний раз синхронизировались,
/// сколько записей загружено, версия схемы и т.д.
/// </summary>
[Table("sync_meta")]
public class SyncMetaRecord
{
    [PrimaryKey]
    public string Key { get; set; } = "";   // "last_sync", "total_downloaded", etc.

    public string Value { get; set; } = "";
}

/// <summary>
/// Кеш базы знаний — загруженные с сервера проверенные статьи
    /// (только verified или от weekly_agent).
/// </summary>
[Table("knowledge_cache")]
public class KnowledgeCacheRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string ErrorCode { get; set; } = "";

    [Indexed]
    public string CarBrand { get; set; } = "";

    public string CarModel { get; set; } = "";
    public string Diagnosis { get; set; } = "";
    public string Snippet { get; set; } = "";
    public string Source { get; set; } = "";        // "verified", "weekly_agent"
    public string Timestamp { get; set; } = "";
    public string? Sources { get; set; }            // JSON-массив URL-источников
}

/// <summary>
/// Очередь диагнозов, полученных офлайн — ждут отправки на сервер.
/// </summary>
[Table("pending_uploads")]
public class PendingUploadRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string ErrorCode { get; set; } = "";

    [Indexed]
    public string CarBrand { get; set; } = "";

    public string CarModel { get; set; } = "";
    public string Diagnosis { get; set; } = "";
    public string Source { get; set; } = "client_offline";  // "client_offline", "client_retest"
    public string CreatedAt { get; set; } = "";
    public int RetryCount { get; set; }
}

// ═══════════════════════════════════════════════════════
// БАЗА ДАННЫХ
// ═══════════════════════════════════════════════════════

/// <summary>
/// Единая локальная SQLite-база для офлайн-режима.
/// Объединяет кеш диагнозов, очередь отзывов, метаданные синхронизации
/// и кеш базы знаний в одном файле.
///
/// Использование:
///   var db = new OfflineDatabase();
///   await db.InitAsync();
///   await db.Cache.UpsertDiagnosisAsync(...);
///   await db.Feedback.EnqueueAsync(...);
/// </summary>
public class OfflineDatabase
{
    private SQLiteAsyncConnection? _conn;
    private bool _initialized;

    private readonly string _dbPath =
        Path.Combine(FileSystem.AppDataDirectory, "offline.db");

    // ── Подсервисы (удобный доступ к группам операций) ──

    public OfflineCacheOps Cache { get; private set; } = null!;
    public FeedbackQueueOps Feedback { get; private set; } = null!;
    public SyncMetaOps SyncMeta { get; private set; } = null!;
    public KnowledgeCacheOps Knowledge { get; private set; } = null!;
    public UploadQueueOps Uploads { get; private set; } = null!;

    // ═══════════════════════════════════════════════════
    // ИНИЦИАЛИЗАЦИЯ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Открывает БД, создаёт таблицы (однократно). Потокобезопасно.
    /// </summary>
    public async Task InitAsync()
    {
        if (_initialized) return;

        _conn = await Task.Run(() => new SQLiteAsyncConnection(_dbPath,
            SQLiteOpenFlags.ReadWrite |
            SQLiteOpenFlags.Create |
            SQLiteOpenFlags.SharedCache));

        await _conn.ExecuteAsync("PRAGMA encoding = 'UTF-8';");

        // Создаём все таблицы
        await _conn.CreateTableAsync<OfflineCacheRecord>();
        await _conn.CreateTableAsync<PendingFeedbackRecord>();
        await _conn.CreateTableAsync<SyncMetaRecord>();
        await _conn.CreateTableAsync<KnowledgeCacheRecord>();
        await _conn.CreateTableAsync<PendingUploadRecord>();

        // Инициализируем сервисы
        Cache = new OfflineCacheOps(_conn);
        Feedback = new FeedbackQueueOps(_conn);
        SyncMeta = new SyncMetaOps(_conn);
        Knowledge = new KnowledgeCacheOps(_conn);
        Uploads = new UploadQueueOps(_conn);

        _initialized = true;
    }

    /// <summary>
    /// Гарантирует готовность и возвращает соединение.
    /// </summary>
    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (!_initialized) await InitAsync();
        return _conn!;
    }

    /// <summary>
    /// Закрывает соединение (опционально).
    /// </summary>
    public async Task CloseAsync()
    {
        if (_conn != null)
        {
            await _conn.CloseAsync();
            _initialized = false;
        }
    }

    /// <summary>
    /// Очищает ВСЕ таблицы (для сброса офлайн-данных).
    /// </summary>
    public async Task WipeAsync()
    {
        var db = await GetDbAsync();
        await db.DeleteAllAsync<OfflineCacheRecord>();
        await db.DeleteAllAsync<PendingFeedbackRecord>();
        await db.DeleteAllAsync<SyncMetaRecord>();
        await db.DeleteAllAsync<KnowledgeCacheRecord>();
        await db.DeleteAllAsync<PendingUploadRecord>();
    }

    /// <summary>
    /// Статистика по всем таблицам.
    /// </summary>
    public async Task<string> GetStatsAsync()
    {
        var db = await GetDbAsync();
        var cache = await db.Table<OfflineCacheRecord>().CountAsync();
        var feedback = await db.Table<PendingFeedbackRecord>().CountAsync();
        var knowledge = await db.Table<KnowledgeCacheRecord>().CountAsync();
        var meta = await db.Table<SyncMetaRecord>().CountAsync();
        var uploads = await db.Table<PendingUploadRecord>().CountAsync();

        return $"Offline DB: {cache} cache, {feedback} pending feedback, " +
               $"{knowledge} knowledge, {uploads} uploads, {meta} meta records";
    }

    /// <summary>
    /// Удаляет устаревшие записи из всех офлайн-таблиц.
    /// Возвращает количество удалённых записей.
    /// </summary>
    public async Task<int> CleanupExpiredCacheAsync(int maxAgeDays = 30)
    {
        var db = await GetDbAsync();
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays).ToString("o");
        var removed = 0;

        // Офлайн-кеш диагнозов
        var oldCache = await db.Table<OfflineCacheRecord>()
            .Where(r => r.LastAccessedAt.CompareTo(cutoff) < 0)
            .ToListAsync();
        foreach (var r in oldCache) { await db.DeleteAsync(r); removed++; }

        // Устаревшие отзывы (более 10 попыток)
        var oldFeedback = await db.Table<PendingFeedbackRecord>()
            .Where(f => f.RetryCount > 10)
            .ToListAsync();
        foreach (var r in oldFeedback) { await db.DeleteAsync(r); removed++; }

        // Кеш знаний старше срока
        var oldKnowledge = await db.Table<KnowledgeCacheRecord>()
            .Where(k => k.Timestamp.CompareTo(cutoff) < 0)
            .ToListAsync();
        foreach (var r in oldKnowledge) { await db.DeleteAsync(r); removed++; }

        // Загрузки с > 10 попытками
        var oldUploads = await db.Table<PendingUploadRecord>()
            .Where(u => u.RetryCount > 10)
            .ToListAsync();
        foreach (var r in oldUploads) { await db.DeleteAsync(r); removed++; }

        return removed;
    }

    // ═══════════════════════════════════════════════════
    // ОПЕРАЦИИ КЕША ДИАГНОЗОВ
    // ═══════════════════════════════════════════════════

    public class OfflineCacheOps
    {
        private readonly SQLiteAsyncConnection _db;

        public OfflineCacheOps(SQLiteAsyncConnection db) => _db = db;

        /// <summary>
        /// Сохраняет или обновляет закешированный диагноз.
        /// </summary>
        public async Task UpsertAsync(string errorCode, string carBrand,
            string carModel, string diagnosis, string source = "online")
        {
            var existing = await _db.Table<OfflineCacheRecord>()
                .Where(r => r.ErrorCode == errorCode
                         && r.CarBrand == carBrand
                         && r.CarModel == carModel)
                .FirstOrDefaultAsync();

            var snippet = diagnosis.Length > 200
                ? diagnosis[..200] + "…"
                : diagnosis;

            if (existing != null)
            {
                existing.Diagnosis = diagnosis;
                existing.Snippet = snippet;
                existing.Timestamp = DateTime.UtcNow.ToString("o");
                existing.Source = source;
                existing.AccessCount++;
                existing.LastAccessedAt = DateTime.UtcNow.ToString("o");
                await _db.UpdateAsync(existing);
            }
            else
            {
                await _db.InsertAsync(new OfflineCacheRecord
                {
                    ErrorCode = errorCode,
                    CarBrand = carBrand,
                    CarModel = carModel,
                    Diagnosis = diagnosis,
                    Snippet = snippet,
                    Timestamp = DateTime.UtcNow.ToString("o"),
                    Source = source,
                    AccessCount = 1,
                    LastAccessedAt = DateTime.UtcNow.ToString("o"),
                });
            }
        }

        /// <summary>
        /// Ищет диагноз ТОЛЬКО для своей марки (и алиасов LADA/ВАЗ).
        /// Чужие марки НЕ возвращаются — иначе AI-диагностика «путает» авто.
        /// </summary>
        public async Task<OfflineCacheRecord?> FindAsync(string errorCode, string carBrand)
        {
            var code = (errorCode ?? "").Trim();
            if (string.IsNullOrEmpty(code)) return null;

            var aliases = CarDiagnosticApp.Data.DiagramDatabase.BrandAliases(carBrand)
                .Concat(new[] { carBrand ?? "" })
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Все записи по коду, фильтр по марке в памяти (алиасы)
            var byCode = await _db.Table<OfflineCacheRecord>()
                .Where(r => r.ErrorCode == code)
                .OrderByDescending(r => r.AccessCount)
                .ToListAsync();

            OfflineCacheRecord? hit = null;
            foreach (var a in aliases)
            {
                hit = byCode.FirstOrDefault(r =>
                    string.Equals(r.CarBrand, a, StringComparison.OrdinalIgnoreCase)
                    || CarDiagnosticApp.Data.DiagramDatabase.BrandsMatch(r.CarBrand, a));
                if (hit != null) break;
            }

            if (hit == null) return null;

            hit.AccessCount++;
            hit.LastAccessedAt = DateTime.UtcNow.ToString("o");
            try { await _db.UpdateAsync(hit); } catch { }
            return hit;
        }

        /// <summary>
        /// Только алиасы той же марки (не «любая марка»).
        /// </summary>
        public async Task<OfflineCacheRecord?> FindAnyBrandAsync(string errorCode, string? preferredBrand = null)
        {
            // Предпочтительная марка обязательна — без неё не отдаём чужой кеш
            if (string.IsNullOrWhiteSpace(preferredBrand))
                return null;
            return await FindAsync(errorCode, preferredBrand);
        }

        /// <summary>
        /// Сколько записей в кеше.
        /// </summary>
        public async Task<int> CountAsync() =>
            await _db.Table<OfflineCacheRecord>().CountAsync();

        /// <summary>
        /// Удаляет записи старше N дней (очистка).
        /// </summary>
        public async Task<int> PruneOldAsync(int daysOld = 90)
        {
            var cutoff = DateTime.UtcNow.AddDays(-daysOld).ToString("o");
            var old = await _db.Table<OfflineCacheRecord>()
                .Where(r => r.LastAccessedAt.CompareTo(cutoff) < 0)
                .ToListAsync();

            foreach (var r in old)
                await _db.DeleteAsync(r);

            return old.Count;
        }
    }

    // ═══════════════════════════════════════════════════
    // ОПЕРАЦИИ ОЧЕРЕДИ ОТЗЫВОВ
    // ═══════════════════════════════════════════════════

    public class FeedbackQueueOps
    {
        private readonly SQLiteAsyncConnection _db;

        public FeedbackQueueOps(SQLiteAsyncConnection db) => _db = db;

        /// <summary>
        /// Добавляет отзыв в очередь.
        /// </summary>
        public async Task EnqueueAsync(string errorCode, bool helpful,
            string? carBrand = null, string? carModel = null,
            string? diagnosis = null, string? comment = null)
        {
            await _db.InsertAsync(new PendingFeedbackRecord
            {
                ErrorCode = errorCode,
                Helpful = helpful,
                CarBrand = carBrand,
                CarModel = carModel,
                Diagnosis = diagnosis,
                Comment = comment,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                RetryCount = 0,
            });
        }

        /// <summary>
        /// Все ожидающие отзывы.
        /// </summary>
        public async Task<List<PendingFeedbackRecord>> GetAllAsync() =>
            await _db.Table<PendingFeedbackRecord>().ToListAsync();

        /// <summary>
        /// Удаляет запись после успешной отправки.
        /// </summary>
        public async Task RemoveAsync(PendingFeedbackRecord item) =>
            await _db.DeleteAsync(item);

        /// <summary>
        /// Увеличивает счётчик попыток.
        /// </summary>
        public async Task IncrementRetryAsync(PendingFeedbackRecord item)
        {
            item.RetryCount++;
            await _db.UpdateAsync(item);
        }

        /// <summary>
        /// Количество в очереди.
        /// </summary>
        public async Task<int> CountAsync() =>
            await _db.Table<PendingFeedbackRecord>().CountAsync();
    }

    // ═══════════════════════════════════════════════════
    // МЕТАДАННЫЕ СИНХРОНИЗАЦИИ
    // ═══════════════════════════════════════════════════

    public class SyncMetaOps
    {
        private readonly SQLiteAsyncConnection _db;

        public SyncMetaOps(SQLiteAsyncConnection db) => _db = db;

        /// <summary>
        /// Сохраняет или обновляет пару ключ-значение.
        /// </summary>
        public async Task SetAsync(string key, string value)
        {
            var existing = await _db.Table<SyncMetaRecord>()
                .Where(m => m.Key == key)
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                existing.Value = value;
                await _db.UpdateAsync(existing);
            }
            else
            {
                await _db.InsertAsync(new SyncMetaRecord { Key = key, Value = value });
            }
        }

        /// <summary>
        /// Читает значение по ключу, или null.
        /// </summary>
        public async Task<string?> GetAsync(string key)
        {
            var record = await _db.Table<SyncMetaRecord>()
                .Where(m => m.Key == key)
                .FirstOrDefaultAsync();
            return record?.Value;
        }

        /// <summary>
        /// Время последней синхронизации (ISO).
        /// </summary>
        public async Task<DateTime?> GetLastSyncTimeAsync()
        {
            var val = await GetAsync("last_sync");
            if (DateTime.TryParse(val, out var dt))
                return dt;
            return null;
        }

        /// <summary>
        /// Обновляет метку времени последней синхронизации.
        /// </summary>
        public async Task SetLastSyncTimeAsync(DateTime time) =>
            await SetAsync("last_sync", time.ToString("o"));

        /// <summary>
        /// Счётчик загруженных записей.
        /// </summary>
        public async Task<int> GetTotalDownloadedAsync()
        {
            var val = await GetAsync("total_downloaded");
            return int.TryParse(val, out var n) ? n : 0;
        }

        /// <summary>
        /// Увеличивает счётчик загруженных записей.
        /// </summary>
        public async Task AddToDownloadedAsync(int delta)
        {
            var current = await GetTotalDownloadedAsync();
            await SetAsync("total_downloaded", (current + delta).ToString());
        }

        /// <summary>Время последней синхронизации базы знаний.</summary>
        public async Task<DateTime?> GetLastKnowledgeSyncTimeAsync()
        {
            var val = await GetAsync("last_knowledge_sync");
            return DateTime.TryParse(val, out var dt) ? dt : null;
        }
        public async Task SetLastKnowledgeSyncTimeAsync(DateTime time) =>
            await SetAsync("last_knowledge_sync", time.ToString("o"));

        /// <summary>Время последней синхронизации схем.</summary>
        public async Task<DateTime?> GetLastDiagramSyncTimeAsync()
        {
            var val = await GetAsync("last_diagram_sync");
            return DateTime.TryParse(val, out var dt) ? dt : null;
        }
        public async Task SetLastDiagramSyncTimeAsync(DateTime time) =>
            await SetAsync("last_diagram_sync", time.ToString("o"));

        /// <summary>Время последней полной синхронизации (get_updates).</summary>
        public async Task<DateTime?> GetLastFullSyncTimeAsync()
        {
            var val = await GetAsync("last_full_sync");
            return DateTime.TryParse(val, out var dt) ? dt : null;
        }
        public async Task SetLastFullSyncTimeAsync(DateTime time) =>
            await SetAsync("last_full_sync", time.ToString("o"));

        /// <summary>Время последней синхронизации диагностик.</summary>
        public async Task<DateTime?> GetLastDiagnosticSyncTimeAsync()
        {
            var val = await GetAsync("last_diagnostic_sync");
            return DateTime.TryParse(val, out var dt) ? dt : null;
        }
        public async Task SetLastDiagnosticSyncTimeAsync(DateTime time) =>
            await SetAsync("last_diagnostic_sync", time.ToString("o"));
    }

    // ═══════════════════════════════════════════════════
    // КЕШ БАЗЫ ЗНАНИЙ
    // ═══════════════════════════════════════════════════

    public class KnowledgeCacheOps
    {
        private readonly SQLiteAsyncConnection _db;

        public KnowledgeCacheOps(SQLiteAsyncConnection db) => _db = db;

        /// <summary>
        /// Сохраняет статью из БЗ (upsert по ErrorCode + CarBrand).
        /// </summary>
        public async Task UpsertAsync(string errorCode, string carBrand,
            string carModel, string diagnosis, string source,
            string? sources = null)
        {
            var existing = await _db.Table<KnowledgeCacheRecord>()
                .Where(r => r.ErrorCode == errorCode && r.CarBrand == carBrand)
                .FirstOrDefaultAsync();

            var snippet = diagnosis.Length > 200
                ? diagnosis[..200] + "…"
                : diagnosis;

            if (existing != null)
            {
                existing.CarModel = carModel;
                existing.Diagnosis = diagnosis;
                existing.Snippet = snippet;
                existing.Source = source;
                existing.Sources = sources;
                existing.Timestamp = DateTime.UtcNow.ToString("o");
                await _db.UpdateAsync(existing);
            }
            else
            {
                await _db.InsertAsync(new KnowledgeCacheRecord
                {
                    ErrorCode = errorCode,
                    CarBrand = carBrand,
                    CarModel = carModel,
                    Diagnosis = diagnosis,
                    Snippet = snippet,
                    Source = source,
                    Sources = sources,
                    Timestamp = DateTime.UtcNow.ToString("o"),
                });
            }
        }

        /// <summary>
        /// Ищет статью по коду ошибки + марке.
        /// </summary>
        public async Task<KnowledgeCacheRecord?> FindAsync(string errorCode, string carBrand)
        {
            return await _db.Table<KnowledgeCacheRecord>()
                .Where(r => r.ErrorCode == errorCode && r.CarBrand == carBrand)
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
        }

        /// <summary>
        /// Все статьи для указанной марки (для наполнения офлайн-справочника).
        /// </summary>
        public async Task<List<KnowledgeCacheRecord>> GetByBrandAsync(string carBrand)
        {
            return await _db.Table<KnowledgeCacheRecord>()
                .Where(r => r.CarBrand == carBrand)
                .OrderBy(r => r.ErrorCode)
                .ToListAsync();
        }

        /// <summary>
        /// Поиск по фрагменту текста (код, марка, диагноз).
        /// </summary>
        public async Task<List<KnowledgeCacheRecord>> SearchAsync(string query)
        {
            var all = await _db.Table<KnowledgeCacheRecord>().ToListAsync();
            var q = query.ToUpperInvariant();
            return all
                .Where(r =>
                    (r.ErrorCode?.ToUpperInvariant().Contains(q) == true) ||
                    (r.CarBrand?.ToUpperInvariant().Contains(q) == true) ||
                    (r.Diagnosis?.ToUpperInvariant().Contains(q) == true))
                .OrderByDescending(r => r.Id)
                .ToList();
        }

        /// <summary>
        /// Количество статей в кеше.
        /// </summary>
        public async Task<int> CountAsync() =>
            await _db.Table<KnowledgeCacheRecord>().CountAsync();
    }

    // ═══════════════════════════════════════════════════
    // ОЧЕРЕДЬ ЗАГРУЗКИ ОФЛАЙН-ДИАГНОЗОВ НА СЕРВЕР
    // ═══════════════════════════════════════════════════

    public class UploadQueueOps
    {
        private readonly SQLiteAsyncConnection _db;

        public UploadQueueOps(SQLiteAsyncConnection db) => _db = db;

        /// <summary>
        /// Добавляет офлайн-диагноз в очередь на отправку.
        /// </summary>
        public async Task EnqueueAsync(string errorCode, string carBrand,
            string carModel, string diagnosis, string source = "client_offline")
        {
            await _db.InsertAsync(new PendingUploadRecord
            {
                ErrorCode = errorCode,
                CarBrand = carBrand,
                CarModel = carModel,
                Diagnosis = diagnosis,
                Source = source,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                RetryCount = 0,
            });
        }

        /// <summary>
        /// Все ожидающие загрузки.
        /// </summary>
        public async Task<List<PendingUploadRecord>> GetAllAsync() =>
            await _db.Table<PendingUploadRecord>().ToListAsync();

        /// <summary>
        /// Удаляет запись после успешной отправки.
        /// </summary>
        public async Task RemoveAsync(PendingUploadRecord item) =>
            await _db.DeleteAsync(item);

        /// <summary>
        /// Увеличивает счётчик попыток.
        /// </summary>
        public async Task IncrementRetryAsync(PendingUploadRecord item)
        {
            item.RetryCount++;
            await _db.UpdateAsync(item);
        }

        /// <summary>
        /// Количество в очереди.
        /// </summary>
        public async Task<int> CountAsync() =>
            await _db.Table<PendingUploadRecord>().CountAsync();
    }
}
