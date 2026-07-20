using CarDiagnosticApp.Models;
using Newtonsoft.Json;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// SQLite-хранилище справочника марок автомобилей.
/// Каждая марка — одна строка; список моделей хранится как JSON.
/// Обновляется агентом UpdateAgent раз в 2 недели.
/// </summary>
public class CarBrandCacheService
{
    private SQLiteAsyncConnection? _db;
    private bool _initialized;

    private static readonly string DbPath =
        Path.Combine(FileSystem.AppDataDirectory, "brands.db");

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (!_initialized)
        {
            _db = await Task.Run(() => new SQLiteAsyncConnection(DbPath,
                SQLiteOpenFlags.ReadWrite |
                SQLiteOpenFlags.Create |
                SQLiteOpenFlags.SharedCache));

            await _db.ExecuteAsync("PRAGMA encoding = 'UTF-8';");
            await _db.CreateTableAsync<CarBrandCacheRecord>();
            await _db.CreateTableAsync<AppMetaRecord>();
            await _db.CreateTableAsync<SearchLogRecord>();
            _initialized = true;
        }
        return _db!;
    }

    // ═══════════════════════════════════════════════════
    //  СОХРАНЕНИЕ (из API → SQLite)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Сохраняет список марок в SQLite.
    /// Обновляет существующие записи, добавляет новые,
    /// помечает устаревшие как inactive.
    /// </summary>
    public async Task SaveBrandsAsync(List<CarBrand> brands)
    {
        try
        {
            var db = await GetDbAsync();
            var now = DateTime.UtcNow;

            // Помечаем все как устаревшие (потом обновим те, что пришли)
            var allExisting = await db.Table<CarBrandCacheRecord>().ToListAsync();
            foreach (var r in allExisting)
            {
                r.IsActive = false;
                await db.UpdateAsync(r);
            }

            foreach (var brand in brands)
            {
                if (string.IsNullOrWhiteSpace(brand.brand)) continue;

                var existing = await db.Table<CarBrandCacheRecord>()
                    .Where(r => r.Brand == brand.brand)
                    .FirstOrDefaultAsync();

                var modelsJson = JsonConvert.SerializeObject(brand.models ?? new List<string>());

                if (existing != null)
                {
                    existing.ModelsJson = modelsJson;
                    existing.ModelCount = brand.models?.Count ?? 0;
                    existing.IsActive = true;
                    existing.UpdatedAt = now;
                    await db.UpdateAsync(existing);
                }
                else
                {
                    await db.InsertAsync(new CarBrandCacheRecord
                    {
                        Brand = brand.brand,
                        ModelsJson = modelsJson,
                        ModelCount = brand.models?.Count ?? 0,
                        IsActive = true,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CarBrandCache] Saved {brands.Count} brands to SQLite");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CarBrandCache] Save error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  ЗАГРУЗКА (из SQLite → List<CarBrand>)
    // ═══════════════════════════════════════════════════

    /// <summary>Загружает марки из SQLite.</summary>
    public async Task<List<CarBrand>?> LoadBrandsAsync()
    {
        try
        {
            var db = await GetDbAsync();
            var records = await db.Table<CarBrandCacheRecord>()
                .Where(r => r.IsActive)
                .OrderBy(r => r.Brand)
                .ToListAsync();

            if (records.Count == 0) return null;

            return records.Select(r => new CarBrand
            {
                brand = r.Brand,
                models = string.IsNullOrWhiteSpace(r.ModelsJson)
                    ? new List<string>()
                    : JsonConvert.DeserializeObject<List<string>>(r.ModelsJson) ?? new List<string>(),
            }).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CarBrandCache] Load error: {ex.Message}");
            return null;
        }
    }

    // ═══════════════════════════════════════════════════
    //  СПРАВОЧНАЯ ИНФОРМАЦИЯ
    // ═══════════════════════════════════════════════════

    /// <summary>Дата последнего обновления.</summary>
    public async Task<DateTime?> GetLastUpdateTimeAsync()
    {
        try
        {
            var db = await GetDbAsync();
            var record = await db.Table<CarBrandCacheRecord>()
                .OrderByDescending(r => r.UpdatedAt)
                .FirstOrDefaultAsync();
            return record?.UpdatedAt;
        }
        catch { return null; }
    }

    /// <summary>Количество активных марок в кеше.</summary>
    public async Task<int> GetBrandCountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<CarBrandCacheRecord>()
            .Where(r => r.IsActive)
            .CountAsync();
    }

    /// <summary>
    /// Ищет модели для конкретной марки.
    /// </summary>
    public async Task<List<string>?> GetModelsForBrandAsync(string brand)
    {
        var db = await GetDbAsync();
        var record = await db.Table<CarBrandCacheRecord>()
            .Where(r => r.Brand == brand && r.IsActive)
            .FirstOrDefaultAsync();

        if (record == null || string.IsNullOrWhiteSpace(record.ModelsJson))
            return null;

        return JsonConvert.DeserializeObject<List<string>>(record.ModelsJson);
    }

    // ═══════════════════════════════════════════════════
    //  МЕТА-ЗАПИСИ (проверка обновлений и др.)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Сохраняет результат проверки обновлений в SQLite.
    /// </summary>
    public async Task SaveUpdateCheckAsync(string currentVersion, string latestVersion, bool updateAvailable)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(new AppMetaRecord
        {
            Key = "last_update_check",
            Value = $"current={currentVersion};latest={latestVersion};available={updateAvailable}",
            Timestamp = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Возвращает последний результат проверки обновлений.
    /// </summary>
    public async Task<string?> GetLastUpdateCheckAsync()
    {
        var db = await GetDbAsync();
        var record = await db.Table<AppMetaRecord>()
            .Where(r => r.Key == "last_update_check")
            .OrderByDescending(r => r.Timestamp)
            .FirstOrDefaultAsync();
        return record?.Value;
    }

    /// <summary>
    /// Записывает поисковый запрос и его результат в журнал.
    /// </summary>
    public async Task LogSearchAsync(string searchType, string query, int resultsFound, string details)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(new SearchLogRecord
        {
            SearchType = searchType,
            Query = query,
            ResultsFound = resultsFound,
            Details = details,
            Timestamp = DateTime.UtcNow,
        });
    }
}

// ═══════════════════════════════════════════════════════
//  SQLite-модель
// ═══════════════════════════════════════════════════════

[Table("CarBrandCache")]
internal class CarBrandCacheRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Название марки (например "Toyota")</summary>
    [Indexed]
    public string Brand { get; set; } = "";

    /// <summary>Список моделей в JSON (например ["Camry","Corolla"])</summary>
    public string ModelsJson { get; set; } = "[]";

    /// <summary>Количество моделей (для быстрой статистики)</summary>
    public int ModelCount { get; set; }

    /// <summary>Активна ли марка (false = удалена с сервера)</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Таблица мета-данных приложения (проверки обновлений, статусы синхронизации).
/// Key-Value хранилище с временной меткой.
/// </summary>
[Table("AppMeta")]
internal class AppMetaRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string Key { get; set; } = "";

    public string Value { get; set; } = "";

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Журнал поисков: что искали, сколько нашли,
/// для отслеживания эффективности UpdateAgent.
/// </summary>
[Table("SearchLog")]
internal class SearchLogRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Тип поиска: new_codes, enrich, scheme_images, car_brands</summary>
    public string SearchType { get; set; } = "";

    /// <summary>Поисковый запрос</summary>
    public string Query { get; set; } = "";

    /// <summary>Количество найденных результатов</summary>
    public int ResultsFound { get; set; }

    /// <summary>Подробности (JSON — какие коды/решения найдены)</summary>
    public string Details { get; set; } = "";

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
