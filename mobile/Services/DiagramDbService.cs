using CarDiagnosticApp.Data;
using CarDiagnosticApp.Models;
using Newtonsoft.Json;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// SQLite-хранилище схем узлов.
/// Мигрирует схемы из старого JSON-файла при первом запуске.
/// </summary>
public class DiagramDbService
{
    private SQLiteAsyncConnection? _db;
    private bool _initialized;
    private static bool _migrationDone;

    // Не трогаем FileSystem в field-initializer (WinUI/MAUI — краш 0xc000027b вне UI).
    private readonly string _dbPath = ResolveDbPath();

    private static string ResolveDbPath()
    {
        try
        {
            var dir = FileSystem.AppDataDirectory;
            if (!string.IsNullOrWhiteSpace(dir))
                return Path.Combine(dir, "diagrams.db");
        }
        catch { /* ignore */ }

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CarDiagnosticApp");
        try { Directory.CreateDirectory(fallback); } catch { }
        return Path.Combine(fallback, "diagrams.db");
    }

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (!_initialized)
        {
            _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath,
                SQLiteOpenFlags.ReadWrite |
                SQLiteOpenFlags.Create |
                SQLiteOpenFlags.SharedCache));

            await _db.ExecuteAsync("PRAGMA encoding = 'UTF-8';");
            await _db.CreateTableAsync<DiagramRecord>();
            await _db.CreateTableAsync<PendingDiagramRequest>();
            _initialized = true;

            // Однократная миграция из старого JSON
            if (!_migrationDone)
            {
                _migrationDone = true;
                await MigrateFromJsonAsync();
            }
        }
        return _db!;
    }

    // ═══════════════════════════════════════════════════
    //  ПРОВЕРКА НАЛИЧИЯ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Быстрая проверка: есть ли хотя бы одна схема для этой комбинации.
    /// </summary>
    public async Task<bool> HasDiagramAsync(string brand, string model, string errorCode)
    {
        var db = await GetDbAsync();
        var count = await db.Table<DiagramRecord>()
            .Where(d => d.CarBrand == brand
                     && d.CarModel == model
                     && d.ErrorCode == errorCode)
            .CountAsync();
        return count > 0;
    }

    /// <summary>
    /// Проверяет, есть ли вообще какие-либо схемы для этого автомобиля.
    /// </summary>
    public async Task<bool> HasAnyDiagramForCarAsync(string brand, string model)
    {
        var db = await GetDbAsync();
        var count = await db.Table<DiagramRecord>()
            .Where(d => d.CarBrand == brand && d.CarModel == model)
            .CountAsync();
        return count > 0;
    }

    // ═══════════════════════════════════════════════════
    //  ПОЛУЧЕНИЕ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Возвращает схему по марке+модели+коду (с алиасами LADA≡ВАЗ, KAMAZ≡КАМАЗ).
    /// </summary>
    public async Task<EngineDiagram?> GetDiagramAsync(string brand, string model, string errorCode)
    {
        var db = await GetDbAsync();
        var code = (errorCode ?? "").Trim();

        // Сначала точное совпадение
        var record = await db.Table<DiagramRecord>()
            .Where(d => d.CarBrand == brand
                     && d.CarModel == model
                     && d.ErrorCode == code)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync();
        if (record != null)
            return DeserializeDiagram(record.DiagramJson);

        // Алиасы марок + любая модель
        var byCode = await db.Table<DiagramRecord>()
            .Where(d => d.ErrorCode == code)
            .OrderByDescending(d => d.Version)
            .ToListAsync();
        foreach (var r in byCode)
        {
            if (DiagramDatabase.BrandsMatch(r.CarBrand, brand))
                return DeserializeDiagram(r.DiagramJson);
        }

        return null;
    }

    /// <summary>
    /// Возвращает все схемы для марки (модель опциональна; LADA≡ВАЗ).
    /// </summary>
    public async Task<List<EngineDiagram>> GetDiagramsForCarAsync(string brand, string model)
    {
        var db = await GetDbAsync();
        // Не грузим всю таблицу — фильтр по алиасам марки
        var aliases = DiagramDatabase.BrandAliases(brand)
            .Concat(new[] { brand ?? "" })
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var all = await db.Table<DiagramRecord>()
            .OrderByDescending(d => d.Version)
            .Take(200)
            .ToListAsync();

        var matched = all
            .Where(r => !string.IsNullOrWhiteSpace(r.CarBrand) &&
                        (aliases.Any(a => string.Equals(a, r.CarBrand, StringComparison.OrdinalIgnoreCase))
                         || DiagramDatabase.BrandsMatch(r.CarBrand, brand)))
            .Where(r => string.IsNullOrWhiteSpace(model)
                        || string.IsNullOrWhiteSpace(r.CarModel)
                        || string.Equals(r.CarModel, model, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matched
            .Select(r => DeserializeDiagram(r.DiagramJson))
            .Where(d => d != null)
            .Cast<EngineDiagram>()
            .ToList();
    }

    /// <summary>
    /// Ищет схему по коду ошибки (без привязки к марке — fallback).
    /// Внимание: может вернуть схему чужой марки — предпочитайте GetDiagramByCodeForBrandAsync.
    /// </summary>
    public async Task<EngineDiagram?> GetDiagramByCodeAsync(string errorCode)
    {
        var db = await GetDbAsync();
        var record = await db.Table<DiagramRecord>()
            .Where(d => d.ErrorCode == errorCode)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync();

        return record != null ? DeserializeDiagram(record.DiagramJson) : null;
    }

    /// <summary>
    /// Ищет схему по коду с фильтром по марке (LADA≡ВАЗ, KAMAZ≡КАМАЗ).
    /// Не возвращает схемы чужих брендов — устраняет «перепутанные» данные КАМАЗ/ВАЗ.
    /// </summary>
    public async Task<EngineDiagram?> GetDiagramByCodeForBrandAsync(string errorCode, string brand)
    {
        var db = await GetDbAsync();
        var code = (errorCode ?? "").Trim();
        if (string.IsNullOrEmpty(code)) return null;

        var records = await db.Table<DiagramRecord>()
            .Where(d => d.ErrorCode == code)
            .OrderByDescending(d => d.Version)
            .ToListAsync();

        foreach (var record in records)
        {
            if (!DiagramDatabase.BrandsMatch(record.CarBrand, brand))
                continue;
            var diagram = DeserializeDiagram(record.DiagramJson);
            if (diagram != null) return diagram;
        }

        // Также ищем записи с пустым ErrorCode, но схемой этой марки
        var brandRecords = await db.Table<DiagramRecord>()
            .Where(d => d.ErrorCode == "" || d.ErrorCode == null)
            .ToListAsync();

        foreach (var record in brandRecords)
        {
            if (!DiagramDatabase.BrandsMatch(record.CarBrand, brand))
                continue;
            var diagram = DeserializeDiagram(record.DiagramJson);
            if (diagram != null && diagram.Views.Count > 0)
                return diagram;
        }

        return null;
    }

    // ═══════════════════════════════════════════════════
    //  СОХРАНЕНИЕ КАРТИНКИ-СХЕМЫ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Скачивает картинку по URL и сохраняет как локальную схему.
    /// Возвращает путь к локальному файлу.
    /// </summary>
    public async Task<string?> DownloadAndSaveImageDiagramAsync(
        string brand, string model, string errorCode,
        string imageUrl, string sourceUrl, string source = "internet")
    {
        try
        {
            var db = await GetDbAsync();

            // Каталог для сохранения картинок
            var dir = Path.Combine(FileSystem.AppDataDirectory, "schemes");
            Directory.CreateDirectory(dir);

            // Имя файла: brand_model_errorCode.jpg
            var safeName = $"{SanitizeFileName(brand)}_{SanitizeFileName(model)}_{SanitizeFileName(errorCode)}";
            var ext = ".jpg";
            if (imageUrl.Contains(".png", StringComparison.OrdinalIgnoreCase)) ext = ".png";
            else if (imageUrl.Contains(".webp", StringComparison.OrdinalIgnoreCase)) ext = ".webp";
            else if (imageUrl.Contains(".gif", StringComparison.OrdinalIgnoreCase)) ext = ".gif";

            var localPath = Path.Combine(dir, safeName + ext);

            // Скачиваем
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var imageBytes = await http.GetByteArrayAsync(imageUrl);

            // Сохраняем на диск
            await File.WriteAllBytesAsync(localPath, imageBytes);

            var now = DateTime.UtcNow;

            // Сохраняем/обновляем запись в SQLite
            var existing = await db.Table<DiagramRecord>()
                .Where(d => d.CarBrand == brand
                         && d.CarModel == model
                         && d.ErrorCode == errorCode
                         && d.ImagePath != "")
                .FirstOrDefaultAsync();

            if (existing != null)
            {
                existing.ImagePath = localPath;
                existing.SourceUrl = sourceUrl;
                existing.Source = source;
                existing.DiagramJson = "";  // сбрасываем векторную схему — теперь растровая
                existing.Version++;
                existing.UpdatedAt = now;
                await db.UpdateAsync(existing);
            }
            else
            {
                await db.InsertAsync(new DiagramRecord
                {
                    CarBrand = brand,
                    CarModel = model,
                    ErrorCode = errorCode,
                    ImagePath = localPath,
                    SourceUrl = sourceUrl,
                    Source = source,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }

            return localPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiagramDb] Download error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Проверяет, есть ли сохранённая картинка-схема.
    /// </summary>
    public async Task<string?> GetImageDiagramPathAsync(string brand, string model, string errorCode)
    {
        var db = await GetDbAsync();
        var record = await db.Table<DiagramRecord>()
            .Where(d => d.CarBrand == brand
                     && d.CarModel == model
                     && d.ErrorCode == errorCode
                     && d.ImagePath != "")
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync();

        if (record == null || string.IsNullOrWhiteSpace(record.ImagePath))
            return null;

        // Проверяем, что файл существует
        if (File.Exists(record.ImagePath))
            return record.ImagePath;

        // Файл пропал — чистим запись
        record.ImagePath = "";
        await db.UpdateAsync(record);
        return null;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "unknown" : clean.Trim();
    }

    // ═══════════════════════════════════════════════════
    //  СОХРАНЕНИЕ ВЕКТОРНОЙ СХЕМЫ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Сохраняет (или обновляет) схему в SQLite.
    /// </summary>
    public async Task SaveDiagramAsync(string brand, string model, string errorCode,
        EngineDiagram diagram, string source = "imported")
    {
        var db = await GetDbAsync();

        // Ищем существующую запись
        var existing = await db.Table<DiagramRecord>()
            .Where(d => d.CarBrand == brand
                     && d.CarModel == model
                     && d.ErrorCode == errorCode)
            .FirstOrDefaultAsync();

        var now = DateTime.UtcNow;

        if (existing != null)
        {
            existing.DiagramJson = SerializeDiagram(diagram);
            existing.Source = source;
            existing.Version++;
            existing.UpdatedAt = now;
            await db.UpdateAsync(existing);
        }
        else
        {
            await db.InsertAsync(new DiagramRecord
            {
                CarBrand = brand,
                CarModel = model,
                ErrorCode = errorCode,
                DiagramJson = SerializeDiagram(diagram),
                Source = source,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }

    // ═══════════════════════════════════════════════════
    //  МИГРАЦИЯ ИЗ JSON
    // ═══════════════════════════════════════════════════

    private async Task MigrateFromJsonAsync()
    {
        try
        {
            // Принудительно перечитываем mapping_*.json (с error_codes)
            DiagramDatabase.Reload();
            var allDiagrams = DiagramDatabase.GetAllDiagrams();
            if (allDiagrams == null || allDiagrams.Count == 0) return;

            var db = await GetDbAsync();
            var now = DateTime.UtcNow;

            // Версия seed: при обновлении mapping всегда перезаписываем local-записи
            const int localSeedVersion = 3;

            foreach (var (cacheKey, diagram) in allDiagrams)
            {
                if (diagram == null) continue;

                // Ключ кэша = нормализованная марка (ВАЗ, КАМАЗ, *), не "Lada Vesta"
                var brand = !string.IsNullOrWhiteSpace(diagram.CarBrand)
                    ? DiagramDatabase.NormalizeBrand(diagram.CarBrand)
                    : DiagramDatabase.NormalizeBrand(cacheKey);
                if (brand == "*") brand = "";
                var model = "";

                var existing = await db.Table<DiagramRecord>()
                    .Where(d => d.CarBrand == brand && d.CarModel == model && d.ErrorCode == ""
                             && d.Source == "local")
                    .FirstOrDefaultAsync();

                // Также ищем старые записи с неверной маркой из старой миграции
                existing ??= await db.Table<DiagramRecord>()
                    .Where(d => d.Source == "local" && d.ErrorCode == "" && d.CarBrand == cacheKey)
                    .FirstOrDefaultAsync();

                var json = SerializeDiagram(diagram);
                if (existing == null)
                {
                    await db.InsertAsync(new DiagramRecord
                    {
                        CarBrand = brand,
                        CarModel = model,
                        ErrorCode = "",
                        DiagramJson = json,
                        Source = "local",
                        Version = localSeedVersion,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
                else if (existing.Version < localSeedVersion ||
                         string.IsNullOrWhiteSpace(existing.DiagramJson) ||
                         !existing.DiagramJson.Contains("ErrorCodes", StringComparison.OrdinalIgnoreCase))
                {
                    // Перезаписываем устаревшие/пустые схемы (исправление error_codes и марок)
                    existing.CarBrand = brand;
                    existing.CarModel = model;
                    existing.DiagramJson = json;
                    existing.Source = "local";
                    existing.Version = localSeedVersion;
                    existing.UpdatedAt = now;
                    await db.UpdateAsync(existing);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                $"[DiagramDb] Local mapping seed complete: {allDiagrams.Count} diagrams.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DiagramDb] Migration error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  ОТЛОЖЕННЫЕ ЗАПРОСЫ (схемы, которые не нашлись)
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Сохраняет запрос на схему, которая не была найдена.
    /// </summary>
    public async Task SavePendingRequestAsync(string brand, string model, string errorCode, string searchQuery)
    {
        var db = await GetDbAsync();
        var now = DateTime.UtcNow;

        // Проверяем — может, уже есть такой запрос
        var existing = await db.Table<PendingDiagramRequest>()
            .Where(p => p.CarBrand == brand
                     && p.CarModel == model
                     && p.ErrorCode == errorCode
                     && p.Status == "pending")
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.RetryCount++;
            existing.LastRetryAt = now;
            existing.SearchQuery = searchQuery;
            await db.UpdateAsync(existing);
        }
        else
        {
            await db.InsertAsync(new PendingDiagramRequest
            {
                CarBrand = brand,
                CarModel = model,
                ErrorCode = errorCode,
                SearchQuery = searchQuery,
                CreatedAt = now,
                LastRetryAt = now,
                RetryCount = 1,
                Status = "pending",
            });
        }
    }

    /// <summary>
    /// Помечает запрос как выполненный (схема найдена).
    /// </summary>
    public async Task MarkRequestAsFoundAsync(string brand, string model, string errorCode)
    {
        var db = await GetDbAsync();
        var requests = await db.Table<PendingDiagramRequest>()
            .Where(p => p.CarBrand == brand
                     && p.CarModel == model
                     && p.ErrorCode == errorCode
                     && p.Status == "pending")
            .ToListAsync();

        foreach (var r in requests)
        {
            r.Status = "found";
            await db.UpdateAsync(r);
        }
    }

    /// <summary>
    /// Возвращает список невыполненных запросов (для повторного поиска).
    /// </summary>
    public async Task<List<PendingDiagramRequest>> GetPendingRequestsAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<PendingDiagramRequest>()
            .Where(p => p.Status == "pending")
            .OrderByDescending(p => p.RetryCount)
            .ToListAsync();
    }

    /// <summary>
    /// Количество ожидающих запросов.
    /// </summary>
    public async Task<int> GetPendingCountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<PendingDiagramRequest>()
            .Where(p => p.Status == "pending")
            .CountAsync();
    }

    /// <summary>
    /// Удаляет запросы со статусом pending, которые старше maxAgeDays
    /// или имеют больше maxRetries попыток.
    /// </summary>
    public async Task<int> CleanupAbandonedRequestsAsync(int maxRetries = 10, int maxAgeDays = 90)
    {
        var db = await GetDbAsync();
        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
        var removed = 0;

        var toRemove = await db.Table<PendingDiagramRequest>()
            .Where(p => p.Status == "pending" &&
                        (p.RetryCount > maxRetries || p.LastRetryAt < cutoff))
            .ToListAsync();

        foreach (var r in toRemove)
        {
            r.Status = "abandoned";
            await db.UpdateAsync(r);
            removed++;
        }

        return removed;
    }

    /// <summary>
    /// Общее количество схем в локальной базе.
    /// </summary>
    public async Task<int> GetDiagramCountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<DiagramRecord>().CountAsync();
    }

    /// <summary>
    /// Количество схем, добавленных после указанной даты.
    /// </summary>
    public async Task<int> GetNewDiagramsSinceAsync(DateTime since)
    {
        var db = await GetDbAsync();
        var all = await db.Table<DiagramRecord>().ToListAsync();
        return all.Count(d => d.CreatedAt >= since);
    }

    // ═══════════════════════════════════════════════════
    //  СЕРИАЛИЗАЦИЯ
    // ═══════════════════════════════════════════════════

    private static string SerializeDiagram(EngineDiagram diagram)
    {
        return JsonConvert.SerializeObject(diagram, Formatting.None);
    }

    private static EngineDiagram? DeserializeDiagram(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonConvert.DeserializeObject<EngineDiagram>(json);
        }
        catch
        {
            return null;
        }
    }
}
