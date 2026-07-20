using CarDiagnosticApp.Models;
using SQLite;
using System.Diagnostics;
using System.Text;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис хранения российских марок/моделей авто.
/// База: rusautos.db, таблица russian_auto_models.
/// </summary>
public class RussianAutoService
{
    private SQLiteAsyncConnection? _db;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RussianAutoService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "rusautos.db");
    }

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null) return _db;
        await _lock.WaitAsync();
        try
        {
            if (_db != null) return _db;
            _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache));
            await _db.CreateTableAsync<RussianAutoModel>();
            await _db.CreateTableAsync<RussianAutoModelUpdate>();
            await _db.CreateTableAsync<RussianAutoEngine>();
            return _db;
        }
        finally { _lock.Release(); }
    }

    public async Task<int> InsertAsync(RussianAutoModel model)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(model);
        return model.Id;
    }

    public async Task<int> InsertAllAsync(IEnumerable<RussianAutoModel> items)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(items);
    }

    public async Task<List<RussianAutoModel>> GetAllAsync(int limit = 200)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>()
            .OrderByDescending(m => m.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModel>> GetByBrandAsync(string brand, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>()
            .Where(m => m.Brand == brand)
            .OrderByDescending(m => m.YearStart)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModel>> GetNewAsync(int limit = 30)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>()
            .Where(m => m.IsNew)
            .OrderByDescending(m => m.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModel>> GetByStatusAsync(string status, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>()
            .Where(m => m.Status == status)
            .OrderByDescending(m => m.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModel>> GetUnprocessedAsync(int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>()
            .Where(m => !m.IsProcessed)
            .OrderByDescending(m => m.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> MarkProcessedAsync(int id)
    {
        var db = await GetDbAsync();
        var m = await db.Table<RussianAutoModel>().Where(x => x.Id == id).FirstOrDefaultAsync();
        if (m == null) return 0;
        m.IsProcessed = true;
        return await db.UpdateAsync(m);
    }

    public async Task<int> UpdateAsync(RussianAutoModel model)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(model);
    }

    public async Task<int> DeleteAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<RussianAutoModel>(id);
    }

    public async Task<int> CountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>().CountAsync();
    }

    public async Task<int> CountNewAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>().Where(m => m.IsNew).CountAsync();
    }

    /// <summary>Дедупликация по Brand + ModelName.</summary>
    public async Task<bool> ExistsByModelAsync(string brand, string modelName)
    {
        var db = await GetDbAsync();
        var count = await db.Table<RussianAutoModel>()
            .Where(m => m.Brand == brand && m.ModelName == modelName)
            .CountAsync();
        return count > 0;
    }

    /// <summary>Список уникальных марок в базе.</summary>
    public async Task<List<string>> GetBrandsAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<RussianAutoModel>().ToListAsync();
        return all.Select(m => m.Brand).Distinct().OrderBy(b => b).ToList();
    }

    /// <summary>Возвращает все модели для известных марок (для поиска обновлений).</summary>
    public async Task<List<RussianAutoModel>> GetKnownModelsAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModel>()
            .Where(m => m.Status == "in_production")
            .OrderBy(m => m.Brand)
            .ThenBy(m => m.ModelName)
            .ToListAsync();
    }

    // ══════════════ Обновления моделей ══════════════

    public async Task<int> InsertUpdateAsync(RussianAutoModelUpdate update)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(update);
        return update.Id;
    }

    public async Task<int> InsertAllUpdatesAsync(IEnumerable<RussianAutoModelUpdate> items)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(items);
    }

    public async Task<List<RussianAutoModelUpdate>> GetAllUpdatesAsync(int limit = 200)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModelUpdate>()
            .OrderByDescending(u => u.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModelUpdate>> GetUpdatesByBrandAsync(string brand, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModelUpdate>()
            .Where(u => u.Brand == brand)
            .OrderByDescending(u => u.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModelUpdate>> GetUpdatesByModelAsync(string brand, string model, int limit = 20)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModelUpdate>()
            .Where(u => u.Brand == brand && u.ModelName == model)
            .OrderByDescending(u => u.Year)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModelUpdate>> GetUnprocessedUpdatesAsync(int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModelUpdate>()
            .Where(u => !u.IsProcessed)
            .OrderByDescending(u => u.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoModelUpdate>> GetDiagnosticsRelevantAsync(int limit = 30)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModelUpdate>()
            .Where(u => u.AffectsDiagnostics)
            .OrderByDescending(u => u.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> MarkUpdateProcessedAsync(int id)
    {
        var db = await GetDbAsync();
        var u = await db.Table<RussianAutoModelUpdate>().Where(x => x.Id == id).FirstOrDefaultAsync();
        if (u == null) return 0;
        u.IsProcessed = true;
        return await db.UpdateAsync(u);
    }

    public async Task<int> UpdateUpdateAsync(RussianAutoModelUpdate update)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(update);
    }

    public async Task<int> DeleteUpdateAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<RussianAutoModelUpdate>(id);
    }

    public async Task<int> CountUpdatesAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoModelUpdate>().CountAsync();
    }

    /// <summary>Дедупликация обновлений: Brand + ModelName + UpdateType + Description (первые 80 символов).</summary>
    public async Task<bool> ExistsUpdateAsync(string brand, string modelName, string updateType, string descPrefix)
    {
        if (string.IsNullOrEmpty(descPrefix) || descPrefix.Length < 10) return false;
        var db = await GetDbAsync();
        var count = await db.Table<RussianAutoModelUpdate>()
            .Where(u => u.Brand == brand && u.ModelName == modelName && u.UpdateType == updateType)
            .ToListAsync();
        return count.Any(u =>
            !string.IsNullOrEmpty(u.Description) &&
            u.Description.Length >= descPrefix.Length &&
            u.Description[..descPrefix.Length].Equals(descPrefix, StringComparison.OrdinalIgnoreCase));
    }

    // ══════════════ Двигатели и системы ══════════════

    public async Task<int> InsertEngineAsync(RussianAutoEngine item)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(item);
        return item.Id;
    }

    public async Task<int> InsertAllEnginesAsync(IEnumerable<RussianAutoEngine> items)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(items);
    }

    public async Task<List<RussianAutoEngine>> GetAllEnginesAsync(int limit = 200)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoEngine>()
            .OrderByDescending(e => e.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoEngine>> GetEnginesByBrandAsync(string brand, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoEngine>()
            .Where(e => e.Brand == brand)
            .OrderByDescending(e => e.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoEngine>> GetNewEnginesAsync(int limit = 30)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoEngine>()
            .Where(e => e.IsNew)
            .OrderByDescending(e => e.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoEngine>> GetByRecordTypeAsync(string recordType, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoEngine>()
            .Where(e => e.RecordType == recordType)
            .OrderByDescending(e => e.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RussianAutoEngine>> GetUnprocessedEnginesAsync(int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoEngine>()
            .Where(e => !e.IsProcessed)
            .OrderByDescending(e => e.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> MarkEngineProcessedAsync(int id)
    {
        var db = await GetDbAsync();
        var e = await db.Table<RussianAutoEngine>().Where(x => x.Id == id).FirstOrDefaultAsync();
        if (e == null) return 0;
        e.IsProcessed = true;
        return await db.UpdateAsync(e);
    }

    public async Task<int> UpdateEngineAsync(RussianAutoEngine item)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(item);
    }

    public async Task<int> DeleteEngineAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<RussianAutoEngine>(id);
    }

    public async Task<int> CountEnginesAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<RussianAutoEngine>().CountAsync();
    }

    /// <summary>Дедупликация по EngineCode или по Brand+EngineName.</summary>
    public async Task<bool> ExistsEngineAsync(string engineCode, string brand, string engineName)
    {
        var db = await GetDbAsync();
        if (!string.IsNullOrEmpty(engineCode) && engineCode.Length >= 3)
        {
            var byCode = await db.Table<RussianAutoEngine>()
                .Where(e => e.EngineCode == engineCode)
                .CountAsync();
            if (byCode > 0) return true;
        }
        if (!string.IsNullOrEmpty(brand) && !string.IsNullOrEmpty(engineName))
        {
            var byName = await db.Table<RussianAutoEngine>()
                .Where(e => e.Brand == brand && e.EngineName == engineName)
                .CountAsync();
            if (byName > 0) return true;
        }
        return false;
    }

    /// <summary>Seed-данные: актуальные двигатели российских авто.</summary>
    public async Task<int> SeedEnginesAsync()
    {
        var db = await GetDbAsync();
        var existing = await db.Table<RussianAutoEngine>().CountAsync();
        if (existing > 0) return 0;

        var defaults = new List<RussianAutoEngine>
        {
            // ── LADA ──
            new() { Brand="LADA", EngineCode="ВАЗ-11189", EngineName="1.6 8V (87 лс)", FuelType="бензин", Displacement=1.6, PowerHP=87, TorqueNM=140, FuelSystem="распределённый впрыск", Turbo="атмосферный", EmissionClass="Евро-5", Transmission="МКПП 5", TransmissionVendor="АвтоВАЗ", Gears=5, ECUType="Bosch ME17.9.7", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD", RecordType="engine", Status="in_production", Factory="АвтоВАЗ" },
            new() { Brand="LADA", EngineCode="ВАЗ-21129", EngineName="1.6 16V (106 лс)", FuelType="бензин", Displacement=1.6, PowerHP=106, TorqueNM=148, FuelSystem="распределённый впрыск", Turbo="атмосферный", EmissionClass="Евро-5", Transmission="МКПП 5 / АКПП 4", TransmissionVendor="АвтоВАЗ / Jatco", Gears=5, ECUType="Bosch ME17.9.7", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD", RecordType="engine", Status="in_production", Factory="АвтоВАЗ" },
            new() { Brand="LADA", EngineCode="ВАЗ-21179", EngineName="1.8 16V (122 лс)", FuelType="бензин", Displacement=1.8, PowerHP=122, TorqueNM=170, FuelSystem="распределённый впрыск", Turbo="атмосферный", EmissionClass="Евро-5", Transmission="МКПП 5 / РКПП 5", TransmissionVendor="АвтоВАЗ", Gears=5, ECUType="Bosch ME17.9.71", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD CAN", RecordType="engine", Status="in_production", Factory="АвтоВАЗ" },
            new() { Brand="LADA", EngineCode="ВАЗ-21214", EngineName="1.7 8V (83 лс) Нива", FuelType="бензин", Displacement=1.7, PowerHP=83, TorqueNM=129, FuelSystem="распределённый впрыск", Turbo="атмосферный", EmissionClass="Евро-5", Transmission="МКПП 5", TransmissionVendor="АвтоВАЗ", Gears=5, ECUType="Bosch M7.9.7", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD", RecordType="engine", Status="in_production", Factory="АвтоВАЗ" },

            // ── УАЗ ──
            new() { Brand="УАЗ", EngineCode="ЗМЗ-40906", EngineName="2.7 16V (149 лс)", FuelType="бензин", Displacement=2.7, PowerHP=149, TorqueNM=235, FuelSystem="распределённый впрыск", Turbo="атмосферный", EmissionClass="Евро-5", Transmission="МКПП 5 / АКПП 6", TransmissionVendor="Dymos / Punch", Gears=5, ECUType="Микас 12.3", ECUVendor="Итэлма", OBDProtocol="OBD2/EOBD", RecordType="engine", Status="in_production", Factory="ЗМЗ" },
            new() { Brand="УАЗ", EngineCode="ЗМЗ-51432", EngineName="2.3 16V Турбодизель (114 лс)", FuelType="дизель", Displacement=2.3, PowerHP=114, TorqueNM=270, FuelSystem="Common Rail", Turbo="турбо", EmissionClass="Евро-4", Transmission="МКПП 5", TransmissionVendor="Dymos", Gears=5, ECUType="Bosch EDC16", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD CAN", RecordType="engine", Status="discontinued", Factory="ЗМЗ", Notes="Снят в 2023" },

            // ── ГАЗ ──
            new() { Brand="ГАЗ", EngineCode="УМЗ-4216", EngineName="2.9 Бензин (107 лс)", FuelType="бензин", Displacement=2.89, PowerHP=107, TorqueNM=220, FuelSystem="распределённый впрыск", Turbo="атмосферный", EmissionClass="Евро-5", Transmission="МКПП 5", TransmissionVendor="ГАЗ", Gears=5, ECUType="Микас 11ET", ECUVendor="Итэлма", OBDProtocol="OBD2/EOBD", RecordType="engine", Status="in_production", Factory="УМЗ" },
            new() { Brand="ГАЗ", EngineCode="Cummins ISF 2.8", EngineName="2.8 Турбодизель (149 лс)", FuelType="дизель", Displacement=2.8, PowerHP=149, TorqueNM=360, FuelSystem="Common Rail", Turbo="турбо", EmissionClass="Евро-5", Transmission="МКПП 5 / 6", TransmissionVendor="ГАЗ", Gears=5, ECUType="Bosch EDC17", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD CAN", RecordType="engine", Status="in_production", Factory="Cummins/ГАЗ" },

            // ── Москвич (JAC) ──
            new() { Brand="Москвич", ModelName="Москвич 3", EngineCode="HFC4GB2.4E", EngineName="1.5 Турбо (150 лс)", FuelType="бензин", Displacement=1.5, PowerHP=150, TorqueNM=210, FuelSystem="прямой впрыск", Turbo="турбо", EmissionClass="China-VI", Transmission="CVT", TransmissionVendor="JAC", OBDProtocol="OBD2/UDS", RecordType="engine", Status="in_production", Factory="JAC/Москвич" },

            // ── Evolute ──
            new() { Brand="Evolute", ModelName="i-Pro", EngineCode="EV-IM", EngineName="Электро (163 лс)", FuelType="электро", PowerHP=163, TorqueNM=250, Transmission="1-ст. редуктор", OBDProtocol="OBD2/UDS", RecordType="electric", Status="in_production", Factory="Моторинвест", Notes="Батарея 53 кВт·ч" },
            new() { Brand="Evolute", ModelName="i-Joy", EngineCode="EV-SUV", EngineName="Электро (177 лс)", FuelType="электро", PowerHP=177, TorqueNM=300, Transmission="1-ст. редуктор", OBDProtocol="OBD2/UDS", RecordType="electric", Status="in_production", Factory="Моторинвест", Notes="Батарея 61 кВт·ч" },

            // ── Электроника/ЭБУ (универсальные) ──
            new() { Brand="LADA", ECUType="Bosch ME17.9.7", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD", RecordType="ecu", Status="in_production", Notes="Основной ЭБУ Vesta/Granta (до 2023)" },
            new() { Brand="LADA", ECUType="Bosch ME17.9.71", ECUVendor="Bosch", OBDProtocol="OBD2/EOBD CAN UDS", RecordType="ecu", Status="in_production", Notes="ЭБУ LADA Vesta NG/Aura" },
            new() { Brand="LADA", ECUType="Микас 12.3", ECUVendor="Итэлма", OBDProtocol="OBD2/EOBD CAN", RecordType="ecu", Status="in_production", Notes="Российский ЭБУ, импортозамещение" },
            new() { Brand="УАЗ", ECUType="Микас 12.3", ECUVendor="Итэлма", OBDProtocol="OBD2/EOBD CAN", RecordType="ecu", Status="in_production", Notes="Устанавливается с 2023" },
            new() { Brand="ГАЗ", ECUType="Микас 11ET", ECUVendor="Итэлма", OBDProtocol="OBD2/EOBD", RecordType="ecu", Status="in_production", Notes="Газель NEXT / УМЗ-4216" },
        };

        var inserted = await db.InsertAllAsync(defaults);
        Debug.WriteLine($"[RussianAutoService] Seeded {inserted} engines/systems.");
        return inserted;
    }

    /// <summary>Seed-данные: актуальный список российских марок и моделей.</summary>
    public async Task<int> SeedDefaultsAsync()
    {
        var db = await GetDbAsync();
        var existing = await db.Table<RussianAutoModel>().CountAsync();
        if (existing > 0) return 0;

        var defaults = new List<RussianAutoModel>
        {
            // ── LADA (АвтоВАЗ) ──
            new() { Brand="LADA", ModelName="Vesta", Generation="I", YearStart=2015, BodyType="седан, универсал", EngineTypes="бензин 1.6, 1.8", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="АвтоВАЗ", Notes="Флагманская модель" },
            new() { Brand="LADA", ModelName="Vesta NG", Generation="II (New Generation)", YearStart=2022, BodyType="седан, универсал, Cross", EngineTypes="бензин 1.6, 1.8", OBDProtocol="OBD2/EOBD CAN", Status="in_production", Factory="АвтоВАЗ", Notes="Рестайлинг Vesta" },
            new() { Brand="LADA", ModelName="Granta", Generation="I", YearStart=2011, BodyType="седан, лифтбек, универсал, хэтчбек", EngineTypes="бензин 1.6", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="АвтоВАЗ" },
            new() { Brand="LADA", ModelName="Granta FL", Generation="рестайлинг", YearStart=2018, BodyType="седан, лифтбек, универсал, хэтчбек", EngineTypes="бензин 1.6", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="АвтоВАЗ", Notes="Обновлённая Granta" },
            new() { Brand="LADA", ModelName="Niva Legend", Generation="I", YearStart=1977, BodyType="внедорожник", EngineTypes="бензин 1.7", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="АвтоВАЗ", Notes="Классическая Нива" },
            new() { Brand="LADA", ModelName="Niva Travel", Generation="I", YearStart=2020, BodyType="внедорожник", EngineTypes="бензин 1.7", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="АвтоВАЗ", Notes="Бывшая Chevrolet Niva" },
            new() { Brand="LADA", ModelName="Largus", Generation="I", YearStart=2012, BodyType="универсал, фургон", EngineTypes="бензин 1.6", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="АвтоВАЗ", Notes="На базе Dacia Logan MCV" },
            new() { Brand="LADA", ModelName="Aura", Generation="I", YearStart=2024, BodyType="седан", EngineTypes="бензин 1.8", OBDProtocol="OBD2/EOBD CAN", Status="in_production", Factory="АвтоВАЗ", Notes="Удлинённая Vesta, бизнес-класс", IsNew=true },

            // ── УАЗ ──
            new() { Brand="УАЗ", ModelName="Patriot", Generation="I", YearStart=2005, BodyType="внедорожник", EngineTypes="бензин 2.7, дизель 2.3", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="УАЗ" },
            new() { Brand="УАЗ", ModelName="Patriot FL", Generation="рестайлинг", YearStart=2016, BodyType="внедорожник", EngineTypes="бензин 2.7, дизель 2.3", OBDProtocol="OBD2/EOBD CAN", Status="in_production", Factory="УАЗ" },
            new() { Brand="УАЗ", ModelName="Pickup", Generation="I", YearStart=2008, BodyType="пикап", EngineTypes="бензин 2.7, дизель 2.3", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="УАЗ" },
            new() { Brand="УАЗ", ModelName="Hunter", Generation="I", YearStart=2003, BodyType="внедорожник", EngineTypes="бензин 2.7, дизель 2.3", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="УАЗ" },
            new() { Brand="УАЗ", ModelName="СГР (Буханка)", Generation="I", YearStart=1965, BodyType="фургон, микроавтобус", EngineTypes="бензин 2.7", OBDProtocol="OBD1/OBD2", Status="in_production", Factory="УАЗ", Notes="Санитарный/грузовой" },
            new() { Brand="УАЗ", ModelName="Профи", Generation="I", YearStart=2017, BodyType="фургон, бортовой", EngineTypes="бензин 2.7, дизель 2.3", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="УАЗ" },

            // ── ГАЗ ──
            new() { Brand="ГАЗ", ModelName="Газель NN", Generation="новое поколение", YearStart=2021, BodyType="фургон, микроавтобус, бортовой", EngineTypes="бензин 2.7, дизель 2.8", OBDProtocol="OBD2/EOBD CAN", Status="in_production", Factory="ГАЗ" },
            new() { Brand="ГАЗ", ModelName="Газель NEXT", Generation="I", YearStart=2013, BodyType="фургон, микроавтобус, бортовой", EngineTypes="дизель 2.8, бензин 2.7", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="ГАЗ" },
            new() { Brand="ГАЗ", ModelName="Соболь NN", Generation="новое поколение", YearStart=2023, BodyType="фургон, микроавтобус", EngineTypes="бензин 2.7, дизель 2.8", OBDProtocol="OBD2/EOBD CAN", Status="in_production", Factory="ГАЗ", IsNew=true },
            new() { Brand="ГАЗ", ModelName="Валдай NEXT", Generation="I", YearStart=2020, BodyType="грузовой", EngineTypes="дизель 2.8, 3.8", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="ГАЗ" },

            // ── Москвич ──
            new() { Brand="Москвич", ModelName="Москвич 3", Generation="I", YearStart=2022, BodyType="кроссовер", EngineTypes="бензин 1.5", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="Москвич", Notes="JAC JS4, сборка в Москве", IsNew=true },
            new() { Brand="Москвич", ModelName="Москвич 6", Generation="I", YearStart=2023, BodyType="лифтбек", EngineTypes="бензин 1.5", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="Москвич", Notes="JAC Sehol A5 Plus", IsNew=true },
            new() { Brand="Москвич", ModelName="Москвич 8", Generation="I", YearStart=2024, BodyType="кроссовер", EngineTypes="бензин 1.5, гибрид", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="Москвич", IsNew=true },

            // ── Evolute (электромобили) ──
            new() { Brand="Evolute", ModelName="i-Pro", Generation="I", YearStart=2022, BodyType="седан", EngineTypes="электро", OBDProtocol="OBD2/UDS", Status="in_production", Factory="Моторинвест", Notes="Электрический седан", IsNew=true },
            new() { Brand="Evolute", ModelName="i-Joy", Generation="I", YearStart=2023, BodyType="кроссовер", EngineTypes="электро", OBDProtocol="OBD2/UDS", Status="in_production", Factory="Моторинвест", IsNew=true },
            new() { Brand="Evolute", ModelName="i-Sky", Generation="I", YearStart=2024, BodyType="кроссовер", EngineTypes="электро", OBDProtocol="OBD2/UDS", Status="in_production", Factory="Моторинвест", IsNew=true },

            // ── Xcite (Автозавод Санкт-Петербург) ──
            new() { Brand="Xcite", ModelName="X-Cross 7", Generation="I", YearStart=2024, BodyType="кроссовер", EngineTypes="бензин 1.5", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="Автозавод СПб", Notes="Chery Tiggo 7 Pro, сборка в РФ", IsNew=true },
            new() { Brand="Xcite", ModelName="X-Cross 8", Generation="I", YearStart=2024, BodyType="кроссовер", EngineTypes="бензин 2.0", OBDProtocol="OBD2/EOBD", Status="in_production", Factory="Автозавод СПб", IsNew=true },

            // ── AmberAuto (Автотор) ──
            new() { Brand="AmberAuto", ModelName="A5", Generation="I", YearStart=2024, BodyType="седан", EngineTypes="электро", OBDProtocol="OBD2/UDS", Status="in_production", Factory="Автотор", Notes="Электроседан на базе JMEV", IsNew=true },
        };

        var inserted = await db.InsertAllAsync(defaults);
        System.Diagnostics.Debug.WriteLine($"[RussianAutoService] Seeded {inserted} default models.");
        return inserted;
    }

    /// <summary>Генерация текстового отчёта.</summary>
    public async Task<string> GenerateReportAsync()
    {
        var all = await GetAllAsync(300);
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine("  РОССИЙСКИЕ МАРКИ И МОДЕЛИ АВТО");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        var brands = all.Select(m => m.Brand).Distinct().OrderBy(b => b).ToList();
        sb.AppendLine($"── Марок: {brands.Count} ──");
        sb.AppendLine($"  {string.Join(", ", brands)}");
        sb.AppendLine();

        sb.AppendLine($"── Всего моделей: {all.Count} (новинок: {all.Count(m => m.IsNew)}) ──");
        sb.AppendLine();

        // По маркам
        foreach (var brand in brands)
        {
            var models = all.Where(m => m.Brand == brand).ToList();
            sb.AppendLine($"  🔹 {brand} ({models.Count} моделей):");
            foreach (var m in models.OrderByDescending(m => m.YearStart))
            {
                var newTag = m.IsNew ? " 🆕" : "";
                sb.AppendLine($"     • {m.ModelName} {m.Generation} ({m.YearStart}{(m.YearEnd.HasValue ? $"-{m.YearEnd}" : "+")}) [{m.BodyType}]{newTag}");
            }
            sb.AppendLine();
        }

        // Актуальные новинки
        var newOnes = all.Where(m => m.IsNew).OrderByDescending(m => m.YearStart).ToList();
        if (newOnes.Count > 0)
        {
            sb.AppendLine("── 🆕 НОВИНКИ ──");
            foreach (var m in newOnes)
                sb.AppendLine($"   {m.Brand} {m.ModelName} ({m.YearStart}) — {m.BodyType}, {m.EngineTypes}");
            sb.AppendLine();
        }

        // Статистика по протоколам
        sb.AppendLine("── Диагностические протоколы ──");
        var protocols = all.Where(m => !string.IsNullOrEmpty(m.OBDProtocol))
            .GroupBy(m => m.OBDProtocol)
            .OrderByDescending(g => g.Count());
        foreach (var g in protocols)
            sb.AppendLine($"   {g.Key}: {g.Count()} моделей");

        // Обновления моделей
        var updates = await GetAllUpdatesAsync(100);
        if (updates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("── 🔄 ОБНОВЛЕНИЯ МОДЕЛЕЙ ──");
            sb.AppendLine($"   Всего обновлений: {updates.Count}");
            sb.AppendLine($"   Влияет на диагностику: {updates.Count(u => u.AffectsDiagnostics)}");

            var byType = updates.GroupBy(u => u.UpdateType).OrderByDescending(g => g.Count());
            foreach (var g in byType)
            {
                var label = g.Key switch
                {
                    "restyling" => "Рестайлинг",
                    "new_generation" => "Новое поколение",
                    "new_engine" => "Новый двигатель",
                    "new_trim" => "Комплектация",
                    "tech_update" => "Тех. обновление",
                    "discontinued" => "Снято с производства",
                    "safety_update" => "Безопасность",
                    "special_edition" => "Спецверсия",
                    _ => g.Key
                };
                sb.AppendLine($"   {label}: {g.Count()}");
            }

            var diagRelevant = updates.Where(u => u.AffectsDiagnostics).Take(10);
            foreach (var u in diagRelevant)
            {
                var icon = u.UpdateType switch
                {
                    "new_generation" => "🔄",
                    "tech_update" => "🔧",
                    "new_engine" => "⚙️",
                    _ => "📌"
                };
                sb.AppendLine($"   {icon} {u.Brand} {u.ModelName}: {u.Description[..Math.Min(u.Description.Length, 100)]}");
            }
        }

        return sb.ToString();
    }
}
