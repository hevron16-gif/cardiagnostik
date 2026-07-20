using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Репозиторий пользовательских данных: настройки, VIN-ы, предпочтения.
/// Все методы — асинхронные с lazy-инициализацией через Task.Run (StrictMode-safe).
/// </summary>
public class UserRepository
{
    private SQLiteAsyncConnection? _db;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private bool _initialized;

    private static readonly string DbPath = Path.Combine(
        FileSystem.AppDataDirectory, "user_data.db");

    /// <summary>
    /// Конструктор не делает I/O — StrictMode safe.
    /// Соединение открывается лениво при первом обращении к GetDbAsync().
    /// </summary>
    public UserRepository()
    {
    }

    /// <summary>
    /// Явная инициализация репозитория при старте приложения.
    /// Гарантирует готовность БД до первого доступа из страниц.
    /// </summary>
    public async Task InitializeAsync()
    {
        await GetDbAsync();
    }

    /// <summary>
    /// Возвращает готовое SQLite-соединение.
    /// Создаёт БД и таблицы на фоновом потоке через Task.Run.
    /// </summary>
    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null && _initialized) return _db;

        await _dbLock.WaitAsync();
        try
        {
            if (_db != null && _initialized) return _db;

            // Offload открытия SQLite на фоновый поток —
            // конструктор делает синхронное I/O (open файла БД).
            _db = await Task.Run(() =>
                new SQLiteAsyncConnection(DbPath,
                    SQLiteOpenFlags.ReadWrite |
                    SQLiteOpenFlags.Create |
                    SQLiteOpenFlags.SharedCache));

            await _db.ExecuteAsync("PRAGMA encoding = 'UTF-8';");
            await _db.CreateTableAsync<UserRecord>();

            // Индекс по ключу + тегу
            await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_user_key ON user_profile(key)");
            await _db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_user_tag ON user_profile(tag)");

            _initialized = true;
            System.Diagnostics.Debug.WriteLine("[UserRepository] Initialized.");
        }
        finally
        {
            _dbLock.Release();
        }

        return _db;
    }

    // ═══════════════════ CRUD ═══════════════════

    /// <summary>
    /// Сохраняет или обновляет значение по ключу.
    /// Потокобезопасно — использует GetDbAsync с double-check locking.
    /// </summary>
    public async Task SetAsync(string key, string value, string valueType = "string", string? tag = null)
    {
        var db = await GetDbAsync();
        var existing = await db.Table<UserRecord>()
            .Where(r => r.Key == key)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.Value = value;
            existing.ValueType = valueType;
            existing.UpdatedAt = DateTime.UtcNow;
            if (tag != null) existing.Tag = tag;
            await db.UpdateAsync(existing);
        }
        else
        {
            await db.InsertAsync(new UserRecord
            {
                Key = key,
                Value = value,
                ValueType = valueType,
                Tag = tag,
                UpdatedAt = DateTime.UtcNow,
            });
        }
    }

    /// <summary>
    /// Читает значение по ключу. Возвращает null если нет.
    /// </summary>
    public async Task<string?> GetAsync(string key)
    {
        var db = await GetDbAsync();
        var record = await db.Table<UserRecord>()
            .Where(r => r.Key == key)
            .FirstOrDefaultAsync();
        return record?.Value;
    }

    /// <summary>
    /// Читает значение как int, или defaultVal если нет.
    /// </summary>
    public async Task<int> GetIntAsync(string key, int defaultVal = 0)
    {
        var val = await GetAsync(key);
        return int.TryParse(val, out var result) ? result : defaultVal;
    }

    /// <summary>
    /// Читает значение как bool, или defaultVal если нет.
    /// </summary>
    public async Task<bool> GetBoolAsync(string key, bool defaultVal = false)
    {
        var val = await GetAsync(key);
        if (val == null) return defaultVal;
        return val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";
    }

    /// <summary>
    /// Удаляет запись по ключу.
    /// </summary>
    public async Task<bool> DeleteAsync(string key)
    {
        var db = await GetDbAsync();
        var record = await db.Table<UserRecord>()
            .Where(r => r.Key == key)
            .FirstOrDefaultAsync();
        if (record == null) return false;
        await db.DeleteAsync(record);
        return true;
    }

    /// <summary>
    /// Возвращает все записи с указанным тегом.
    /// </summary>
    public async Task<List<UserRecord>> GetByTagAsync(string tag)
    {
        var db = await GetDbAsync();
        return await db.Table<UserRecord>()
            .Where(r => r.Tag == tag)
            .OrderBy(r => r.Key)
            .ToListAsync();
    }

    /// <summary>
    /// Возвращает все записи (для отладки/админки).
    /// </summary>
    public async Task<List<UserRecord>> GetAllAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<UserRecord>()
            .OrderBy(r => r.Tag)
            .ThenBy(r => r.Key)
            .ToListAsync();
    }

    /// <summary>
    /// Пакетная запись нескольких ключей в одной транзакции.
    /// </summary>
    public async Task SetBatchAsync(Dictionary<string, string> keyValues, string? tag = null)
    {
        var db = await GetDbAsync();
        await db.RunInTransactionAsync(conn =>
        {
            foreach (var kv in keyValues)
            {
                var existing = conn.Table<UserRecord>()
                    .FirstOrDefault(r => r.Key == kv.Key);
                if (existing != null)
                {
                    existing.Value = kv.Value;
                    existing.UpdatedAt = DateTime.UtcNow;
                    if (tag != null) existing.Tag = tag;
                    conn.Update(existing);
                }
                else
                {
                    conn.Insert(new UserRecord
                    {
                        Key = kv.Key,
                        Value = kv.Value,
                        Tag = tag,
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
            }
        });
    }
}
