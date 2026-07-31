using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Локальная SQLite-база для хранения истории диагностик.
/// Потокобезопасна через AsyncLock (SQLiteAsyncConnection сам потокобезопасен).
/// </summary>
public class LocalDatabase
{
    public static LocalDatabase Instance { get; } = new();
    private SQLiteAsyncConnection? _db;
    private bool _initialized;

    private readonly string _dbPath =
        Path.Combine(FileSystem.AppDataDirectory, "diagnostics.db");

    /// <summary>
    /// Инициализирует БД и создаёт таблицы (однократно).
    /// </summary>
    private async Task InitAsync()
    {
        if (_initialized) return;

        _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath,
            SQLiteOpenFlags.ReadWrite |
            SQLiteOpenFlags.Create |
            SQLiteOpenFlags.SharedCache));

        await _db.ExecuteAsync("PRAGMA encoding = 'UTF-8';");
        await _db.CreateTableAsync<HistoryRecord>();
        _initialized = true;
    }

    /// <summary>
    /// Гарантирует, что БД готова, и возвращает соединение.
    /// </summary>
    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        await InitAsync();
        return _db!;
    }

    // ═══════════════════════════════════════════════
    // CRUD
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Возвращает все записи из локальной БД (новые сверху).
    /// </summary>
    public async Task<List<HistoryRecord>> GetAllAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<HistoryRecord>()
            .OrderByDescending(r => r.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Возвращает записи как HistoryItem (для UI).
    /// </summary>
    public async Task<List<HistoryItem>> GetAllAsItemsAsync()
    {
        var records = await GetAllAsync();
        return records.Select(r => r.ToHistoryItem()).ToList();
    }

    /// <summary>
    /// Сохраняет или обновляет запись (ищет по ErrorCode + CarBrand + CarModel).
    /// Возвращает сохранённую запись.
    /// </summary>
    public async Task<HistoryRecord> UpsertAsync(HistoryRecord record)
    {
        var db = await GetDbAsync();

        // Ищем существующую запись по ключевым полям
        var existing = await db.Table<HistoryRecord>()
            .Where(r => r.ErrorCode == record.ErrorCode
                     && r.CarBrand == record.CarBrand
                     && r.CarModel == record.CarModel)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            // Обновляем серверные поля, сохраняем локальный статус
            existing.Snippet = record.Snippet;
            existing.Diagnosis = record.Diagnosis;
            existing.Timestamp = record.Timestamp;
            // Status не трогаем — сохраняем локальный
            await db.UpdateAsync(existing);
            return existing;
        }
        else
        {
            await db.InsertAsync(record);
            return record;
        }
    }

    /// <summary>
    /// Обновляет только статус записи.
    /// </summary>
    public async Task UpdateStatusAsync(int id, string status)
    {
        var db = await GetDbAsync();
        var record = await db.Table<HistoryRecord>()
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync();

        if (record != null)
        {
            record.Status = status;
            await db.UpdateAsync(record);
        }
    }

    /// <summary>
    /// Удаляет все записи из локальной БД.
    /// </summary>
    public async Task DeleteAllAsync()
    {
        var db = await GetDbAsync();
        await db.DeleteAllAsync<HistoryRecord>();
    }

    /// <summary>
    /// Количество записей в БД.
    /// </summary>
    public async Task<int> GetCountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<HistoryRecord>().CountAsync();
    }
}
