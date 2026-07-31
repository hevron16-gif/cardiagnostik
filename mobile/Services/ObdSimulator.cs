using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Полный симулятор ELM327 для тестирования без автомобиля.
/// Имитирует адаптер: PID, DTC (03/07/0A), freeze frame, AT-команды,
/// профили российских авто, естественную генерацию ошибок.
/// </summary>
public class ObdSimulator
{
    // ══════════════ Конфигурации российских авто ══════════════
    public static readonly IReadOnlyDictionary<string, CarProfile> Cars = new Dictionary<string, CarProfile>
    {
        ["Lada Vesta 1.8"] = new() { Brand = "LADA", Model = "Vesta", Displacement = 1.8, MaxRPM = 6400, IdleRPM = 840, MaxSpeed = 190, FuelTankL = 55, ECU = "M86", EngineCode = "ВАЗ-21179" },
        ["Lada Granta 1.6"] = new() { Brand = "LADA", Model = "Granta", Displacement = 1.6, MaxRPM = 6200, IdleRPM = 850, MaxSpeed = 178, FuelTankL = 50, ECU = "M74", EngineCode = "ВАЗ-11186" },
        ["Lada Niva Legend"] = new() { Brand = "LADA", Model = "Niva Legend", Displacement = 1.7, MaxRPM = 5600, IdleRPM = 800, MaxSpeed = 140, FuelTankL = 65, ECU = "Bosch MP7.0", EngineCode = "ВАЗ-21214" },
        ["УАЗ Patriot 2.7"] = new() { Brand = "УАЗ", Model = "Patriot", Displacement = 2.7, MaxRPM = 5000, IdleRPM = 780, MaxSpeed = 150, FuelTankL = 87, ECU = "Mikas 12.3", EngineCode = "ЗМЗ-40906" },
        ["УАЗ Буханка"] = new() { Brand = "УАЗ", Model = "Буханка", Displacement = 2.7, MaxRPM = 5000, IdleRPM = 750, MaxSpeed = 120, FuelTankL = 77, ECU = "Mikas 7.2", EngineCode = "ЗМЗ-4091" },
        ["ГАЗ Соболь NN"] = new() { Brand = "ГАЗ", Model = "Соболь NN", Displacement = 2.8, MaxRPM = 4200, IdleRPM = 750, MaxSpeed = 170, FuelTankL = 80, ECU = "Bosch EDC17", EngineCode = "Cummins ISF2.8", FuelType = "Дизель" },
        ["ГАЗель Next"] = new() { Brand = "ГАЗ", Model = "ГАЗель Next", Displacement = 2.8, MaxRPM = 4200, IdleRPM = 750, MaxSpeed = 150, FuelTankL = 95, ECU = "Bosch EDC17", EngineCode = "Cummins ISF2.8", FuelType = "Дизель" },
        ["Москвич 3"] = new() { Brand = "Москвич", Model = "3", Displacement = 1.5, MaxRPM = 6000, IdleRPM = 820, MaxSpeed = 190, FuelTankL = 55, ECU = "Delphi MT92", EngineCode = "JAC HFC4GB2" },
        ["КамАЗ 54901"] = new() { Brand = "КамАЗ", Model = "54901", Displacement = 12.0, MaxRPM = 2600, IdleRPM = 600, MaxSpeed = 110, FuelTankL = 700, ECU = "Bosch MD1", EngineCode = "КамАЗ-910.12", FuelType = "Дизель" },
        ["МТЗ-82"] = new() { Brand = "МТЗ", Model = "82.1", Displacement = 4.75, MaxRPM = 2200, IdleRPM = 650, MaxSpeed = 35, FuelTankL = 130, ECU = "Мотороник", EngineCode = "Д-243", FuelType = "Дизель" },
        ["Aurus Senat"] = new() { Brand = "Aurus", Model = "Senat", Displacement = 4.4, MaxRPM = 6800, IdleRPM = 700, MaxSpeed = 250, FuelTankL = 80, ECU = "НАМИ", EngineCode = "НАМИ-4123", FuelType = "Бензин Twin-Turbo" },
    };

    // ══════════════ База ошибок с описаниями ══════════════
    public static readonly IReadOnlyDictionary<string, string> ErrorDB = new Dictionary<string, string>
    {
        ["P0100"] = "MAF — неисправность цепи",
        ["P0101"] = "MAF — выход за диапазон",
        ["P0102"] = "MAF — низкий сигнал",
        ["P0103"] = "MAF — высокий сигнал",
        ["P0110"] = "IAT — неисправность цепи",
        ["P0113"] = "IAT — высокий сигнал",
        ["P0115"] = "ECT — неисправность цепи",
        ["P0116"] = "ECT — выход за диапазон",
        ["P0117"] = "ECT — низкий сигнал (перегрев)",
        ["P0118"] = "ECT — высокий сигнал",
        ["P0120"] = "TPS — неисправность цепи",
        ["P0121"] = "TPS — выход за диапазон",
        ["P0122"] = "TPS — низкий сигнал",
        ["P0123"] = "TPS — высокий сигнал",
        ["P0130"] = "O₂ B1S1 — неисправность цепи",
        ["P0131"] = "O₂ B1S1 — низкое напряжение",
        ["P0132"] = "O₂ B1S1 — высокое напряжение",
        ["P0133"] = "O₂ B1S1 — медленный отклик",
        ["P0135"] = "Датчик O₂ B1S1 — неисправность нагревателя",
        ["P0136"] = "O₂ B1S2 — неисправность цепи",
        ["P0137"] = "O₂ B1S2 — низкое напряжение",
        ["P0138"] = "O₂ B1S2 — высокое напряжение",
        ["P0141"] = "Датчик O₂ B1S2 — неисправность нагревателя",
        ["P0170"] = "Топливная коррекция — нарушение",
        ["P0171"] = "Слишком бедная смесь (Bank 1)",
        ["P0172"] = "Слишком богатая смесь (Bank 1)",
        ["P0173"] = "Топливная коррекция — нарушение (Bank 2)",
        ["P0174"] = "Слишком бедная смесь (Bank 2)",
        ["P0201"] = "Форсунка 1 — неисправность",
        ["P0202"] = "Форсунка 2 — неисправность",
        ["P0203"] = "Форсунка 3 — неисправность",
        ["P0204"] = "Форсунка 4 — неисправность",
        ["P0300"] = "Случайные пропуски зажигания",
        ["P0301"] = "Пропуски зажигания — цилиндр 1",
        ["P0302"] = "Пропуски зажигания — цилиндр 2",
        ["P0303"] = "Пропуски зажигания — цилиндр 3",
        ["P0304"] = "Пропуски зажигания — цилиндр 4",
        ["P0325"] = "Датчик детонации — неисправность цепи",
        ["P0340"] = "Датчик распредвала — неисправность",
        ["P0351"] = "Катушка A — неисправность",
        ["P0352"] = "Катушка B — неисправность",
        ["P0400"] = "EGR — неисправность потока",
        ["P0401"] = "EGR — недостаточный поток",
        ["P0402"] = "EGR — избыточный поток",
        ["P0420"] = "Низкая эффективность катализатора (Bank 1)",
        ["P0430"] = "Низкая эффективность катализатора (Bank 2)",
        ["P0440"] = "EVAP — неисправность системы",
        ["P0441"] = "EVAP — некорректная продувка",
        ["P0442"] = "EVAP — малая утечка",
        ["P0455"] = "EVAP — большая утечка",
        ["P0500"] = "Датчик скорости — неисправность",
        ["P0505"] = "IAC — неисправность",
        ["P0560"] = "Напряжение системы — неисправность",
        ["P0562"] = "Напряжение системы — низкое",
        ["P0563"] = "Напряжение системы — высокое",
        ["P0600"] = "CAN — ошибка связи",
        ["P0601"] = "ЭБУ — ошибка ROM",
        ["P0606"] = "ЭБУ — внутренняя ошибка",
        ["P0627"] = "Топливный насос — обрыв цепи",
        ["P0630"] = "VIN не запрограммирован / не совпадает",
        ["P0700"] = "АКПП — неисправность системы",
        ["P0715"] = "Датчик оборотов турбины АКПП — неисправность",
        ["P1602"] = "ЭБУ — пропадание питания",
        ["U0001"] = "CAN High Speed — ошибка шины",
        ["U0100"] = "Потеря связи с PCM",
        ["U0121"] = "Потеря связи с ABS",
        ["U0155"] = "Потеря связи с приборной панелью",
    };

    // ══════════════ Поля ══════════════
    private readonly Random _rng = new();
    private readonly object _lock = new();

    private CarProfile _car;
    private bool _engineRunning;
    private readonly EngState _eng = new();

    // Инжектированные ошибки
    private readonly List<InjectedError> _injected = new();

    // Тики для естественного дрейфа
    private int _tick;
    private double _coolantTempTarget;
    private int _dtcCounter;

    // ══════════════ Конструктор ══════════════
    public ObdSimulator(string carKey = "Lada Vesta 1.8")
    {
        _car = Cars.TryGetValue(carKey, out var cp) ? cp : Cars.First().Value;
        _engineRunning = false;
        ResetState();
    }

    /// <summary>Сменить авто по ключу из словаря Cars.</summary>
    public void SetCar(string key)
    {
        if (Cars.TryGetValue(key, out var cp))
        {
            _car = cp;
            ResetState();
        }
    }

    /// <summary>Все доступные ключи авто.</summary>
    public static IEnumerable<string> CarKeys => Cars.Keys;

    /// <summary>Текущий профиль авто.</summary>
    public CarProfile Car => _car;

    /// <summary>Двигатель запущен?</summary>
    public bool IsRunning => _engineRunning;

    // ══════════════ Управление двигателем ══════════════
    /// <summary>Запустить двигатель.</summary>
    public void StartEngine()
    {
        _engineRunning = true;
        _eng.RPM = _car.IdleRPM + _rng.Next(-40, 40);
        _eng.Speed = 0;
        _eng.Load = 20 + _rng.NextDouble() * 5;
        _eng.MAF = _car.Displacement * _eng.RPM / 120 * 1.2 * _rng.NextDouble() * 0.05;
        _coolantTempTarget = 20 + _rng.NextDouble() * 10;
        _eng.CoolantTemp = _coolantTempTarget;
        _eng.IntakeTemp = 15 + _rng.NextDouble() * 10;
        _eng.FuelPressure = 300 + _rng.NextDouble() * 20;
        _eng.BatteryVoltage = 12.0 + _rng.NextDouble() * 0.8;
        _eng.O2B1S1 = 0.45 + _rng.NextDouble() * 0.1;
        _eng.O2B1S2 = 0.65 + _rng.NextDouble() * 0.1;
        _eng.ShortFT1 = _rng.NextDouble() * 2 - 1;
        _eng.LongFT1 = _rng.NextDouble() * 3 - 1.5;
        _eng.IgnitionAdvance = 8 + _rng.NextDouble() * 5;
        _tick = 0;
    }

    /// <summary>Остановить двигатель.</summary>
    public void StopEngine()
    {
        _engineRunning = false;
        _eng.RPM = 0;
        _eng.Speed = 0;
        _eng.Load = 0;
        _eng.MAF = 0;
        _eng.FuelPressure = 0;
        _eng.O2B1S1 = 0;
        _eng.O2B1S2 = 0;
    }

    // ══════════════ Тик симуляции ══════════════
    /// <summary>
    /// Один шаг симуляции (~1 сек). Двигает PID, генерирует ошибки.
    /// </summary>
    public void Tick(double targetSpeed = 0)
    {
        if (!_engineRunning) return;
        _tick++;

        // --- Обороты ---
        if (targetSpeed > 0)
        {
            var targetRPM = Math.Min(_car.MaxRPM, _car.IdleRPM + targetSpeed / _car.MaxSpeed * (_car.MaxRPM - _car.IdleRPM));
            // Сглаживание
            _eng.RPM += (targetRPM - _eng.RPM) * 0.3 + (_rng.NextDouble() - 0.5) * 30;
        }
        else
        {
            // Холостой ход с дрейфом
            _eng.RPM += (_car.IdleRPM - _eng.RPM) * 0.2 + (_rng.NextDouble() - 0.5) * 20;
        }
        _eng.RPM = Math.Clamp(_eng.RPM, _car.IdleRPM * 0.8, _car.MaxRPM * 1.02);

        // --- Скорость ---
        if (targetSpeed > 0)
        {
            _eng.Speed += (targetSpeed - _eng.Speed) * 0.2 + (_rng.NextDouble() - 0.5) * 1.5;
        }
        else
        {
            _eng.Speed = Math.Max(0, _eng.Speed - _rng.NextDouble() * 3);
        }
        _eng.Speed = Math.Clamp(_eng.Speed, 0, _car.MaxSpeed * 1.05);

        // --- Температура ОЖ ---
        _coolantTempTarget += (90 - _coolantTempTarget) * 0.01;
        _eng.CoolantTemp += (_coolantTempTarget - _eng.CoolantTemp) * 0.05 + (_rng.NextDouble() - 0.5) * 0.3;
        _eng.CoolantTemp = Math.Clamp(_eng.CoolantTemp, -5, 115);

        // --- Нагрузка ---
        _eng.Load += (_eng.RPM / _car.MaxRPM * 70 + 15 - _eng.Load) * 0.1 + (_rng.NextDouble() - 0.5) * 2;
        _eng.Load = Math.Clamp(_eng.Load, 15, 100);

        // --- MAF (г/с) ---
        var idealMAF = _car.Displacement * _eng.RPM / 120 * 1.2 * (_eng.Load / 100);
        _eng.MAF += (idealMAF - _eng.MAF) * 0.15 + (_rng.NextDouble() - 0.5) * 0.3;
        _eng.MAF = Math.Max(0.5, _eng.MAF);

        // --- Температура впуска ---
        _eng.IntakeTemp += (_eng.CoolantTemp * 0.4 + 10 - _eng.IntakeTemp) * 0.02 + (_rng.NextDouble() - 0.5) * 0.2;
        _eng.IntakeTemp = Math.Clamp(_eng.IntakeTemp, -10, 80);

        // --- Топливное давление ---
        _eng.FuelPressure += (_car.FuelType == "Дизель" ? 1400 : 300 - _eng.FuelPressure) * 0.05 + (_rng.NextDouble() - 0.5) * 5;
        _eng.FuelPressure = Math.Max(0, _eng.FuelPressure);

        // --- Напряжение ---
        _eng.BatteryVoltage += (13.8 + (_rng.NextDouble() - 0.5) * 0.2 - _eng.BatteryVoltage) * 0.1;
        _eng.BatteryVoltage = Math.Clamp(_eng.BatteryVoltage, 9, 15.5);

        // --- O2 ---
        _eng.O2B1S1 += (_eng.RPM > _car.IdleRPM * 1.3 ? (0.1 + (_rng.NextDouble() - 0.5) * 0.8) : 0.5) - _eng.O2B1S1 * 0.1;
        _eng.O2B1S1 = Math.Clamp(_eng.O2B1S1, 0, 1.2);
        _eng.O2B1S2 += (0.65 - _eng.O2B1S2) * 0.05;
        _eng.O2B1S2 = Math.Clamp(_eng.O2B1S2, 0, 1.2);

        // --- Топливные коррекции ---
        _eng.ShortFT1 += ((_rng.NextDouble() - 0.5) * 0.4 - _eng.ShortFT1 * 0.05);
        _eng.ShortFT1 = Math.Clamp(_eng.ShortFT1, -25, 25);
        _eng.LongFT1 += (_eng.ShortFT1 * 0.01 - _eng.LongFT1 * 0.003);
        _eng.LongFT1 = Math.Clamp(_eng.LongFT1, -25, 25);

        // --- Угол зажигания ---
        _eng.IgnitionAdvance += (8 + _eng.RPM / 800 - _eng.IgnitionAdvance) * 0.1 + (_rng.NextDouble() - 0.5) * 0.5;
        _eng.IgnitionAdvance = Math.Clamp(_eng.IgnitionAdvance, -5, 50);

        // --- Естественная генерация ошибок ---
        TryGenerateNaturalError();
    }

    // ══════════════ Инжект ошибок ══════════════
    /// <summary>Инжект ошибки в симулятор.</summary>
    public void InjectError(string code, ObdErrorType type = ObdErrorType.Current, string? description = null)
    {
        var desc = description ?? (ErrorDB.TryGetValue(code, out var ed) ? ed : "Неизвестная ошибка");
        lock (_lock)
        {
            _injected.Add(new InjectedError
            {
                Code = code,
                Type = type,
                Description = desc,
                Frame = GenerateFreezeFrame(code),
                InjectedAt = _tick,
            });
        }
    }

    /// <summary>Сброс всех инжектированных ошибок.</summary>
    public void ClearInjected() { lock (_lock) _injected.Clear(); }

    // ══════════════ Чтение ошибок (Mode 03/07/0A) ══════════════
    public List<ObdError> GetErrors(ObdErrorType type)
    {
        lock (_lock)
        {
            return _injected
                .Where(e => e.Type == type)
                .Select(e => new ObdError
                {
                    Code = e.Code,
                    Type = e.Type,
                    FreezeFrame = e.Frame,
                })
                .ToList();
        }
    }

    public List<ObdError> GetAllErrors()
    {
        lock (_lock)
        {
            return _injected.Select(e => new ObdError
            {
                Code = e.Code,
                Type = e.Type,
                FreezeFrame = e.Frame,
            }).ToList();
        }
    }

    // ══════════════ Живые данные (PID) ══════════════
    public Dictionary<string, object> GetLiveData()
    {
        return new()
        {
            ["RPM"] = Math.Round(_eng.RPM, 0),
            ["Speed"] = Math.Round(_eng.Speed, 1),
            ["CoolantTemp"] = Math.Round(_eng.CoolantTemp, 1),
            ["IntakeTemp"] = Math.Round(_eng.IntakeTemp, 1),
            ["MAF"] = Math.Round(_eng.MAF, 2),
            ["Load"] = Math.Round(_eng.Load, 1),
            ["ThrottlePos"] = Math.Round(_eng.Load * 0.85, 1),  // ~load → throttle
            ["FuelPressure"] = Math.Round(_eng.FuelPressure, 1),
            ["BatteryVoltage"] = Math.Round(_eng.BatteryVoltage, 2),
            ["O2B1S1"] = Math.Round(_eng.O2B1S1, 3),
            ["O2B1S2"] = Math.Round(_eng.O2B1S2, 3),
            ["ShortFT1"] = Math.Round(_eng.ShortFT1, 2),
            ["LongFT1"] = Math.Round(_eng.LongFT1, 2),
            ["IgnitionAdvance"] = Math.Round(_eng.IgnitionAdvance, 1),
            ["FuelLevel"] = Math.Round(Math.Max(0, _car.FuelTankL - _tick * 0.005), 1),
            ["Runtime_sec"] = _tick,
            ["OBD_standard"] = "EOBD + OBD2",
            ["FuelType"] = _car.FuelType,
            ["AmbientTemp"] = Math.Round(15 + _eng.IntakeTemp * 0.3 + (_rng.NextDouble() - 0.5) * 2, 1),
        };
    }

    // ══════════════ ELM327 AT-команды ══════════════
    public string SendAtCommand(string cmd)
    {
        cmd = cmd.Trim().ToUpperInvariant();
        return cmd switch
        {
            "ATZ" or "ATWS" => "ELM327 v2.3",                   // Сброс
            "ATE0" => "OK",                                     // Эхо выкл
            "ATE1" => "OK",                                     // Эхо вкл
            "ATL0" => "OK",                                     // Переводы строк выкл
            "ATL1" => "OK",                                     // Переводы строк вкл
            "ATH0" => "OK",                                     // Заголовки выкл
            "ATH1" => "OK",                                     // Заголовки вкл
            "ATSP0" => "OK",                                    // Авто-протокол
            "ATSP1" => "OK",
            "ATSP2" => "OK",
            "ATSP3" => "OK",
            "ATDP" => "AUTO, ISO 15765-4 (CAN 11/500)",        // Текущий протокол
            "ATDPN" => "7",                                     // Номер протокола
            "ATRV" => $"{_eng.BatteryVoltage:F1}V",              // Напряжение
            "AT@1" => $"OBD2 Simulator\r\nCar: {_car.Brand} {_car.Model}\r\nEngine: {_car.EngineCode}\r\nECU: {_car.ECU}",
            "ATI" => "ELM327 v2.3 (Simulated)",
            "ATIGN" => _engineRunning ? "ON" : "OFF",
            "ATD" => "OK",                                      // Все значения по умолчанию
            "ATFCSH" or "ATFCSD" or "ATFC" => "OK",
            "ATWM" => "OK",
            "ATAM" => "OK",
            "ATR0" => "OK",
            "ATR1" => "OK",
            "ATS0" => "OK",
            "ATS1" => "OK",
            "ATAR" => "OK",
            "ATV" => "OK",
            "ATTP" => "7",                                      // CAN протокол
            "ATKW" => "OK",
            "ATKW0" => "OK",
            "ATKW1" => "OK",
            "ATPP" => "OK",
            "ATPB" => "OK",
            _ when cmd.StartsWith("ATSH") => "OK",               // Установка заголовка
            _ when cmd.StartsWith("ATCRA") => "OK",              // Фильтр CAN
            _ when cmd.StartsWith("ATCM") => "OK",               // Маска CAN
            _ => "?",
        };
    }

    // ══════════════ Freeze frame ══════════════
    private Dictionary<string, string> GenerateFreezeFrame(string code)
    {
        return new()
        {
            ["RPM"] = $"{_eng.RPM:F0}",
            ["Speed"] = $"{_eng.Speed:F0}",
            ["CoolantTemp"] = $"{_eng.CoolantTemp:F0}",
            ["IntakeTemp"] = $"{_eng.IntakeTemp:F0}",
            ["Load"] = $"{_eng.Load:F0}",
            ["MAF"] = $"{_eng.MAF:F2}",
            ["FuelPressure"] = $"{_eng.FuelPressure:F0}",
            ["BatteryVoltage"] = $"{_eng.BatteryVoltage:F1}",
            ["ShortFT1"] = $"{_eng.ShortFT1:F1}",
            ["LongFT1"] = $"{_eng.LongFT1:F1}",
            ["IgnitionAdvance"] = $"{_eng.IgnitionAdvance:F0}",
            ["Runtime_sec"] = $"{_tick}",
            ["DTC"] = code,
            ["CarBrand"] = _car.Brand,
            ["CarModel"] = _car.Model,
            ["EngineCode"] = _car.EngineCode,
            ["ECU"] = _car.ECU,
        };
    }

    // ══════════════ Естественная генерация ошибок ══════════════
    private void TryGenerateNaturalError()
    {
        // Каждые 60–120 тиков — шанс естественной ошибки
        _dtcCounter++;
        if (_dtcCounter < 60 + _rng.Next(0, 120)) return;
        _dtcCounter = 0;

        // Выбираем правдоподобную ошибку под текущее состояние
        var candidates = new List<string>();
        if (_eng.CoolantTemp > 110) candidates.AddRange(new[] { "P0117", "P0118", "P0115" });
        if (_eng.ShortFT1 > 20) candidates.Add("P0171");
        if (_eng.ShortFT1 < -20) candidates.Add("P0172");
        if (_tick > 600 && _eng.RPM / _car.MaxRPM > 0.8) candidates.Add("P0300");
        if (_eng.CoolantTemp > 100) candidates.Add("P0401");
        if (_tick > 1200) candidates.AddRange(new[] { "P0420", "P0430", "P0440", "P0455" });
        if (_eng.BatteryVoltage < 10.5) candidates.Add("P0562");
        if (_rng.NextDouble() < 0.3) candidates.AddRange(new[] { "P0130", "P0170", "P0300", "P0325" });

        if (candidates.Count == 0) return;

        var code = candidates[_rng.Next(candidates.Count)];
        var desc = ErrorDB.TryGetValue(code, out var ed) ? ed : "—";
        var type = _rng.NextDouble() < 0.6 ? ObdErrorType.Pending : ObdErrorType.Current;

        lock (_lock)
        {
            // Не дублируем уже инжектированный код того же типа
            if (_injected.Any(e => e.Code == code && e.Type == type)) return;

            _injected.Add(new InjectedError
            {
                Code = code,
                Type = type,
                Description = desc,
                Frame = GenerateFreezeFrame(code),
                InjectedAt = _tick,
                IsNatural = true,
            });
        }
    }

    private void ResetState()
    {
        _eng.RPM = 0;
        _eng.Speed = 0;
        _eng.Load = 0;
        _eng.MAF = 0;
        _eng.CoolantTemp = 15;
        _eng.IntakeTemp = 15;
        _eng.FuelPressure = 0;
        _eng.BatteryVoltage = 12.5;
        _tick = 0;
        _coolantTempTarget = 20;
        _dtcCounter = 0;
        lock (_lock) _injected.Clear();
        // Демо-DTC после сброса — симулятор не должен вечно отдавать «ошибок нет»
        InjectError("P0134", ObdErrorType.Current, "O2 sensor no activity");
        InjectError("P0301", ObdErrorType.Current, "Cylinder 1 misfire");
        InjectError("P0171", ObdErrorType.Pending, "System too lean");
        InjectError("P0420", ObdErrorType.Permanent, "Catalyst efficiency");
    }

    // ══════════════ Внутренние типы ══════════════
    private class EngState
    {
        public double RPM, Speed, Load, MAF, CoolantTemp, IntakeTemp,
            FuelPressure, BatteryVoltage, O2B1S1, O2B1S2,
            ShortFT1, LongFT1, IgnitionAdvance;
    }

    private class InjectedError
    {
        public string Code = "", Description = "";
        public ObdErrorType Type;
        public Dictionary<string, string> Frame = new();
        public int InjectedAt;
        public bool IsNatural;
    }
}

// ══════════════ Профиль автомобиля ══════════════
public class CarProfile
{
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public double Displacement { get; set; }
    public int MaxRPM { get; set; } = 6000;
    public int IdleRPM { get; set; } = 800;
    public int MaxSpeed { get; set; } = 180;
    public double FuelTankL { get; set; } = 50;
    public string ECU { get; set; } = "M86";
    public string EngineCode { get; set; } = "";
    public string FuelType { get; set; } = "Бензин";

    public override string ToString() => $"{Brand} {Model} ({EngineCode}, {Displacement}L, {FuelType})";
}
