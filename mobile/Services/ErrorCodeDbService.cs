using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Централизованная база кодов ошибок OBD-II и J1939.
/// Этап 13: единая справочная таблица error_codes.
/// </summary>
public class ErrorCodeDbService
{
    private SQLiteAsyncConnection? _db;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private static readonly string DbPath = Path.Combine(
        FileSystem.AppDataDirectory, "error_codes.db");

    public ErrorCodeDbService()
    {
        // Конструктор не делает I/O — StrictMode safe.
        // Соединение открывается лениво при первом обращении к GetDbAsync().
    }

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null) return _db;

        await _dbLock.WaitAsync();
        try
        {
            if (_db != null) return _db;

            // Offload открытия SQLite на фоновый поток —
            // конструктор делает синхронное I/O (open файла БД).
            _db = await Task.Run(() =>
                new SQLiteAsyncConnection(DbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache));

            return _db;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task InitializeAsync()
    {
        var db = await GetDbAsync();
        await db.CreateTableAsync<ErrorCodeEntry>();
        await db.CreateTableAsync<DiagnosticProcedure>();
        await db.CreateTableAsync<PendingCodeRequest>();
        await db.CreateTableAsync<SchemeRecord>();
        await db.CreateTableAsync<AdminLogEntry>();
        await db.CreateTableAsync<RepairGuideRecord>();
        await db.CreateTableAsync<CodingOptionRecord>();
        await db.CreateTableAsync<CompetitorRecord>();

        // Индексы
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_ec_code ON error_codes(code)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_ec_brand_model ON error_codes(brand, model)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_ec_source ON error_codes(source)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_schemes_error_code ON schemes(error_code)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_schemes_brand_model ON schemes(brand, model)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_admin_log_date ON admin_log(date)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_repair_guides_error_code ON repair_guides(error_code)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_repair_guides_brand_model ON repair_guides(brand, model)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_coding_options_brand_model ON coding_options(brand, model)");
        await db.ExecuteAsync("CREATE INDEX IF NOT EXISTS idx_competitors_name ON competitors(name)");

        System.Diagnostics.Debug.WriteLine("[ErrorCodeDb] Initialized.");
    }

    // ═══════════════════ CRUD: error_codes ═══════════════════

    public async Task<int> InsertAsync(ErrorCodeEntry entry)
    {
        var db = await GetDbAsync();
        if (entry.DateAdded == default) entry.DateAdded = DateTime.UtcNow;
        return await db.InsertAsync(entry);
    }

    public async Task<int> InsertAllAsync(IEnumerable<ErrorCodeEntry> entries)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(entries);
    }

    public async Task<int> UpdateAsync(ErrorCodeEntry entry)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(entry);
    }

    public async Task<int> DeleteAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<ErrorCodeEntry>(id);
    }

    public async Task<ErrorCodeEntry?> GetByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<ErrorCodeEntry>(id);
    }

    /// <summary>
    /// Поиск кода ошибки (точное совпадение).
    /// </summary>
    public async Task<List<ErrorCodeEntry>> SearchByCodeAsync(string code)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<ErrorCodeEntry>(
            "SELECT * FROM error_codes WHERE code = ?", code);
    }

    /// <summary>
    /// Поиск по коду с учётом марки и модели.
    /// </summary>
    public async Task<List<ErrorCodeEntry>> SearchAsync(string? code = null, string? brand = null, string? model = null, int limit = 50)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM error_codes WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrEmpty(code))
        {
            sql += " AND code LIKE ?";
            args.Add($"%{code}%");
        }

        if (!string.IsNullOrEmpty(brand))
        {
            sql += " AND brand LIKE ?";
            args.Add($"%{brand}%");
        }

        if (!string.IsNullOrEmpty(model))
        {
            sql += " AND model LIKE ?";
            args.Add($"%{model}%");
        }

        sql += " ORDER BY date_added DESC LIMIT ?";
        args.Add(limit);

        return await db.QueryAsync<ErrorCodeEntry>(sql, args.ToArray());
    }

    /// <summary>
    /// Возвращает все уникальные бренды из базы.
    /// </summary>
    public async Task<List<string>> GetBrandsAsync()
    {
        var db = await GetDbAsync();
        var rows = await db.QueryAsync<ErrorCodeEntry>(
            "SELECT DISTINCT brand FROM error_codes WHERE brand IS NOT NULL AND brand != '' ORDER BY brand");
        return rows.Select(r => r.Brand ?? "").Where(b => b.Length > 0).ToList();
    }

    /// <summary>
    /// Количество записей всего и по источникам.
    /// </summary>
    public async Task<(int Total, Dictionary<string, int> BySource)> GetStatsAsync()
    {
        var db = await GetDbAsync();
        var total = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_codes");
        var bySourceRows = await db.QueryAsync<ErrorCodeEntry>(
            "SELECT source, COUNT(*) as cnt FROM error_codes GROUP BY source ORDER BY cnt DESC");
        // Используем временный класс для агрегации
        var bySource = new Dictionary<string, int>();
        foreach (var row in bySourceRows)
        {
            var src = row.Source ?? "unknown";
            bySource[src] = bySource.GetValueOrDefault(src) + 1;
        }
        return (total, bySource);
    }

    // ═══════════════════ CRUD: schemes ═══════════════════

    public async Task<int> InsertSchemeAsync(SchemeRecord scheme)
    {
        var db = await GetDbAsync();
        if (scheme.DateAdded == default) scheme.DateAdded = DateTime.UtcNow;
        return await db.InsertAsync(scheme);
    }

    public async Task<int> InsertSchemesAsync(IEnumerable<SchemeRecord> schemes)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(schemes);
    }

    public async Task<int> UpdateSchemeAsync(SchemeRecord scheme)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(scheme);
    }

    public async Task<int> DeleteSchemeAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<SchemeRecord>(id);
    }

    /// <summary>
    /// Поиск схем по коду ошибки, бренду и модели.
    /// </summary>
    public async Task<List<SchemeRecord>> SearchSchemesAsync(string? errorCode = null, string? brand = null, string? model = null, int limit = 50)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM schemes WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrEmpty(errorCode))
        {
            sql += " AND error_code LIKE ?";
            args.Add($"%{errorCode}%");
        }
        if (!string.IsNullOrEmpty(brand))
        {
            sql += " AND brand LIKE ?";
            args.Add($"%{brand}%");
        }
        if (!string.IsNullOrEmpty(model))
        {
            sql += " AND model LIKE ?";
            args.Add($"%{model}%");
        }

        sql += " ORDER BY date_added DESC LIMIT ?";
        args.Add(limit);
        return await db.QueryAsync<SchemeRecord>(sql, args.ToArray());
    }

    /// <summary>
    /// Возвращает все схемы для указанного кода ошибки.
    /// </summary>
    public async Task<List<SchemeRecord>> GetSchemesForCodeAsync(string code)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<SchemeRecord>(
            "SELECT * FROM schemes WHERE error_code = ? ORDER BY date_added DESC", code);
    }

    // ═══════════════════ CRUD: admin_log ═══════════════════

    public async Task<int> InsertLogAsync(AdminLogEntry entry)
    {
        var db = await GetDbAsync();
        if (entry.Date == default) entry.Date = DateTime.UtcNow;
        return await db.InsertAsync(entry);
    }

    /// <summary>
    /// Быстрый лог действия с авто-заполнением.
    /// </summary>
    public async Task LogAsync(string action, string description, string? section = null, int? targetId = null, string? userName = null)
    {
        await InsertLogAsync(new AdminLogEntry
        {
            Action = action,
            Description = description,
            Section = section,
            TargetId = targetId,
            UserName = userName ?? "admin",
            Date = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Получить последние N записей лога.
    /// </summary>
    public async Task<List<AdminLogEntry>> GetRecentLogsAsync(int limit = 200)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<AdminLogEntry>(
            "SELECT * FROM admin_log ORDER BY date DESC LIMIT ?", limit);
    }

    /// <summary>
    /// Поиск по логу (действие, раздел, текст в описании).
    /// </summary>
    public async Task<List<AdminLogEntry>> SearchLogsAsync(string? action = null, string? section = null, string? searchText = null, int limit = 100)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM admin_log WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrEmpty(action)) { sql += " AND action = ?"; args.Add(action); }
        if (!string.IsNullOrEmpty(section)) { sql += " AND section = ?"; args.Add(section); }
        if (!string.IsNullOrEmpty(searchText)) { sql += " AND description LIKE ?"; args.Add($"%{searchText}%"); }

        sql += " ORDER BY date DESC LIMIT ?";
        args.Add(limit);
        return await db.QueryAsync<AdminLogEntry>(sql, args.ToArray());
    }

    /// <summary>
    /// Очистка логов старше N дней.
    /// </summary>
    public async Task<int> CleanupOldLogsAsync(int daysToKeep = 90)
    {
        var db = await GetDbAsync();
        var cutoff = DateTime.UtcNow.AddDays(-daysToKeep);
        var oldLogs = await db.QueryAsync<AdminLogEntry>(
            "SELECT * FROM admin_log WHERE date < ?", cutoff);
        foreach (var log in oldLogs)
            await db.DeleteAsync(log);
        return oldLogs.Count;
    }

    // ═══════════════════ CRUD: diagnostic_procedures ═══════════════════

    public async Task InsertProcedureAsync(DiagnosticProcedure proc)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(proc);
    }

    public async Task<List<DiagnosticProcedure>> GetProceduresForCodeAsync(string code)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<DiagnosticProcedure>(
            "SELECT * FROM diagnostic_procedures WHERE error_code = ? ORDER BY step_number", code);
    }

    // ═══════════════════ CRUD: repair_guides ═══════════════════

    public async Task<int> InsertGuideAsync(RepairGuideRecord guide)
    {
        var db = await GetDbAsync();
        if (guide.DateAdded == default) guide.DateAdded = DateTime.UtcNow;
        return await db.InsertAsync(guide);
    }

    public async Task<int> InsertGuidesAsync(IEnumerable<RepairGuideRecord> guides)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(guides);
    }

    public async Task<int> UpdateGuideAsync(RepairGuideRecord guide)
    {
        var db = await GetDbAsync();
        guide.UpdateCount++;
        return await db.UpdateAsync(guide);
    }

    public async Task<int> DeleteGuideAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<RepairGuideRecord>(id);
    }

    public async Task<RepairGuideRecord?> GetGuideByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<RepairGuideRecord>(id);
    }

    /// <summary>
    /// Поиск руководств по коду ошибки, бренду, модели.
    /// </summary>
    public async Task<List<RepairGuideRecord>> SearchGuidesAsync(string? errorCode = null, string? brand = null, string? model = null, int limit = 50)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM repair_guides WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrEmpty(errorCode)) { sql += " AND error_code LIKE ?"; args.Add($"%{errorCode}%"); }
        if (!string.IsNullOrEmpty(brand)) { sql += " AND brand LIKE ?"; args.Add($"%{brand}%"); }
        if (!string.IsNullOrEmpty(model)) { sql += " AND model LIKE ?"; args.Add($"%{model}%"); }

        sql += " ORDER BY date_added DESC LIMIT ?";
        args.Add(limit);
        return await db.QueryAsync<RepairGuideRecord>(sql, args.ToArray());
    }

    /// <summary>
    /// Все руководства для указанного кода ошибки.
    /// </summary>
    public async Task<List<RepairGuideRecord>> GetGuidesForCodeAsync(string code)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<RepairGuideRecord>(
            "SELECT * FROM repair_guides WHERE error_code = ? ORDER BY rating DESC, date_added DESC", code);
    }

    // ═══════════════════ CRUD: coding_options ═══════════════════

    public async Task<int> InsertCodingOptionAsync(CodingOptionRecord option)
    {
        var db = await GetDbAsync();
        if (option.DateAdded == default) option.DateAdded = DateTime.UtcNow;
        return await db.InsertAsync(option);
    }

    public async Task<int> InsertCodingOptionsAsync(IEnumerable<CodingOptionRecord> options)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(options);
    }

    public async Task<int> UpdateCodingOptionAsync(CodingOptionRecord option)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(option);
    }

    public async Task<int> DeleteCodingOptionAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<CodingOptionRecord>(id);
    }

    /// <summary>
    /// Поиск опций кодирования по бренду, модели, категории.
    /// </summary>
    public async Task<List<CodingOptionRecord>> SearchCodingOptionsAsync(string? brand = null, string? model = null, string? category = null, int limit = 100)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM coding_options WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrEmpty(brand)) { sql += " AND brand LIKE ?"; args.Add($"%{brand}%"); }
        if (!string.IsNullOrEmpty(model)) { sql += " AND model LIKE ?"; args.Add($"%{model}%"); }
        if (!string.IsNullOrEmpty(category)) { sql += " AND category = ?"; args.Add(category); }

        sql += " ORDER BY brand, option_name LIMIT ?";
        args.Add(limit);
        return await db.QueryAsync<CodingOptionRecord>(sql, args.ToArray());
    }

    /// <summary>
    /// Все опции для бренда/модели.
    /// </summary>
    public async Task<List<CodingOptionRecord>> GetCodingOptionsForVehicleAsync(string brand, string? model = null)
    {
        var db = await GetDbAsync();
        if (!string.IsNullOrEmpty(model))
            return await db.QueryAsync<CodingOptionRecord>(
                "SELECT * FROM coding_options WHERE brand = ? AND model = ? ORDER BY category, option_name", brand, model);
        return await db.QueryAsync<CodingOptionRecord>(
            "SELECT * FROM coding_options WHERE brand = ? ORDER BY model, category, option_name", brand);
    }

    /// <summary>
    /// Переключить активность опции и вернуть новое состояние.
    /// </summary>
    public async Task<bool> ToggleCodingOptionAsync(int id)
    {
        var db = await GetDbAsync();
        var option = await db.FindAsync<CodingOptionRecord>(id);
        if (option == null) return false;
        option.IsActive = !option.IsActive;
        await db.UpdateAsync(option);
        return option.IsActive;
    }

    // ═══════════════════ CRUD: competitors ═══════════════════

    public async Task<int> InsertCompetitorAsync(CompetitorRecord competitor)
    {
        var db = await GetDbAsync();
        if (competitor.LastChecked == default) competitor.LastChecked = DateTime.UtcNow;
        return await db.InsertAsync(competitor);
    }

    public async Task<int> UpdateCompetitorAsync(CompetitorRecord competitor)
    {
        var db = await GetDbAsync();
        competitor.LastChecked = DateTime.UtcNow;
        return await db.UpdateAsync(competitor);
    }

    public async Task<int> DeleteCompetitorAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<CompetitorRecord>(id);
    }

    public async Task<CompetitorRecord?> GetCompetitorByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<CompetitorRecord>(id);
    }

    /// <summary>
    /// Поиск конкурентов по названию или платформе.
    /// </summary>
    public async Task<List<CompetitorRecord>> SearchCompetitorsAsync(string? searchText = null, string? platform = null, int limit = 50)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM competitors WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrEmpty(searchText)) { sql += " AND name LIKE ?"; args.Add($"%{searchText}%"); }
        if (!string.IsNullOrEmpty(platform)) { sql += " AND platform = ?"; args.Add(platform); }

        sql += " ORDER BY rating DESC, last_checked DESC LIMIT ?";
        args.Add(limit);
        return await db.QueryAsync<CompetitorRecord>(sql, args.ToArray());
    }

    /// <summary>
    /// Все конкуренты, отсортированные по рейтингу.
    /// </summary>
    public async Task<List<CompetitorRecord>> GetAllCompetitorsAsync()
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<CompetitorRecord>(
            "SELECT * FROM competitors ORDER BY rating DESC, name");
    }

    /// <summary>
    /// Конкуренты, давно не проверявшиеся (старше N дней).
    /// </summary>
    public async Task<List<CompetitorRecord>> GetStaleCompetitorsAsync(int daysSinceCheck = 14)
    {
        var db = await GetDbAsync();
        var cutoff = DateTime.UtcNow.AddDays(-daysSinceCheck);
        return await db.QueryAsync<CompetitorRecord>(
            "SELECT * FROM competitors WHERE last_checked < ? ORDER BY last_checked", cutoff);
    }

    // ═══════════════════ Наполнение seed-данными ═══════════════════

    public async Task SeedFromExistingDataAsync()
    {
        var db = await GetDbAsync();
        var existing = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM error_codes");
        if (existing > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[ErrorCodeDb] Already seeded ({existing} records).");
            return;
        }

        var entries = new List<ErrorCodeEntry>();
        var now = DateTime.UtcNow;

        // ── Универсальные OBD-II коды (топ-50) ──
        var universalCodes = new (string code, string desc, string solution, string source)[]
        {
            ("P0100", "Датчик массового расхода воздуха (MAF) — неисправность цепи", "Проверить разъём и проводку MAF. Очистить чувствительный элемент. При необходимости заменить датчик.", "OBD-II стандарт"),
            ("P0101", "Датчик MAF — выход за пределы диапазона", "Очистить MAF-сенсор. Проверить подсос воздуха после датчика. Проверить воздушный фильтр.", "OBD-II стандарт"),
            ("P0102", "Датчик MAF — низкий сигнал", "Проверить питание 12V на датчике. Проверить массу. Проверить целостность проводки.", "OBD-II стандарт"),
            ("P0103", "Датчик MAF — высокий сигнал", "Проверить на отсутствие подсоса воздуха. Проверить возвратную цепь на обрыв.", "OBD-II стандарт"),
            ("P0110", "Датчик температуры впускного воздуха (IAT) — неисправность", "Проверить разъём IAT. Проверить сопротивление датчика (должно меняться с температурой).", "OBD-II стандарт"),
            ("P0113", "Датчик IAT — высокий сигнал (низкая температура)", "Проверить цепь на обрыв. Заменить датчик.", "OBD-II стандарт"),
            ("P0115", "Датчик температуры ОЖ (ECT) — неисправность", "Проверить разъём ECT. Проверить сопротивление: ~2.5kΩ при 20°C, ~300Ω при 80°C.", "OBD-II стандарт"),
            ("P0118", "Датчик ECT — высокий сигнал (перегрев)", "Проверить термостат, уровень ОЖ. Проверить датчик.", "OBD-II стандарт"),
            ("P0120", "Датчик положения дроссельной заслонки (TPS) — неисправность", "Калибровка заслонки. Проверить разъём TPS. Проверить сигнал 0.5-4.5V.", "OBD-II стандарт"),
            ("P0121", "Датчик TPS — несоответствие сигналов", "Заменить датчик положения педали или заслонки.", "OBD-II стандарт"),
            ("P0130", "Датчик кислорода (O2 B1S1) — неисправность цепи", "Проверить питание подогрева (12V). Проверить проводку сигнала. Заменить лямбда-зонд.", "OBD-II стандарт"),
            ("P0133", "Датчик O2 B1S1 — медленный отклик", "Заменить лямбда-зонд. Проверить на подсос воздуха.", "OBD-II стандарт"),
            ("P0135", "Датчик O2 B1S1 — неисправность подогрева", "Проверить сопротивление нагревателя (2-15Ω). Проверить предохранитель подогрева.", "OBD-II стандарт"),
            ("P0141", "Датчик O2 B1S2 — неисправность подогрева", "Проверить цепь нагревателя на обрыв/КЗ. Проверить предохранитель.", "OBD-II стандарт"),
            ("P0170", "Топливная коррекция (Bank 1) — нарушение", "Проверить MAF, подсос воздуха, давление топлива, форсунки.", "OBD-II стандарт"),
            ("P0171", "Смесь слишком бедная (Bank 1)", "Проверить подсос воздуха после MAF. Проверить давление топлива. Очистить форсунки.", "OBD-II стандарт"),
            ("P0172", "Смесь слишком богатая (Bank 1)", "Проверить MAF. Проверить давление топлива (не завышено ли). Проверить форсунки на подтекание.", "OBD-II стандарт"),
            ("P0300", "Случайные/множественные пропуски зажигания", "Проверить свечи, катушки, ВВ-провода. Проверить компрессию. Проверить топливную систему.", "OBD-II стандарт"),
            ("P0301", "Пропуски зажигания в цилиндре 1", "Проверить свечу, катушку, форсунку цилиндра 1. Компрессия.", "OBD-II стандарт"),
            ("P0302", "Пропуски зажигания в цилиндре 2", "Проверить свечу, катушку, форсунку цилиндра 2. Компрессия.", "OBD-II стандарт"),
            ("P0303", "Пропуски зажигания в цилиндре 3", "Проверить свечу, катушку, форсунку цилиндра 3. Компрессия.", "OBD-II стандарт"),
            ("P0304", "Пропуски зажигания в цилиндре 4", "Проверить свечу, катушку, форсунку цилиндра 4. Компрессия.", "OBD-II стандарт"),
            ("P0325", "Датчик детонации (Knock) — неисправность", "Проверить проводку датчика. Проверить момент затяжки датчика (20 Нм). Заменить датчик.", "OBD-II стандарт"),
            ("P0335", "Датчик положения коленвала (CKP) — неисправность", "Проверить проводку и разъём CKP. Проверить сопротивление (200-1000Ω). Проверить зазор.", "OBD-II стандарт"),
            ("P0340", "Датчик положения распредвала (CMP) — неисправность", "Проверить проводку. Проверить синхронизацию ГРМ. Заменить датчик.", "OBD-II стандарт"),
            ("P0400", "Система EGR — неисправность потока", "Очистить клапан EGR от нагара. Проверить вакуумные трубки. Проверить электроклапан.", "OBD-II стандарт"),
            ("P0401", "Система EGR — недостаточный поток", "Очистить клапан и каналы EGR. Проверить датчик перепада давления.", "OBD-II стандарт"),
            ("P0420", "Катализатор B1 — эффективность ниже порога", "Проверить лямбда-зонды. Проверить на пропуски зажигания. Возможна замена катализатора.", "OBD-II стандарт"),
            ("P0440", "Система EVAP — неисправность", "Проверить крышку бензобака. Проверить клапан продувки адсорбера. Проверить на утечки.", "OBD-II стандарт"),
            ("P0442", "Система EVAP — малая утечка", "Проверить крышку бензобака (затянуть). Проверить шланги адсорбера.", "OBD-II стандарт"),
            ("P0445", "Система EVAP — большая утечка", "Проверить все шланги и клапан адсорбера. Возможен обрыв шланга.", "OBD-II стандарт"),
            ("P0500", "Датчик скорости автомобиля (VSS) — неисправность", "Проверить датчик скорости на КПП. Проверить проводку до приборной панели.", "OBD-II стандарт"),
            ("P0505", "Система управления холостым ходом (IAC) — неисправность", "Очистить клапан IAC. Проверить проводку. Адаптировать ХХ.", "OBD-II стандарт"),
            ("P0601", "Ошибка контрольной суммы ПЗУ ЭБУ", "Перепрошить/заменить ЭБУ. Проверить питание ЭБУ.", "OBD-II стандарт"),
            ("P0606", "Ошибка процессора ЭБУ", "Проверить питание и массу ЭБУ. Возможна замена ЭБУ.", "OBD-II стандарт"),
            ("P0700", "Неисправность системы управления АКПП", "Проверить уровень и состояние масла АКПП. Считать коды АКПП.", "OBD-II стандарт"),
            ("P0740", "Гидротрансформатор (TCC) — неисправность", "Проверить соленоид блокировки. Проверить давление масла АКПП.", "OBD-II стандарт"),
            ("P1120", "Датчик положения педали акселератора (APP) — неисправность", "Проверить разъём датчика педали. Проверить сигналы APP1/APP2.", "OBD-II стандарт"),
            ("P1135", "Датчик O2 B1S1 — неисправность нагревателя (Toyota)", "Заменить лямбда-зонд (Denso). Проверить предохранитель EFI.", "OBD-II стандарт"),
            ("P1602", "Пропадание питания ЭБУ (КЗ в цепи)", "Проверить предохранители. Проверить реле главного питания.", "OBD-II стандарт"),
            ("P2100", "Привод дроссельной заслонки (ETC) — обрыв", "Проверить разъём заслонки. Проверить мотор-редуктор ETC.", "OBD-II стандарт"),
            ("P2101", "Привод заслонки (ETC) — ошибка диапазона", "Калибровка заслонки. Проверить на заедание.", "OBD-II стандарт"),
            ("P2120", "Датчик положения педали (APP) / заслонки — недостоверность", "Проверить оба датчика APP. Проверить референсное напряжение 5V.", "OBD-II стандарт"),
            ("P2135", "Корреляция TPS/APP — несоответствие", "Заменить дроссельную заслонку в сборе или блок педали.", "OBD-II стандарт"),
            ("P2187", "Смесь бедная на холостом ходу (Bank 1)", "Проверить подсос воздуха, клапан PCV. Проверить MAF.", "OBD-II стандарт"),
            ("P2188", "Смесь богатая на холостом ходу (Bank 1)", "Проверить форсунки. Проверить датчик MAP/MAF. Проверить регулятор давления топлива.", "OBD-II стандарт"),
            ("P2195", "Датчик O2 B1S1 — постоянно бедная смесь", "Подсос воздуха, низкое давление топлива, неисправный MAF.", "OBD-II стандарт"),
            ("P2279", "Подсос воздуха во впускной коллектор", "Проверить прокладку коллектора. Проверить вакуумные шланги. Дым-тест.", "OBD-II стандарт"),
            ("P2509", "Питание ЭБУ — прерывистое (ECM/PCM)", "Проверить клеммы АКБ. Проверить массу двигателя. Проверить реле.", "OBD-II стандарт"),
            ("U0001", "Высокоскоростная CAN-шина — неисправность", "Проверить сопротивление шины (60Ω). Проверить целостность проводов CAN-H/CAN-L.", "OBD-II стандарт"),
        };

        foreach (var (code, desc, solution, source) in universalCodes)
        {
            entries.Add(new ErrorCodeEntry
            {
                Code = code,
                Brand = "",
                Model = "",
                Description = desc,
                Solution = solution,
                Source = source,
                DateAdded = now,
            });
        }

        await db.InsertAllAsync(entries);
        System.Diagnostics.Debug.WriteLine($"[ErrorCodeDb] Seeded {entries.Count} universal error codes.");
    }
}

/// <summary>
/// Запись в таблице error_codes — центральный справочник кодов ошибок.
/// </summary>
[Table("error_codes")]
public class ErrorCodeEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки (P0300, SPN110FMI0, etc).</summary>
    [Column("code"), Indexed]
    public string? Code { get; set; }

    /// <summary>Марка автомобиля (пусто = универсальный код).</summary>
    [Column("brand")]
    public string? Brand { get; set; }

    /// <summary>Модель (опционально).</summary>
    [Column("model")]
    public string? Model { get; set; }

    /// <summary>Двигатель/ЭБУ (опционально).</summary>
    public string? Engine { get; set; }

    /// <summary>Год выпуска (опционально).</summary>
    public string? Year { get; set; }

    /// <summary>Описание ошибки.</summary>
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>Решение / рекомендации по ремонту.</summary>
    [Column("solution")]
    public string? Solution { get; set; }

    /// <summary>Степень критичности: critical, high, medium, low.</summary>
    public string? Severity { get; set; }

    /// <summary>Источник данных (OBD-II стандарт, drive2.ru, kodobd.ru, manual).</summary>
    [Column("source"), Indexed]
    public string? Source { get; set; }

    /// <summary>Дата добавления в базу.</summary>
    [Column("date_added")]
    public DateTime DateAdded { get; set; }

    /// <summary>Количество успешных использований.</summary>
    public int UseCount { get; set; }

    /// <summary>Рейтинг полезности (0-100).</summary>
    public int Rating { get; set; }

    public override string ToString()
        => $"[{Code}] {Description}";
}

/// <summary>
/// Пошаговая диагностическая процедура для кода ошибки.
/// </summary>
[Table("diagnostic_procedures")]
public class DiagnosticProcedure
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Привязка к коду ошибки.</summary>
    [Column("error_code"), Indexed]
    public string? ErrorCode { get; set; }

    /// <summary>Номер шага (1, 2, 3...).</summary>
    [Column("step_number")]
    public int StepNumber { get; set; }

    /// <summary>Тип шага: check, measure, replace, verify.</summary>
    public string? StepType { get; set; }

    /// <summary>Описание действия.</summary>
    public string? Description { get; set; }

    /// <summary>Ожидаемое значение (для check/measure).</summary>
    public string? ExpectedValue { get; set; }

    /// <summary>Что делать, если условие не выполняется.</summary>
    public string? IfNotOk { get; set; }

    /// <summary>Инструменты для шага.</summary>
    public string? Tools { get; set; }

    /// <summary>Изображение/схема (путь к файлу).</summary>
    public string? ImagePath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Отложенный запрос кода ошибки (для фонового поиска в интернете).
/// </summary>
[Table("pending_code_requests")]
public class PendingCodeRequest
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string? Code { get; set; }

    public string? Brand { get; set; }
    public string? Model { get; set; }

    /// <summary>Статус: pending, found, not_found, abandoned.</summary>
    public string? Status { get; set; } = "pending";

    public int RetryCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRetryAt { get; set; }
}

/// <summary>
/// Схема узла/двигателя для кода ошибки (таблица schemes).
/// </summary>
[Table("schemes")]
public class SchemeRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки, к которому привязана схема.</summary>
    [Column("error_code"), Indexed]
    public string? ErrorCode { get; set; }

    /// <summary>Марка автомобиля.</summary>
    public string? Brand { get; set; }

    /// <summary>Модель.</summary>
    public string? Model { get; set; }

    /// <summary>Изображение схемы (BLOB).</summary>
    [Column("image_data")]
    public byte[]? ImageData { get; set; }

    /// <summary>URL источника изображения.</summary>
    [Column("image_url")]
    public string? ImageUrl { get; set; }

    /// <summary>Дата добавления.</summary>
    [Column("date_added")]
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Описание схемы (подпись).</summary>
    public string? Description { get; set; }

    /// <summary>Источник (drive2.ru, auto.ru, diagnost.ru, etc).</summary>
    public string? Source { get; set; }

    /// <summary>Рейтинг полезности (0-100).</summary>
    public int Rating { get; set; }
}

/// <summary>
/// Журнал действий администратора (таблица admin_log).
/// </summary>
[Table("admin_log")]
public class AdminLogEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Действие: insert, update, delete, export, seed, backup, restore.</summary>
    [Column("action")]
    public string? Action { get; set; }

    /// <summary>Описание действия.</summary>
    [Column("description")]
    public string? Description { get; set; }

    /// <summary>Дата и время действия.</summary>
    [Column("date")]
    public DateTime Date { get; set; } = DateTime.UtcNow;

    /// <summary>Раздел/таблица, где выполнялось действие.</summary>
    public string? Section { get; set; }

    /// <summary>ID записи, над которой выполнено действие.</summary>
    public int? TargetId { get; set; }

    /// <summary>Кто выполнил (пользователь/агент).</summary>
    public string? UserName { get; set; }
}

/// <summary>
/// Пошаговое руководство по ремонту (таблица repair_guides).
/// </summary>
[Table("repair_guides")]
public class RepairGuideRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки.</summary>
    [Column("error_code"), Indexed]
    public string? ErrorCode { get; set; }

    /// <summary>Марка автомобиля.</summary>
    public string? Brand { get; set; }

    /// <summary>Модель.</summary>
    public string? Model { get; set; }

    /// <summary>Двигатель.</summary>
    public string? Engine { get; set; }

    /// <summary>Заголовок руководства.</summary>
    public string? Title { get; set; }

    /// <summary>Шаги в JSON-формате.</summary>
    [Column("steps")]
    public string? Steps { get; set; }

    /// <summary>Инструменты (JSON-список).</summary>
    [Column("tools")]
    public string? Tools { get; set; }

    /// <summary>Моменты затяжки (JSON: узел → Н·м).</summary>
    [Column("torque_values")]
    public string? TorqueValues { get; set; }

    /// <summary>Сложность: easy, medium, hard, expert.</summary>
    public string? Difficulty { get; set; }

    /// <summary>Оценочное время ремонта (минут).</summary>
    public int EstimatedTime { get; set; }

    /// <summary>Источник данных.</summary>
    public string? Source { get; set; }

    /// <summary>Дата добавления.</summary>
    [Column("date_added")]
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Количество обновлений.</summary>
    public int UpdateCount { get; set; }

    /// <summary>Рейтинг полезности (0-100).</summary>
    public int Rating { get; set; }
}

/// <summary>
/// Опция кодирования/скрытых функций (таблица coding_options).
/// </summary>
[Table("coding_options")]
public class CodingOptionRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка автомобиля.</summary>
    public string? Brand { get; set; }

    /// <summary>Модель.</summary>
    public string? Model { get; set; }

    /// <summary>Название опции (старт-стоп, автозакрытие окон...).</summary>
    [Column("option_name")]
    public string? OptionName { get; set; }

    /// <summary>Описание опции.</summary>
    [Column("option_description")]
    public string? OptionDescription { get; set; }

    /// <summary>Активна ли сейчас.</summary>
    [Column("is_active")]
    public bool IsActive { get; set; }

    /// <summary>Дата добавления.</summary>
    [Column("date_added")]
    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    /// <summary>Категория (комфорт, безопасность, освещение...).</summary>
    public string? Category { get; set; }

    /// <summary>Позиция байта в EEPROM/Flash.</summary>
    public int? BytePosition { get; set; }

    /// <summary>Битовая маска.</summary>
    public int? BitMask { get; set; }

    /// <summary>Уровень Security Access (1-5).</summary>
    public int SecurityLevel { get; set; } = 1;

    /// <summary>Кодированное значение байта (hex).</summary>
    public string? EncodedByte { get; set; }
}

/// <summary>
/// Информация о конкуренте (таблица competitors).
/// </summary>
[Table("competitors")]
public class CompetitorRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Название продукта/сервиса.</summary>
    public string? Name { get; set; }

    /// <summary>Сайт.</summary>
    public string? Website { get; set; }

    /// <summary>Функции (JSON-список или описание).</summary>
    public string? Features { get; set; }

    /// <summary>Цена / ценовая модель.</summary>
    [Column("price")]
    public string? Price { get; set; }

    /// <summary>Дата последней проверки.</summary>
    [Column("last_checked")]
    public DateTime LastChecked { get; set; } = DateTime.UtcNow;

    /// <summary>Платформа: Windows, Android, iOS, Web.</summary>
    public string? Platform { get; set; }

    /// <summary>Рейтинг (1-10).</summary>
    public int Rating { get; set; }

    /// <summary>Заметки/комментарий.</summary>
    public string? Notes { get; set; }
}
