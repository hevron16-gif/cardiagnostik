using CarDiagnosticApp.Models;
using SQLite;
using System.Diagnostics;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис каталога спецтехники: тракторы, комбайны, грузовики, автобусы.
/// База: special_vehicles.db.
/// </summary>
public class SpecialVehicleService
{
    private SQLiteAsyncConnection? _db;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _seeded;

    public SpecialVehicleService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "special_vehicles.db");
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

            await _db.CreateTableAsync<SpecialVehicle>();
            await _db.CreateTableAsync<SpecialErrorCode>();
            await _db.CreateTableAsync<SpecialVehicleECU>();

            if (!_seeded)
            {
                _seeded = true;
                await SeedAsync();
            }

            return _db;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ═══════════════════ CRUD: Транспорт ═══════════════════

    public async Task<List<SpecialVehicle>> GetVehiclesAsync(string? vehicleType = null, string? brand = null)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM special_vehicles WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrWhiteSpace(vehicleType)) { sql += " AND VehicleType = ?"; args.Add(vehicleType); }
        if (!string.IsNullOrWhiteSpace(brand)) { sql += " AND Brand = ?"; args.Add(brand); }
        sql += " ORDER BY Brand, Model";

        return await db.QueryAsync<SpecialVehicle>(sql, args.ToArray());
    }

    public async Task<List<string>> GetBrandsAsync(string? vehicleType = null)
    {
        var vehicles = await GetVehiclesAsync(vehicleType);
        return vehicles.Select(v => v.Brand).Distinct().OrderBy(b => b).ToList();
    }

    public async Task<List<string>> GetVehicleTypesAsync()
    {
        var db = await GetDbAsync();
        var rows = await db.QueryAsync<SpecialVehicle>("SELECT DISTINCT VehicleType FROM special_vehicles");
        return rows.Select(r => r.VehicleType).Distinct().OrderBy(t => t).ToList();
    }

    public async Task<SpecialVehicle?> GetVehicleByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<SpecialVehicle>(id);
    }

    public async Task<int> GetVehicleCountAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<SpecialVehicle>().ToListAsync();
        return all.Count;
    }

    // ═══════════════════ CRUD: Коды ошибок ═══════════════════

    public async Task<List<SpecialErrorCode>> GetErrorCodesAsync(int vehicleId)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<SpecialErrorCode>(
            "SELECT * FROM special_error_codes WHERE VehicleId = ? ORDER BY SPN, FMI", vehicleId);
    }

    public async Task<List<SpecialErrorCode>> SearchErrorCodesAsync(string query)
    {
        var db = await GetDbAsync();
        var q = $"%{query}%";
        return await db.QueryAsync<SpecialErrorCode>(
            "SELECT * FROM special_error_codes WHERE Code LIKE ? OR Description LIKE ? OR Causes LIKE ? LIMIT 50",
            q, q, q);
    }

    public async Task<SpecialErrorCode?> GetErrorBySpnFmiAsync(int vehicleId, int spn, int fmi)
    {
        var db = await GetDbAsync();
        var rows = await db.QueryAsync<SpecialErrorCode>(
            "SELECT * FROM special_error_codes WHERE VehicleId = ? AND SPN = ? AND FMI = ? LIMIT 1",
            vehicleId, spn, fmi);
        return rows.FirstOrDefault();
    }

    public async Task<int> GetErrorCodeCountAsync(int? vehicleId = null)
    {
        var db = await GetDbAsync();
        if (vehicleId.HasValue)
            return (await db.QueryAsync<SpecialErrorCode>(
                "SELECT * FROM special_error_codes WHERE VehicleId = ?", vehicleId.Value)).Count;
        return (await db.Table<SpecialErrorCode>().ToListAsync()).Count;
    }

    // ═══════════════════ CRUD: ЭБУ ═══════════════════

    public async Task<List<SpecialVehicleECU>> GetECUsAsync(int vehicleId)
    {
        var db = await GetDbAsync();
        return await db.QueryAsync<SpecialVehicleECU>(
            "SELECT * FROM special_ecus WHERE VehicleId = ? ORDER BY SourceAddress", vehicleId);
    }

    // ═══════════════════ Сводка ═══════════════════

    public async Task<(int Vehicles, int ErrorCodes, int ECUs, List<string> Brands)> GetSummaryAsync()
    {
        var db = await GetDbAsync();
        var vehicles = await GetVehiclesAsync();
        var ecus = await db.Table<SpecialVehicleECU>().ToListAsync();
        var errors = await db.Table<SpecialErrorCode>().ToListAsync();
        var brands = vehicles.Select(v => v.Brand).Distinct().OrderBy(b => b).ToList();

        return (vehicles.Count, errors.Count, ecus.Count, brands);
    }

    // ═══════════════════ Seed-данные ═══════════════════

    private async Task<int> SeedAsync()
    {
        var db = await GetDbAsync();
        var existing = (await db.Table<SpecialVehicle>().ToListAsync()).Count;
        if (existing > 0)
        {
            Debug.WriteLine($"[SpecialVehicle] DB already has {existing} vehicles, skipping seed.");
            return 0;
        }

        var now = DateTime.UtcNow;
        int added = 0;

        // ═══════════════════ КИРОВЕЦ ═══════════════════

        var kirovetsK744 = new SpecialVehicle
        {
            Brand = "Кировец", Model = "К-744Р4", VehicleType = "tractor",
            EngineFamily = "ЯМЗ-658 (ТМЗ-8481)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2018–н.в.", Description = "Современный сельскохозяйственный трактор 420 л.с. с гидравликой и системой точного земледелия.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(kirovetsK744);

        var kirovetsK742 = new SpecialVehicle
        {
            Brand = "Кировец", Model = "К-742М", VehicleType = "tractor",
            EngineFamily = "ЯМЗ-6585", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2020–н.в.", Description = "Трактор 450 л.с., усиленная трансмиссия для тяжёлых работ.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(kirovetsK742);

        var kirovetsK5 = new SpecialVehicle
        {
            Brand = "Кировец", Model = "К-5", VehicleType = "tractor",
            EngineFamily = "ЯМЗ-536", Protocol = "CAN/J1939", BusSpeedKbps = 500,
            Years = "2016–н.в.", Description = "Средний трактор 300 л.с. с электронной системой управления.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(kirovetsK5);

        var kirovetsK7M = new SpecialVehicle
        {
            Brand = "Кировец", Model = "К-7М", VehicleType = "tractor",
            EngineFamily = "ТМЗ-8481.10", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2014–2022", Description = "Тяжёлый трактор 400 л.с., классическая серия К-700.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(kirovetsK7M);

        // ═══════════════════ БЕЛАРУС (МТЗ) ═══════════════════

        var belarus1221 = new SpecialVehicle
        {
            Brand = "Беларус", Model = "МТЗ-1221.2", VehicleType = "tractor",
            EngineFamily = "ММЗ Д-260.2", Protocol = "CAN/J1939", BusSpeedKbps = 250,
            Years = "2010–н.в.", Description = "Универсальный трактор 130 л.с. с электронной панелью и CAN-шиной.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(belarus1221);

        var belarus1523 = new SpecialVehicle
        {
            Brand = "Беларус", Model = "МТЗ-1523", VehicleType = "tractor",
            EngineFamily = "ММЗ Д-260.4S2", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2012–н.в.", Description = "Трактор 155 л.с. с турбонаддувом и электронной системой впрыска.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(belarus1523);

        var belarus82 = new SpecialVehicle
        {
            Brand = "Беларус", Model = "МТЗ-82.1", VehicleType = "tractor",
            EngineFamily = "ММЗ Д-243", Protocol = "K-Line", BusSpeedKbps = 10,
            Years = "2000–н.в.", Description = "Самый массовый трактор 81 л.с. Диагностика через K-Line (ISO 9141).",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(belarus82);

        var belarus1220 = new SpecialVehicle
        {
            Brand = "Беларус", Model = "МТЗ-1220.1", VehicleType = "tractor",
            EngineFamily = "ММЗ Д-260.1", Protocol = "K-Line/CAN", BusSpeedKbps = 10,
            Years = "2008–2016", Description = "Переходная модель 130 л.с. со смешанной диагностикой K-Line/CAN.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(belarus1220);

        // ═══════════════════ ДСТ-Урал ═══════════════════

        var dstDt75 = new SpecialVehicle
        {
            Brand = "ДСТ-Урал", Model = "ДТ-75М", VehicleType = "tractor",
            EngineFamily = "А-41СИ", Protocol = "K-Line", BusSpeedKbps = 10,
            Years = "2005–н.в.", Description = "Гусеничный сельхозтрактор 95 л.с. Диагностика через K-Line адаптер.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(dstDt75);

        var dstFt80 = new SpecialVehicle
        {
            Brand = "ДСТ-Урал", Model = "FT-80", VehicleType = "tractor",
            EngineFamily = "ЯМЗ-236М2", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2018–н.в.", Description = "Современный гусеничный трактор 240 л.с. с J1939-диагностикой.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(dstFt80);

        var dstT10 = new SpecialVehicle
        {
            Brand = "ДСТ-Урал", Model = "Т-10МБ", VehicleType = "tractor",
            EngineFamily = "ЯМЗ-238НД", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2012–н.в.", Description = "Болотоходный гусеничный трактор 180 л.с. для лесной и нефтегазовой отраслей.",
            Icon = "🚜", CreatedAt = now,
        };
        await db.InsertAsync(dstT10);

        // ═══════════════════ ГРЕЙДЕРЫ ═══════════════════

        var dz98 = new SpecialVehicle
        {
            Brand = "ЧТЗ", Model = "ДЗ-98", VehicleType = "grader",
            EngineFamily = "Д-180 / ЯМЗ-238НД", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2008–н.в.", Description = "Тяжёлый автогрейдер 250 л.с. (класс 250). Полная масса 20 т, длина отвала 4,2 м. Применяется в дорожном строительстве и содержании дорог.",
            Icon = "🚧", CreatedAt = now,
        };
        await db.InsertAsync(dz98);

        var dz122 = new SpecialVehicle
        {
            Brand = "Брянский Арсенал", Model = "ДЗ-122", VehicleType = "grader",
            EngineFamily = "ЯМЗ-236НД / Cummins QSB 6.7", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2010–н.в.", Description = "Средний автогрейдер 160–180 л.с. с гидромеханической трансмиссией. Масса 14,5 т, отвал 3,7 м.",
            Icon = "🚧", CreatedAt = now,
        };
        await db.InsertAsync(dz122);

        // ═══════════════════ КОМБАЙНЫ ═══════════════════

        var rsmAcros = new SpecialVehicle
        {
            Brand = "Ростсельмаш", Model = "Acros 595 Plus", VehicleType = "combine",
            EngineFamily = "ЯМЗ-658 (Cummins)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2015–н.в.", Description = "Зерноуборочный комбайн 325 л.с., бункер 9 м³, жатка до 9 м. Система РСМ Агротроник с GPS-мониторингом.",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(rsmAcros);

        var rsmTorum = new SpecialVehicle
        {
            Brand = "Ростсельмаш", Model = "Torum 780", VehicleType = "combine",
            EngineFamily = "ЯМЗ-6585 (506 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2018–н.в.", Description = "Роторный зерноуборочный комбайн 506 л.с., бункер 12 м³, жатка 12 м. Флагманская модель с роторным обмолотом.",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(rsmTorum);

        var rsmVector = new SpecialVehicle
        {
            Brand = "Ростсельмаш", Model = "Vector 410", VehicleType = "combine",
            EngineFamily = "ЯМЗ-536 (260 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2013–н.в.", Description = "Средний зерноуборочный комбайн 260 л.с., бункер 6 м³, жатка 7 м. Оптимален для небольших хозяйств.",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(rsmVector);

        var yenisei950 = new SpecialVehicle
        {
            Brand = "Енисей", Model = "КЗС-950", VehicleType = "combine",
            EngineFamily = "ЯМЗ-236ДК (185 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2012–н.в.", Description = "Зерноуборочный комбайн 185 л.с. Красноярского завода. Бункер 5 м³, жатка 6 м. Простая и надёжная конструкция.",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(yenisei950);

        var yenisei1200 = new SpecialVehicle
        {
            Brand = "Енисей", Model = "КЗС-1200", VehicleType = "combine",
            EngineFamily = "ЯМЗ-238ДК (240 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2014–н.в.", Description = "Зерноуборочный комбайн 240 л.с., бункер 7 м³, жатка 9 м. Усиленная ходовая часть для Сибири.",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(yenisei1200);

        var don1500 = new SpecialVehicle
        {
            Brand = "Дон", Model = "Дон-1500Б", VehicleType = "combine",
            EngineFamily = "ЯМЗ-238АК (235 л.с.)", Protocol = "K-Line", BusSpeedKbps = 10,
            Years = "2005–2015", Description = "Классический зерноуборочный комбайн 235 л.с., бункер 6 м³. Диагностика через K-Line (ISO 9141).",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(don1500);

        var don680 = new SpecialVehicle
        {
            Brand = "Дон", Model = "Дон-680М", VehicleType = "combine",
            EngineFamily = "ЯМЗ-6563 (280 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2016–н.в.", Description = "Модернизированный зерноуборочный комбайн 280 л.с. с электронной системой контроля потерь и влажности.",
            Icon = "🌾", CreatedAt = now,
        };
        await db.InsertAsync(don680);

        // ═══════════════════ АВТОКРАНЫ ═══════════════════

        var kranKs3571 = new SpecialVehicle
        {
            Brand = "Ивановец", Model = "КС-3571", VehicleType = "crane",
            EngineFamily = "ЯМЗ-236М2 (180 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2005–н.в.", Description = "Автокран 10 т на шасси МАЗ/КамАЗ. Длина стрелы 14 м, гуськом 21 м. ОГМ-240 с защитой от перегруза.",
            Icon = "🏗️", CreatedAt = now,
        };
        await db.InsertAsync(kranKs3571);

        var kranKs45717 = new SpecialVehicle
        {
            Brand = "Ивановец", Model = "КС-45717", VehicleType = "crane",
            EngineFamily = "ЯМЗ-536 (280 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2010–н.в.", Description = "Автокран 25 т на шасси КамАЗ-65115. Стрела 21,7 м + гуськом 29 м. ОГМ-240, микропроцессорный ограничитель.",
            Icon = "🏗️", CreatedAt = now,
        };
        await db.InsertAsync(kranKs45717);

        var kranKs35714 = new SpecialVehicle
        {
            Brand = "Ивановец", Model = "КС-35714К", VehicleType = "crane",
            EngineFamily = "КамАЗ-740.62 (280 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2015–н.в.", Description = "Автокран 16 т на шасси КамАЗ-43118. Стрела 18 м, телескопическая с гидроприводом.",
            Icon = "🏗️", CreatedAt = now,
        };
        await db.InsertAsync(kranKs35714);

        var kranKs55713 = new SpecialVehicle
        {
            Brand = "ГАКЗ", Model = "КС-55713", VehicleType = "crane",
            EngineFamily = "КамАЗ-740.71 (300 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2012–н.в.", Description = "Автокран 25 т Галичского завода на шасси КамАЗ-65115. Стрела 28 м, гидравлика с пропорциональным управлением.",
            Icon = "🏗️", CreatedAt = now,
        };
        await db.InsertAsync(kranKs55713);

        var kranKs65713 = new SpecialVehicle
        {
            Brand = "ГАКЗ", Model = "КС-65713", VehicleType = "crane",
            EngineFamily = "КамАЗ-740.75 (400 л.с.)", Protocol = "J1939", BusSpeedKbps = 250,
            Years = "2016–н.в.", Description = "Тяжёлый автокран 40 т на шасси КамАЗ-6520. Стрела 34 м, система координатной защиты ОГМ-240.",
            Icon = "🏗️", CreatedAt = now,
        };
        await db.InsertAsync(kranKs65713);

        // ═══════════════════ Коды ошибок: Кировец ═══════════════════

        var kirovErrors = new (int spn, int fmi, string lamp, string system, string desc, string causes, string fix, string severity)[]
        {
            // Engine — ЯМЗ-658
            (102, 0, "MIL", "engine", "Давление наддува — критически высокое",
                "Неисправность турбокомпрессора, забит воздушный фильтр, утечка наддувочного воздуха.",
                "Проверить турбокомпрессор, патрубки наддува, воздушный фильтр.", "high"),
            (102, 18, "AmberWarning", "engine", "Давление наддува — низкое (ниже нормы)",
                "Утечка в системе наддува, неисправность интеркулера, забит воздушный фильтр.",
                "Проверить герметичность впускного тракта, интеркулер, фильтр.", "medium"),
            (100, 1, "RedStop", "engine", "Давление масла — ниже критического",
                "Низкий уровень масла, неисправность масляного насоса, забит масляный фильтр.",
                "Немедленно заглушить двигатель! Проверить уровень и давление масла.", "critical"),
            (100, 3, "AmberWarning", "engine", "Давление масла — короткое замыкание на +",
                "Обрыв или замыкание проводки датчика давления масла, неисправность датчика.",
                "Проверить проводку и датчик давления масла.", "high"),
            (110, 3, "AmberWarning", "engine", "Температура ОЖ — короткое замыкание на +",
                "Обрыв цепи датчика температуры, неисправность датчика.",
                "Проверить проводку и датчик температуры ОЖ.", "medium"),
            (110, 0, "RedStop", "engine", "Температура ОЖ — перегрев",
                "Низкий уровень ОЖ, неисправность термостата, забит радиатор, отказ вентилятора.",
                "Заглушить двигатель! Проверить систему охлаждения.", "critical"),
            (110, 16, "AmberWarning", "engine", "Температура ОЖ — выше нормы",
                "Частичное засорение радиатора, слабый поток ОЖ.",
                "Проверить радиатор и помпу.", "medium"),
            (174, 0, "RedStop", "engine", "Температура топлива — критическая",
                "Перегрев топлива, неисправность топливного охладителя.",
                "Проверить систему охлаждения топлива.", "high"),
            (94, 1, "AmberWarning", "engine", "Давление топлива — низкое",
                "Засорение топливного фильтра, неисправность ТНВД, подсос воздуха.",
                "Заменить топливный фильтр, проверить ТНВД и магистрали.", "high"),
            (94, 0, "RedStop", "engine", "Давление топлива — критически высокое",
                "Засорение обратной магистрали, неисправность регулятора давления.",
                "Проверить обратку и регулятор давления топлива.", "high"),
            (190, 0, "AmberWarning", "engine", "Частота вращения коленвала — выше нормы",
                "Неисправность регулятора оборотов, заклинивание ТНВД.",
                "Проверить регулятор оборотов.", "high"),
            (91, 13, "AmberWarning", "engine", "Положение педали газа — ошибка калибровки",
                "Сбой калибровки датчика положения педали.",
                "Выполнить калибровку педали газа через диагностический прибор.", "low"),
        };

        foreach (var (spn, fmi, lamp, system, desc, causes, fix, severity) in kirovErrors)
        {
            await db.InsertAsync(new SpecialErrorCode
            {
                VehicleId = kirovetsK744.Id, Code = $"SPN{spn}FMI{fmi}",
                CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                Lamp = lamp, System = system, Description = desc,
                Causes = causes, FixRecommendation = fix, Severity = severity,
                CreatedAt = now,
            });
            added++;
        }

        // ═══════════════════ Коды ошибок: Беларус/МТЗ ═══════════════════

        var belarusErrors = new (int spn, int fmi, string lamp, string system, string desc, string causes, string fix, string severity)[]
        {
            // Engine — ММЗ Д-260
            (110, 0, "RedStop", "engine", "Температура охлаждающей жидкости — перегрев",
                "Низкий уровень ОЖ, неисправность термостата или вентилятора.",
                "Заглушить двигатель! Проверить уровень ОЖ и систему охлаждения.", "critical"),
            (110, 3, "AmberWarning", "engine", "Датчик температуры ОЖ — обрыв цепи",
                "Обрыв проводки или неисправность датчика.",
                "Проверить цепь датчика температуры.", "medium"),
            (100, 1, "RedStop", "engine", "Давление масла — ниже нормы",
                "Низкий уровень масла, износ масляного насоса.",
                "Немедленно заглушить! Проверить уровень масла.", "critical"),
            (174, 0, "AmberWarning", "engine", "Температура топлива — высокая",
                "Перегрев топлива в магистрали.",
                "Проверить топливный охладитель.", "medium"),
            (157, 3, "AmberWarning", "engine", "Давление в топливной рампе — обрыв датчика",
                "Обрыв цепи датчика давления в рампе.",
                "Проверить проводку датчика давления топлива.", "high"),
            (157, 1, "RedStop", "engine", "Давление в топливной рампе — ниже нормы",
                "Засорение фильтра, неисправность ТНВД, подсос воздуха.",
                "Заменить топливный фильтр, проверить ТНВД.", "high"),
            (637, 12, "AmberWarning", "engine", "Датчик положения коленвала — ошибка сигнала",
                "Неисправность ДПКВ, зазор до задающего диска.",
                "Проверить датчик коленвала и зазор.", "high"),
            (636, 5, "AmberWarning", "engine", "Датчик положения распредвала — ток ниже нормы",
                "Обрыв цепи датчика распредвала.",
                "Проверить проводку датчика распредвала.", "medium"),
            (91, 2, "AmberWarning", "engine", "Датчик педали газа — некорректный сигнал",
                "Износ потенциометра, окисление контактов.",
                "Проверить датчик педали газа, очистить контакты.", "medium"),
            (108, 3, "AmberWarning", "engine", "Датчик атмосферного давления — обрыв",
                "Неисправность датчика или проводки.",
                "Проверить датчик атмосферного давления.", "low"),
        };

        foreach (var (spn, fmi, lamp, system, desc, causes, fix, severity) in belarusErrors)
        {
            await db.InsertAsync(new SpecialErrorCode
            {
                VehicleId = belarus1221.Id, Code = $"SPN{spn}FMI{fmi}",
                CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                Lamp = lamp, System = system, Description = desc,
                Causes = causes, FixRecommendation = fix, Severity = severity,
                CreatedAt = now,
            });
            added++;
        }

        // ═══════════════════ Коды ошибок: ДСТ-Урал ═══════════════════

        var dstErrors = new (int spn, int fmi, string lamp, string system, string desc, string causes, string fix, string severity)[]
        {
            // Engine — ЯМЗ-236/238
            (110, 0, "RedStop", "engine", "Температура ОЖ — критический перегрев",
                "Низкий уровень ОЖ, забит радиатор, отказ вентилятора.",
                "Немедленно заглушить двигатель!", "critical"),
            (110, 16, "AmberWarning", "engine", "Температура ОЖ — повышена",
                "Частичное засорение радиатора.",
                "Очистить радиатор, проверить натяжение ремня вентилятора.", "medium"),
            (100, 1, "RedStop", "engine", "Давление масла — аварийное",
                "Низкий уровень масла, забит маслозаборник.",
                "Немедленно заглушить! Проверить систему смазки.", "critical"),
            (102, 18, "AmberWarning", "engine", "Давление наддува — низкое",
                "Утечка наддувочного воздуха, неисправность турбины.",
                "Проверить патрубки наддува и турбокомпрессор.", "high"),
            (174, 0, "AmberWarning", "engine", "Температура топлива — высокая",
                "Перегрев топлива в системе.",
                "Проверить топливный бак и магистрали.", "medium"),
            (637, 8, "AmberWarning", "engine", "Датчик коленвала — пропуск сигнала",
                "Неисправность ДПКВ или задающего диска.",
                "Проверить датчик коленвала и зазор.", "high"),
            (190, 0, "RedStop", "engine", "Обороты двигателя — превышение",
                "Разнос двигателя, неисправность регулятора.",
                "Немедленно заглушить!", "critical"),
            (111, 1, "AmberWarning", "engine", "Уровень ОЖ — низкий",
                "Утечка ОЖ, недостаточный уровень.",
                "Долить ОЖ, проверить герметичность системы.", "high"),
            (98, 1, "AmberWarning", "engine", "Уровень масла — низкий",
                "Расход масла, утечка.",
                "Долить масло, проверить на утечки.", "medium"),
        };

        foreach (var (spn, fmi, lamp, system, desc, causes, fix, severity) in dstErrors)
        {
            await db.InsertAsync(new SpecialErrorCode
            {
                VehicleId = dstDt75.Id, Code = $"SPN{spn}FMI{fmi}",
                CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                Lamp = lamp, System = system, Description = desc,
                Causes = causes, FixRecommendation = fix, Severity = severity,
                CreatedAt = now,
            });
            added++;
        }

        // ═══════════════════ Коды ошибок: Грейдеры ═══════════════════

        var graderErrors = new (int spn, int fmi, string lamp, string system, string desc, string causes, string fix, string severity)[]
        {
            // Engine — общие для ДЗ-98 и ДЗ-122
            (110, 0, "RedStop", "engine", "Температура ОЖ — критический перегрев",
                "Низкий уровень ОЖ, отказ вентилятора, забит радиатор.",
                "Немедленно заглушить двигатель!", "critical"),
            (110, 16, "AmberWarning", "engine", "Температура ОЖ — выше нормы",
                "Частичное засорение радиатора, слабый поток ОЖ.",
                "Очистить радиатор, проверить помпу.", "medium"),
            (100, 1, "RedStop", "engine", "Давление масла — аварийно низкое",
                "Низкий уровень масла, забит маслозаборник, износ насоса.",
                "Немедленно заглушить! Проверить систему смазки.", "critical"),
            (100, 3, "AmberWarning", "engine", "Датчик давления масла — обрыв цепи",
                "Обрыв проводки, неисправность датчика.",
                "Проверить цепь датчика давления масла.", "medium"),
            (102, 0, "MIL", "engine", "Давление наддува — критически высокое",
                "Неисправность турбокомпрессора, заедание перепускного клапана.",
                "Проверить турбокомпрессор и вестгейт.", "high"),
            (102, 18, "AmberWarning", "engine", "Давление наддува — низкое",
                "Утечка воздуха, неисправность турбины, забит фильтр.",
                "Проверить патрубки наддува и воздушный фильтр.", "high"),
            (174, 0, "AmberWarning", "engine", "Температура топлива — высокая",
                "Перегрев топлива, неисправность охладителя.",
                "Проверить топливный охладитель.", "medium"),
            (94, 1, "RedStop", "engine", "Давление топлива — низкое",
                "Засорение фильтра, неисправность ТНВД, подсос воздуха.",
                "Заменить топливный фильтр, прокачать систему.", "high"),
            (637, 8, "AmberWarning", "engine", "Датчик коленвала — пропуск импульсов",
                "Неисправность датчика, повреждение задающего диска.",
                "Проверить датчик положения коленвала.", "high"),
            (91, 13, "AmberWarning", "engine", "Датчик педали газа — ошибка калибровки",
                "Сбой калибровки, износ потенциометра.",
                "Выполнить калибровку педали газа.", "low"),
            // Hydraulics
            (521, 7, "AmberWarning", "hydraulics", "Гидрораспределитель — механическая ошибка",
                "Заклинивание золотника, износ гидрораспределителя.",
                "Проверить гидрораспределитель, заменить при износе.", "high"),
            (522, 1, "RedStop", "hydraulics", "Давление в гидросистеме — ниже нормы",
                "Низкий уровень масла, неисправность гидронасоса, утечка.",
                "Проверить уровень гидромасла, насос и магистрали.", "critical"),
            // Brakes
            (610, 3, "AmberWarning", "brakes", "Датчик давления в тормозной системе — обрыв",
                "Обрыв проводки или неисправность датчика.",
                "Проверить цепь датчика тормозного давления.", "medium"),
            (610, 1, "RedStop", "brakes", "Давление в тормозной системе — ниже нормы",
                "Утечка, низкий уровень тормозной жидкости, износ колодок.",
                "Немедленно проверить тормозную систему!", "critical"),
        };

        foreach (var (spn, fmi, lamp, system, desc, causes, fix, severity) in graderErrors)
        {
            await db.InsertAsync(new SpecialErrorCode
            {
                VehicleId = dz98.Id, Code = $"SPN{spn}FMI{fmi}",
                CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                Lamp = lamp, System = system, Description = desc,
                Causes = causes, FixRecommendation = fix, Severity = severity,
                CreatedAt = now,
            });
            await db.InsertAsync(new SpecialErrorCode
            {
                VehicleId = dz122.Id, Code = $"SPN{spn}FMI{fmi}",
                CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                Lamp = lamp, System = system, Description = desc,
                Causes = causes, FixRecommendation = fix, Severity = severity,
                CreatedAt = now,
            });
            added += 2;
        }

        // ═══════════════════ Коды ошибок: Комбайны ═══════════════════

        var combineCommonCodes = new (int spn, int fmi, string lamp, string system, string desc, string causes, string fix, string severity)[]
        {
            // Engine — стандартные ЯМЗ
            (110, 0, "RedStop", "engine", "Температура ОЖ — перегрев",
                "Низкий уровень ОЖ, забит радиатор (пыль/полова).",
                "Очистить радиатор, проверить натяжение ремня.", "critical"),
            (100, 1, "RedStop", "engine", "Давление масла — аварийное",
                "Низкий уровень, забит маслозаборник.",
                "Немедленно заглушить, проверить систему смазки.", "critical"),
            (102, 18, "AmberWarning", "engine", "Давление наддува — низкое",
                "Засорение воздушного фильтра (пыль), утечка наддува.",
                "Заменить воздушный фильтр, проверить патрубки.", "high"),
            (174, 0, "AmberWarning", "engine", "Температура топлива — высокая",
                "Перегрев топлива при длительной работе.",
                "Проверить топливный охладитель.", "medium"),
            (94, 1, "AmberWarning", "engine", "Давление топлива — низкое",
                "Засорение топливного фильтра, подсос воздуха.",
                "Заменить фильтр, прокачать систему.", "high"),

            // Header / Жатка
            (513, 7, "AmberWarning", "header", "Датчик высоты жатки — механическая ошибка",
                "Заклинивание датчика, повреждение копирующего башмака.",
                "Проверить механизм копирования, заменить датчик.", "high"),
            (514, 3, "AmberWarning", "header", "Датчик частоты вращения мотовила — обрыв цепи",
                "Обрыв проводки или неисправность датчика мотовила.",
                "Проверить проводку датчика мотовила.", "medium"),
            (514, 8, "AmberWarning", "header", "Датчик мотовила — пропуск сигнала",
                "Повреждение датчика, неправильный зазор.",
                "Проверить датчик и зазор до задающего диска.", "medium"),
            (515, 1, "RedStop", "header", "Привод жатки — аварийная пробуксовка",
                "Забивание жатки, перегрузка, обрыв ремня.",
                "Остановить, очистить жатку, проверить приводной ремень.", "critical"),

            // Threshing / Молотилка
            (540, 0, "MIL", "threshing", "Барабан молотильный — превышение оборотов",
                "Пробуксовка вариатора, неисправность гидропривода.",
                "Проверить вариатор оборотов барабана.", "high"),
            (540, 1, "RedStop", "threshing", "Барабан молотильный — обороты ниже нормы",
                "Перегрузка, забивание, проскальзывание ремня.",
                "Уменьшить подачу, очистить подбарабанье.", "critical"),
            (541, 7, "AmberWarning", "threshing", "Датчик зазора подбарабанья — ошибка",
                "Неисправность датчика или механизма регулировки деки.",
                "Проверить датчик зазора деки.", "medium"),
            (542, 3, "AmberWarning", "threshing", "Датчик частоты вращения вентилятора очистки — обрыв",
                "Обрыв цепи датчика вентилятора.",
                "Проверить проводку датчика вентилятора.", "medium"),
            (542, 1, "RedStop", "threshing", "Вентилятор очистки — обороты ниже нормы",
                "Проскальзывание ремня, забивание решёт.",
                "Проверить привод вентилятора, очистить решёта.", "high"),

            // Grain handling / Зерновая система
            (550, 8, "AmberWarning", "grain", "Датчик зернового элеватора — пропуск сигнала",
                "Повреждение датчика или цепи элеватора.",
                "Проверить датчик и цепь элеватора.", "high"),
            (551, 1, "RedStop", "grain", "Шнек выгрузной — аварийная остановка",
                "Забивание выгрузного шнека, перегрузка.",
                "Остановить выгрузку, очистить шнек.", "critical"),
            (552, 3, "AmberWarning", "grain", "Датчик заполнения бункера — обрыв цепи",
                "Обрыв проводки или неисправность датчика уровня зерна.",
                "Проверить датчик уровня бункера.", "medium"),
            (553, 0, "AmberWarning", "grain", "Датчик потерь зерна — высокие потери",
                "Превышение допустимых потерь за соломотрясом/очисткой.",
                "Отрегулировать обороты вентилятора и зазор деки.", "high"),

            // Yield monitoring / Агротроник
            (560, 3, "AmberWarning", "yield", "Датчик влажности зерна — обрыв",
                "Обрыв цепи датчика влажности.",
                "Проверить датчик влажности.", "medium"),
            (561, 12, "AmberWarning", "yield", "Датчик урожайности — ошибка калибровки",
                "Сбита калибровка датчика массы.",
                "Выполнить калибровку датчика урожайности.", "low"),
            (630, 9, "AmberWarning", "yield", "GPS-приёмник — потеря связи",
                "Пропадание сигнала GPS, неисправность антенны.",
                "Проверить GPS-антенну и кабель.", "low"),
        };

        var combineVehicleIds = new[] { rsmAcros.Id, rsmTorum.Id, rsmVector.Id, yenisei950.Id, yenisei1200.Id, don680.Id };
        foreach (var (spn, fmi, lamp, system, desc, causes, fix, severity) in combineCommonCodes)
        {
            foreach (var vid in combineVehicleIds)
            {
                await db.InsertAsync(new SpecialErrorCode
                {
                    VehicleId = vid, Code = $"SPN{spn}FMI{fmi}",
                    CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                    Lamp = lamp, System = system, Description = desc,
                    Causes = causes, FixRecommendation = fix, Severity = severity,
                    CreatedAt = now,
                });
                added++;
            }
        }

        // ═══════════════════ Коды ошибок: Автокраны ═══════════════════

        var craneCommonCodes = new (int spn, int fmi, string lamp, string system, string desc, string causes, string fix, string severity)[]
        {
            // Engine (шасси КамАЗ/ЯМЗ)
            (110, 0, "RedStop", "engine", "Температура ОЖ — перегрев",
                "Низкий уровень ОЖ, забит радиатор.",
                "Заглушить, проверить систему охлаждения.", "critical"),
            (100, 1, "RedStop", "engine", "Давление масла — аварийное",
                "Низкий уровень, неисправность насоса.",
                "Немедленно заглушить!", "critical"),
            (94, 1, "AmberWarning", "engine", "Давление топлива — низкое",
                "Засорение фильтра, подсос воздуха.",
                "Заменить фильтр, прокачать.", "high"),

            // ОГМ-240 — Ограничитель Грузоподъёмности
            (650, 1, "RedStop", "crane", "Перегруз крана — превышение грузового момента",
                "Подъём груза выше номинала, неисправность датчика нагрузки.",
                "Опустить груз! Проверить настройки ОГМ.", "critical"),
            (650, 3, "AmberWarning", "crane", "Датчик нагрузки (тензодатчик) — обрыв",
                "Обрыв цепи тензодатчика, повреждение кабеля.",
                "Проверить тензодатчик и кабель.", "high"),
            (651, 3, "AmberWarning", "crane", "Датчик угла стрелы — обрыв",
                "Обрыв цепи датчика угла наклона стрелы.",
                "Проверить датчик угла стрелы.", "high"),
            (651, 2, "AmberWarning", "crane", "Датчик угла стрелы — некорректный сигнал",
                "Сбой калибровки, магнитные помехи.",
                "Перекалибровать датчик угла.", "medium"),
            (652, 3, "AmberWarning", "crane", "Датчик вылета стрелы — обрыв",
                "Обрыв цепи датчика длины стрелы.",
                "Проверить кабель-барабан датчика вылета.", "high"),
            (653, 1, "RedStop", "crane", "Датчик азимута (поворота) — ошибка",
                "Неисправность датчика поворота платформы.",
                "Проверить датчик азимута.", "high"),

            // Hydraulic system
            (660, 1, "RedStop", "hydraulics", "Давление в гидросистеме крана — ниже нормы",
                "Низкий уровень масла, неисправность гидронасоса, утечка.",
                "Проверить уровень гидромасла, насос.", "critical"),
            (660, 3, "AmberWarning", "hydraulics", "Датчик давления гидросистемы — обрыв",
                "Обрыв цепи датчика давления.",
                "Проверить датчик и проводку.", "medium"),
            (661, 0, "MIL", "hydraulics", "Температура гидромасла — перегрев",
                "Интенсивная работа, засорение радиатора гидравлики.",
                "Дать остыть, проверить радиатор.", "high"),

            // Outriggers / Аутригеры
            (670, 7, "RedStop", "outriggers", "Аутригеры — не выдвинуты / ошибка",
                "Попытка работы без опор, неисправность концевых выключателей.",
                "Выдвинуть аутригеры, проверить концевики.", "critical"),
            (670, 3, "AmberWarning", "outriggers", "Концевой выключатель аутригера — обрыв",
                "Обрыв цепи концевого выключателя.",
                "Проверить концевик аутригера.", "medium"),

            // Winch / Лебёдка
            (680, 1, "RedStop", "winch", "Лебёдка — перегруз момента",
                "Превышение допустимого момента на барабане лебёдки.",
                "Опустить груз, проверить настройки.", "critical"),
            (680, 3, "AmberWarning", "winch", "Датчик оборотов лебёдки — обрыв",
                "Обрыв цепи датчика скорости намотки троса.",
                "Проверить датчик лебёдки.", "medium"),

            // Safety / Защита
            (690, 0, "RedStop", "safety", "Ветровая защита — превышение скорости ветра",
                "Скорость ветра выше допустимой для работы крана.",
                "Прекратить работу, опустить стрелу.", "critical"),
            (690, 3, "AmberWarning", "safety", "Анемометр — обрыв цепи",
                "Обрыв цепи датчика ветра.",
                "Проверить анемометр.", "low"),
        };

        var craneVehicleIds = new[] { kranKs3571.Id, kranKs45717.Id, kranKs35714.Id, kranKs55713.Id, kranKs65713.Id };
        foreach (var (spn, fmi, lamp, system, desc, causes, fix, severity) in craneCommonCodes)
        {
            foreach (var vid in craneVehicleIds)
            {
                await db.InsertAsync(new SpecialErrorCode
                {
                    VehicleId = vid, Code = $"SPN{spn}FMI{fmi}",
                    CodeType = "j1939", SPN = spn, FMI = fmi, SourceAddress = 0,
                    Lamp = lamp, System = system, Description = desc,
                    Causes = causes, FixRecommendation = fix, Severity = severity,
                    CreatedAt = now,
                });
                added++;
            }
        }

        // ═══════════════════ ЭБУ: Кировец ═══════════════════

        var kirovEcus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ ЯМЗ-658 EDC7UC31", 0x00, "J1939", "BOSCH", "engine", "Электронный блок управления двигателем ЯМЗ-658."),
            ("Контроллер АКПП", 0x03, "J1939", "ZF", "transmission", "Управление автоматической коробкой передач ZF."),
            ("Контроллер гидравлики", 0x18, "J1939", "Bosch Rexroth", "hydraulic", "Управление навесной гидросистемой."),
            ("Панель приборов", 0x28, "CAN", "ИТЭЛМА", "instrument", "Цифровая приборная панель с J1939-интерфейсом."),
        };
        foreach (var (name, sa, proto, mfr, func, desc) in kirovEcus)
            await db.InsertAsync(new SpecialVehicleECU { VehicleId = kirovetsK744.Id, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });

        // ═══════════════════ ЭБУ: Беларус ═══════════════════

        var belarusEcus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ ММЗ Д-260 EDCS6", 0x00, "J1939", "BOSCH", "engine", "Блок управления двигателем ММЗ Д-260 Common Rail."),
            ("Контроллер ВОМ/КПП", 0x04, "CAN", "МЗА", "transmission", "Управление валом отбора мощности и КПП."),
            ("Блок управления навеской", 0x18, "CAN", "BOSCH", "hydraulic", "Электронная регулировка навески (EHR)."),
            ("Комбинация приборов", 0x28, "CAN", "ИТЭЛМА", "instrument", "Электронная панель с индикацией ошибок."),
        };
        foreach (var (name, sa, proto, mfr, func, desc) in belarusEcus)
            await db.InsertAsync(new SpecialVehicleECU { VehicleId = belarus1221.Id, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });

        // ═══════════════════ ЭБУ: ДСТ-Урал ═══════════════════

        var dstEcus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ ЯМЗ-238 ЯЗДА М230", 0x00, "J1939", "ЯЗДА", "engine", "Блок управления дизелем ЯМЗ-238НД."),
            ("Блок управления трансмиссией", 0x03, "J1939", "МЗКТ", "transmission", "Управление гусеничной трансмиссией."),
            ("Панель приборов", 0x28, "CAN", "ИТЭЛМА", "instrument", "Приборная панель с J1939."),
        };
        foreach (var (name, sa, proto, mfr, func, desc) in dstEcus)
            await db.InsertAsync(new SpecialVehicleECU { VehicleId = dstFt80.Id, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });

        // ═══════════════════ ЭБУ: Грейдеры ═══════════════════

        var graderEcus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ ЯМЗ-236НД (ДЗ-122)", 0x00, "J1939", "ЯЗДА", "engine", "Блок управления дизелем Cummins-аналогом."),
            ("ЭБУ Д-180 (ДЗ-98)", 0x00, "J1939", "ЧТЗ", "engine", "Контроллер двигателя Д-180."),
            ("Контроллер трансмиссии", 0x03, "J1939", "ZF/Bosch", "transmission", "Управление гидромеханической трансмиссией грейдера."),
            ("Блок управления отвалом", 0x10, "J1939", "Sauer-Danfoss", "hydraulic", "Электрогидравлическое управление отвалом и рыхлителем."),
            ("Блок ABS/EBS", 0x0B, "J1939", "WABCO", "brakes", "Антиблокировочная система тормозов."),
        };
        foreach (var (name, sa, proto, mfr, func, desc) in graderEcus)
        {
            await db.InsertAsync(new SpecialVehicleECU { VehicleId = dz98.Id, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });
            await db.InsertAsync(new SpecialVehicleECU { VehicleId = dz122.Id, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });
        }

        // ═══════════════════ ЭБУ: Комбайны ═══════════════════

        var combineEcus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ ЯМЗ (Common Rail)", 0x00, "J1939", "BOSCH/ЯЗДА", "engine", "Управление двигателем ЯМЗ-658/536/6563."),
            ("Контроллер жатки (AutoHeader)", 0x10, "J1939", "Ростсельмаш", "header", "Автоматическое копирование рельефа, управление мотовилом."),
            ("Контроллер молотилки", 0x14, "J1939", "Ростсельмаш", "threshing", "Управление оборотами барабана, зазором деки, вентилятором."),
            ("Контроллер зерновой системы", 0x18, "J1939", "Ростсельмаш", "grain", "Элеватор, выгрузной шнек, датчики заполнения бункера."),
            ("Контроллер потерь", 0x1C, "J1939", "Ростсельмаш", "yield", "Датчики потерь за соломотрясом и очисткой."),
            ("Блок Агротроник / RSM AutoPilot", 0x20, "J1939", "РСМ/Topcon", "yield", "GPS-автопилот, картирование урожайности, влажность."),
            ("Терминал кабины (Advisor)", 0x28, "CAN", "РСМ/Bosch", "instrument", "Сенсорный дисплей с индикацией ошибок и настройками."),
        };

        foreach (var vid in combineVehicleIds)
        {
            foreach (var (name, sa, proto, mfr, func, desc) in combineEcus)
            {
                await db.InsertAsync(new SpecialVehicleECU { VehicleId = vid, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });
            }
        }

        // ЭБУ для Дон-1500Б (K-Line) — отдельно
        var don1500Ecus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ ЯМЗ-238АК (K-Line)", 0x00, "K-Line", "ЯЗДА", "engine", "Управление двигателем ЯМЗ-238АК через K-Line."),
            ("Контроллер жатки", 0x10, "K-Line", "РСМ", "header", "Управление жаткой."),
            ("Контроллер молотилки", 0x14, "K-Line", "РСМ", "threshing", "Управление обмолотом."),
        };
        foreach (var (name, sa, proto, mfr, func, desc) in don1500Ecus)
            await db.InsertAsync(new SpecialVehicleECU { VehicleId = don1500.Id, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });

        // ═══════════════════ ЭБУ: Автокраны ═══════════════════

        var craneEcus = new (string name, int sa, string protocol, string mfr, string func, string desc)[]
        {
            ("ЭБУ двигателя (КамАЗ-740)", 0x00, "J1939", "BOSCH/АБИТ", "engine", "Управление дизелем КамАЗ-740.71/75."),
            ("ЭБУ двигателя (ЯМЗ-236/536)", 0x00, "J1939", "ЯЗДА/BOSCH", "engine", "Управление дизелем ЯМЗ-236М2/536."),
            ("Блок ОГМ-240", 0x30, "J1939", "Ивановец/ИЗА", "crane", "Ограничитель грузоподъёмности: нагрузка, вылет, азимут, угол, ветрозащита."),
            ("Контроллер гидравлики крана", 0x40, "J1939", "Bosch Rexroth", "hydraulics", "Пропорциональное управление гидрораспределителями стрелы и лебёдки."),
            ("Контроллер аутригеров", 0x44, "J1939", "HYVA", "outriggers", "Управление выдвижными опорами с контролем горизонта."),
            ("Блок лебёдки", 0x48, "J1939", "ИЗА", "winch", "Контроль намотки троса, момента лебёдки."),
            ("Панель безопасности", 0x4C, "J1939", "Ивановец", "safety", "Контроль зон работы, анемометр, креномер, звуковая сигнализация."),
        };

        foreach (var vid in craneVehicleIds)
        {
            foreach (var (name, sa, proto, mfr, func, desc) in craneEcus)
            {
                await db.InsertAsync(new SpecialVehicleECU { VehicleId = vid, ECUName = name, SourceAddress = sa, Protocol = proto, Manufacturer = mfr, Function = func, Description = desc, CreatedAt = now });
            }
        }

        var totalEcus = kirovEcus.Length + belarusEcus.Length + dstEcus.Length
                        + graderEcus.Length * 2
                        + combineEcus.Length * combineVehicleIds.Length + don1500Ecus.Length
                        + craneEcus.Length * craneVehicleIds.Length;

        Debug.WriteLine($"[SpecialVehicle] Seeded {25} vehicles + {added} error codes + {totalEcus} ECUs.");
        return added;
    }

    // ═══════════════════ ELM327: команды J1939 ═══════════════════

    /// <summary>
    /// Возвращает AT-команды для переключения ELM327 в режим J1939.
    /// </summary>
    public string GetJ1939InitCommands(int busSpeedKbps = 250)
    {
        var cmds = new List<string>
        {
            "ATZ",              // сброс
            "ATE0",             // эхо выкл
            "ATL0",             // переводы строк выкл
            "ATH0",             // заголовки выкл
            "ATSP6",            // протокол ISO 15765-4 (CAN 11-bit 500kbps)
        };

        if (busSpeedKbps == 250)
            cmds.Add("ATPB 2501"); // J1939 250 kbps

        cmds.Add("ATCF 0");        // принудительный CAN-формат
        cmds.Add("ATCAF 0");       // автоформат выкл

        return string.Join("\r", cmds);
    }

    /// <summary>
    /// Парсит J1939-фрейм вида 18FEEE00 00 12 34 AB CD EF FF 00
    /// Возвращает (PGN, SA, DataBytes).
    /// </summary>
    public static (uint PGN, byte SourceAddress, byte[] Data) ParseJ1939Frame(string rawCanFrame)
    {
        var parts = rawCanFrame.Replace("\r", "").Replace("\n", "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return (0, 0, Array.Empty<byte>());

        // Первые 4 символа — CAN ID (29-битный)
        var canIdHex = parts[0];
        if (!uint.TryParse(canIdHex, System.Globalization.NumberStyles.HexNumber, null, out var canId))
            return (0, 0, Array.Empty<byte>());

        // J1939: PGN = (canId >> 8) & 0x3FFFF, SA = canId & 0xFF
        uint pgn = (canId >> 8) & 0x3FFFF;
        byte sa = (byte)(canId & 0xFF);

        // Остальные части — байты данных
        var data = parts.Skip(1).Select(p => byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out var b) ? b : (byte)0).ToArray();

        return (pgn, sa, data);
    }
}
