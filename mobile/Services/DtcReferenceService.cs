using System.Text.Json;
using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Офлайн-справочник DTC: 12 000+ кодов OBD-II (MIT, Wal33D/dtc-database)
/// + русская надстройка проекта (причины/решения/симптомы).
/// Данные — в Resources/Raw/dtc/ (dtc_codes.db копируется в AppData при первом обращении).
/// </summary>
public class DtcReferenceService
{
    private SQLiteAsyncConnection? _db;
    private Dictionary<string, RuEntry>? _ru;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly string DbPath =
        Path.Combine(FileSystem.AppDataDirectory, "dtc_codes.db");

    // Модель строки таблицы dtc_definitions (Wal33D/dtc-database)
    private class DtcRow
    {
        public string code { get; set; } = "";
        public string manufacturer { get; set; } = "";
        public string description { get; set; } = "";
        public int is_generic { get; set; }
    }

    // Модель записи русской надстройки (server/data/dtc_ru.json)
    private class RuEntry
    {
        public string? description_ru { get; set; }
        public List<string>? causes { get; set; }
        public List<string>? solutions { get; set; }
        public string? symptoms { get; set; }
        public string? severity { get; set; }
    }

    private async Task EnsureInitAsync()
    {
        if (_db != null && _ru != null) return;

        await _lock.WaitAsync();
        try
        {
            if (_db == null)
            {
                if (!File.Exists(DbPath))
                {
                    await using var src = await FileSystem.OpenAppPackageFileAsync("dtc/dtc_codes.db");
                    await using var dst = File.Create(DbPath);
                    await src.CopyToAsync(dst);
                }
                _db = new SQLiteAsyncConnection(DbPath,
                    SQLiteOpenFlags.ReadOnly | SQLiteOpenFlags.SharedCache);
            }

            if (_ru == null)
            {
                await using var src = await FileSystem.OpenAppPackageFileAsync("dtc/dtc_ru.json");
                _ru = await JsonSerializer.DeserializeAsync<Dictionary<string, RuEntry>>(src)
                       ?? new Dictionary<string, RuEntry>();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Расшифровка кода. Приоритет: русская надстройка → англ. GENERIC.
    /// Возвращает null, если код неизвестен ни в одном источнике.
    /// </summary>
    public async Task<KnowledgeItem?> GetAsync(string code)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code.Length < 4) return null;

        await EnsureInitAsync();

        _ru!.TryGetValue(code, out var ru);

        var rows = await _db!.Table<DtcRow>()
            .Where(r => r.code == code)
            .ToListAsync();
        var generic = rows.FirstOrDefault(r => r.is_generic == 1);

        if (ru == null && rows.Count == 0)
            return null;

        return new KnowledgeItem
        {
            Code = code,
            Category = "Справочник DTC (12 000+ кодов)",
            Description = ru?.description_ru
                          ?? generic?.description
                          ?? rows[0].description,
            Causes = ru?.causes is { Count: > 0 } ? string.Join("; ", ru.causes) : "",
            Symptoms = ru?.symptoms ?? "",
        };
    }
}
