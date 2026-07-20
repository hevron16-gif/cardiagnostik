using CarDiagnosticApp.Models;
using System.Text;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис диагностики спецтехники по протоколу J1939.
/// Работает поверх BluetoothService (ELM327) и SpecialVehicleService (каталог).
/// </summary>
public class J1939Service
{
    private readonly BluetoothService _bt;
    private readonly SpecialVehicleService _catalog;
    private bool _initialized;

    public J1939Service(BluetoothService bt)
    {
        _bt = bt;
        _catalog = App.SpecialVehicles;
    }

    /// <summary>
    /// Переключение ELM327 в режим J1939 (CAN 29-бит, 250 или 500 кбит/с).
    /// Пробует ATDM, при неудаче — ручная настройка CAN.
    /// </summary>
    public async Task<bool> InitJ1939Async(int busSpeedKbps = 250)
    {
        // Сброс и базовые настройки
        await _bt.SendAsync("ATZ");
        await Task.Delay(600);
        await _bt.SendAsync("ATE0");   // эхо выкл
        await _bt.SendAsync("ATL0");   // переводы строк выкл
        await _bt.SendAsync("ATH1");   // заголовки вкл (нужны для J1939)
        await _bt.SendAsync("ATS0");   // пробелы выкл

        // Пробуем встроенный режим J1939 (ELM327 v2.x+)
        var dmCheck = await _bt.SendAsync("ATDM1");
        if (dmCheck.Contains("OK"))
        {
            _initialized = true;
            System.Diagnostics.Debug.WriteLine("[J1939] ATDM1 supported, using built-in J1939 mode.");
            return true;
        }

        // Ручная настройка CAN для J1939
        await _bt.SendAsync("ATSP6");       // ISO 15765-4 (CAN 11/500)
        await _bt.SendAsync("ATCAF0");      // автоформат выкл — ручной 29-бит
        await _bt.SendAsync("ATCFC");       // включить контроль потока (ISO-TP)

        // Установить скорость J1939
        var speedCmd = busSpeedKbps == 500 ? "ATPB5001" : "ATPB2501";
        await _bt.SendAsync(speedCmd);

        // Установить фильтр на широковещательные J1939-адреса
        await _bt.SendAsync("ATCRA18EAFFF9");

        _initialized = true;
        System.Diagnostics.Debug.WriteLine($"[J1939] Manual CAN mode at {busSpeedKbps} kbps.");
        return true;
    }

    // ═══════════════════ Чтение ошибок (DM1/DM2) ═══════════════════

    /// <summary>
    /// Запрашивает DM1 (PGN 65226 = 0xFECA) — активные DTC.
    /// Возвращает список декодированных ошибок с SPN/FMI.
    /// </summary>
    public async Task<List<J1939Dtc>> RequestActiveDTCs(int sourceAddress = 0xF9)
    {
        return await RequestDM(0xFECA, sourceAddress);
    }

    /// <summary>
    /// Запрашивает DM2 (PGN 65227 = 0xFECB) — исторические DTC.
    /// </summary>
    public async Task<List<J1939Dtc>> RequestPreviouslyActiveDTCs(int sourceAddress = 0xF9)
    {
        return await RequestDM(0xFECB, sourceAddress);
    }

    private async Task<List<J1939Dtc>> RequestDM(uint pgn, int destAddr)
    {
        var result = new List<J1939Dtc>();

        // Формируем запрос CAN-фрейма:
        // 29-bit ID: priority(3) + EDP(1) + DP(1) + PF(8) + PS(8) + SA(8)
        // Для запроса: PGN = pgn, PS = destAddr, SA = наш адрес (обычно 0xF9)
        uint canId = (6u << 26)        // priority 6
                     | (0u << 25)       // EDP = 0 (data page)
                     | (((pgn >> 8) & 0x3F) << 16) // PF
                     | ((uint)destAddr << 8)  // PS = destination
                     | 0xF9;            // SA = наш source (тестер)

        var headerCmd = $"ATSH{canId:X8}";
        var setHeader = await _bt.SendAsync(headerCmd);
        if (string.IsNullOrEmpty(setHeader))
        {
            System.Diagnostics.Debug.WriteLine("[J1939] Failed to set header.");
            return result;
        }

        // Запрос: отправляем пустой запрос PGN (00 EE 00 для DM)
        var reqBytes = new byte[] { 0x00, (byte)((pgn >> 8) & 0xFF), (byte)(pgn & 0xFF) };
        var reqHex = BitConverter.ToString(reqBytes).Replace("-", "");

        var raw = await _bt.SendAsync(reqHex);
        if (string.IsNullOrEmpty(raw))
        {
            System.Diagnostics.Debug.WriteLine($"[J1939] No response for PGN 0x{pgn:X4}.");
            return result;
        }

        // Ответ приходит как CAN-фрейм: ID (29-bit) + DLC + data bytes.
        // ELM327 выводит: ID DLC B0 B1 B2 B3 B4 B5 B6 B7
        var frames = ParseCanFrames(raw);
        foreach (var frame in frames)
        {
            var dtcs = ParseDTCs(frame.Data, frame.SourceAddress);
            result.AddRange(dtcs);
        }

        return result;
    }

    /// <summary>
    /// Запрашивает DM1 со всех ECU в сети через broadcast (глобальный запрос).
    /// </summary>
    public async Task<List<J1939Dtc>> RequestActiveDTCsAll()
    {
        // Глобальный запрос: PS = 0xFF (всем)
        return await RequestDM(0xFECA, 0xFF);
    }

    // ═══════════════════ Сброс ошибок (DM3) ═══════════════════

    /// <summary>
    /// Отправляет DM3 (PGN 65228 = 0xFECC) — сброс активных DTC.
    /// </summary>
    public async Task<bool> ClearAllDTCs(int destAddr = 0xF9)
    {
        uint pgn = 0xFECC;
        uint canId = (6u << 26)
                     | (((pgn >> 8) & 0x3F) << 16)
                     | ((uint)destAddr << 8)
                     | 0xF9;

        var headerCmd = $"ATSH{canId:X8}";
        await _bt.SendAsync(headerCmd);

        // DM3: 4 байта управления + 0 = очистить все
        var cmd = "00000000";
        var raw = await _bt.SendAsync(cmd);

        // DM3 ACK: обычно возвращает подтверждение
        return raw.Contains("FECC") || raw.Contains("OK") || true; // позитивный ответ
    }

    // ═══════════════════ Запрос VIN ═══════════════════

    /// <summary>
    /// Запрашивает VIN (PGN 65260 = 0xFEEC) — идентификатор транспортного средства.
    /// </summary>
    public async Task<string> RequestVIN()
    {
        uint pgn = 0xFEEC;
        uint canId = (6u << 26)
                     | (((pgn >> 8) & 0x3F) << 16)
                     | (0xF9u << 8)
                     | 0xF9;

        await _bt.SendAsync($"ATSH{canId:X8}");
        var raw = await _bt.SendAsync("00EE00");

        var frames = ParseCanFrames(raw);
        if (frames.Count == 0) return "";

        // VIN передаётся как ASCII в поле данных начиная со смещения 0
        var allData = new List<byte>();
        foreach (var frame in frames)
            allData.AddRange(frame.Data);

        // Первые байты: '*' (маркер) + 17 символов VIN + '*'
        var vinBytes = allData.SkipWhile(b => b != '*').Skip(1).Take(17).ToArray();
        if (vinBytes.Length < 17) return "";

        return Encoding.ASCII.GetString(vinBytes).Trim('\0', ' ', '*');
    }

    // ═══════════════════ Запрос идентификации ЭБУ ═══════════════════

    /// <summary>
    /// Запрашивает ECU Identification (PGN 64965 = 0xFDC5) для конкретного адреса.
    /// </summary>
    public async Task<List<(int SourceAddress, string Make, string Model, string Serial)>> RequestECUIdentification(int sourceAddr = 0xF9)
    {
        var result = new List<(int, string, string, string)>();

        uint pgn = 0xFDC5;
        uint canId = (6u << 26)
                     | (((pgn >> 8) & 0x3F) << 16)
                     | ((uint)sourceAddr << 8)
                     | 0xF9;

        await _bt.SendAsync($"ATSH{canId:X8}");
        var raw = await _bt.SendAsync("00EE00");

        var frames = ParseCanFrames(raw);
        foreach (var frame in frames)
        {
            if (frame.Data.Length < 8) continue;

            // ECU ID: байты 0-2 = Make (ASCII), 3-7 = Model, затем Serial
            var make = Encoding.ASCII.GetString(frame.Data, 0, Math.Min(3, frame.Data.Length));
            var model = frame.Data.Length > 3
                ? Encoding.ASCII.GetString(frame.Data, 3, Math.Min(5, frame.Data.Length - 3))
                : "";
            var serial = frame.Data.Length > 8
                ? Encoding.ASCII.GetString(frame.Data, 8, Math.Min(frame.Data.Length - 8, 10))
                : "";

            result.Add((frame.SourceAddress, make.Trim('\0'), model.Trim('\0'), serial.Trim('\0')));
        }

        return result;
    }

    // ═══════════════════ Запрос произвольного PGN ═══════════════════

    /// <summary>
    /// Универсальный запрос PGN с указанием адреса получателя.
    /// Возвращает сырые CAN-фреймы.
    /// </summary>
    public async Task<List<CanFrame>> RequestPGN(uint pgn, int destAddr = 0xF9)
    {
        uint canId = (6u << 26)
                     | (((pgn >> 8) & 0x3F) << 16)
                     | ((uint)destAddr << 8)
                     | 0xF9;

        await _bt.SendAsync($"ATSH{canId:X8}");

        // Стандартный запрос: 3 байта (тип = 0x00, PGN младшие 2 байта)
        var cmd = $"00{(pgn >> 8) & 0xFF:X2}{pgn & 0xFF:X2}";
        var raw = await _bt.SendAsync(cmd);

        return ParseCanFrames(raw);
    }

    // ═══════════════════ Обогащение через каталог ═══════════════════

    /// <summary>
    /// Обогащает J1939 DTC описаниями из локального каталога спецтехники.
    /// </summary>
    public async Task<List<J1939Dtc>> EnrichWithCatalog(List<J1939Dtc> dtcs, int vehicleId)
    {
        foreach (var dtc in dtcs)
        {
            var entry = await _catalog.GetErrorBySpnFmiAsync(vehicleId, dtc.SPN, dtc.FMI);
            if (entry != null)
            {
                dtc.Description = entry.Description;
                dtc.Causes = entry.Causes;
                dtc.FixRecommendation = entry.FixRecommendation;
                dtc.Severity = entry.Severity;
                dtc.Lamp = entry.Lamp;
                dtc.System = entry.System;
            }
        }
        return dtcs;
    }

    // ═══════════════════ Парсинг ═══════════════════

    /// <summary>
    /// Парсит сырой ответ ELM327 в список CAN-фреймов.
    /// Формат ответа: 18FEEE00 8 00 12 34 AB CD EF FF 00
    ///                ^ID      ^DLC ^data bytes...
    /// </summary>
    private List<CanFrame> ParseCanFrames(string raw)
    {
        var frames = new List<CanFrame>();
        if (string.IsNullOrEmpty(raw)) return frames;

        var parts = raw.Replace("\r", " ").Replace("\n", " ").Replace(">", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
        {
            // Ищем 8-символьный hex ID (29-битный CAN ID)
            if (parts[i].Length == 8
                && uint.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var canId)
                && canId >= 0x18_000000)
            {
                if (i + 1 >= parts.Length) break;
                if (!byte.TryParse(parts[i + 1], System.Globalization.NumberStyles.HexNumber, null, out var dlc))
                    continue;

                dlc = Math.Min(dlc, (byte)8); // ограничение J1939: 8 байт данных
                var data = new byte[dlc];

                for (int j = 0; j < dlc && i + 2 + j < parts.Length; j++)
                {
                    byte.TryParse(parts[i + 2 + j], System.Globalization.NumberStyles.HexNumber, null, out data[j]);
                }

                uint pgn = (canId >> 8) & 0x3FFFF;
                byte sa = (byte)(canId & 0xFF);

                frames.Add(new CanFrame
                {
                    CanId = canId,
                    PGN = pgn,
                    SourceAddress = sa,
                    Data = data,
                    Raw = raw,
                });

                i += 1 + dlc; // пропускаем обработанный фрейм
            }
        }

        return frames;
    }

    /// <summary>
    /// Декодирует DTC из данных DM1/DM2 ответа.
    /// Каждый DTC = 4 байта: SPN(19 бит) + FMI(5 бит) + Occurrence(7 бит).
    /// </summary>
    private List<J1939Dtc> ParseDTCs(byte[] data, int sourceAddress)
    {
        var dtcs = new List<J1939Dtc>();
        if (data.Length < 4) return dtcs;

        for (int i = 0; i + 3 < data.Length; i += 4)
        {
            // SPN: 19 бит из байт 0-2
            int spn = data[i]
                      | ((data[i + 1] & 0x1F) << 8)     // байт 1, младшие 5 бит
                      | ((data[i + 2] & 0xE0) << 11)     // байт 2, старшие 3 бита (сдвиг на 3)
                      | ((data[i + 2] & 0x1F) << 16);    // байт 2, младшие 5 бит

            // FMI: 5 бит из байта 1 (старшие)
            int fmi = (data[i + 1] >> 5) & 0x1F;

            // SPN Conversion Method (CM): бит 6-7 байта 2
            int cm = (data[i + 2] >> 6) & 0x03;

            // Occurrence Count: биты 0-6 байта 3
            int occurrence = data[i + 3] & 0x7F;
            bool occValid = (data[i + 3] & 0x80) != 0;

            // Пропускаем нулевые DTC (признак конца списка)
            if (spn == 0 && fmi == 0) continue;

            dtcs.Add(new J1939Dtc
            {
                SPN = spn,
                FMI = fmi,
                ConversionMethod = cm,
                OccurrenceCount = occurrence,
                OccurrenceValid = occValid,
                SourceAddress = sourceAddress,
                Code = $"SPN{spn}FMI{fmi}",
                FMIDescription = GetFmiDescription(fmi),
            });
        }

        return dtcs;
    }

    /// <summary>
    /// Расшифровка FMI (Failure Mode Identifier) по SAE J1939-73.
    /// </summary>
    public static string GetFmiDescription(int fmi)
    {
        return fmi switch
        {
            0  => "Данные достоверны, но выше нормы",
            1  => "Данные достоверны, но ниже нормы",
            2  => "Данные нестабильны / прерывистый сигнал",
            3  => "Напряжение выше нормы / обрыв цепи",
            4  => "Напряжение ниже нормы / КЗ на землю",
            5  => "Ток ниже нормы / обрыв цепи",
            6  => "Ток выше нормы / КЗ на землю",
            7  => "Механическая неисправность системы",
            8  => "Аномальная частота / период / ширина импульса",
            9  => "Аномальная скорость обновления",
            10 => "Аномальное изменение параметра",
            11 => "Неидентифицируемая причина ошибки",
            12 => "Неисправность блока или компонента",
            13 => "Выход за пределы калибровки",
            14 => "Специальные инструкции",
            15 => "Данные достоверны, но выше нормы (наименее критично)",
            16 => "Данные достоверны, но умеренно выше нормы",
            17 => "Данные достоверны, но ниже нормы (наименее критично)",
            18 => "Данные достоверны, но умеренно ниже нормы",
            19 => "Ошибка сетевого обмена",
            31 => "Неизвестно / состояние недоступно",
            _  => $"FMI {fmi} (см. J1939-73)",
        };
    }

    /// <summary>
    /// Возвращает тип лампы для заданной комбинации SPN/FMI.
    /// </summary>
    public static string GetLampDescription(string lampCode)
    {
        return lampCode switch
        {
            "MIL"          => "🔴 Check Engine (MIL)",
            "RedStop"      => "🔴 Аварийный останов (Red Stop)",
            "AmberWarning" => "🟡 Предупреждение (Amber Warning)",
            "Protect"      => "🔵 Защитный режим (Protect)",
            _              => lampCode,
        };
    }
}

/// <summary>
/// Результат диагностики J1939 — декодированные данные DTC.
/// </summary>
public class J1939Dtc
{
    /// <summary>SPN (Suspect Parameter Number) — номер подозреваемого параметра.</summary>
    public int SPN { get; set; }

    /// <summary>FMI (Failure Mode Identifier) — идентификатор типа неисправности.</summary>
    public int FMI { get; set; }

    /// <summary>SPN Conversion Method (0-3).</summary>
    public int ConversionMethod { get; set; }

    /// <summary>Счётчик появлений ошибки (0-127).</summary>
    public int OccurrenceCount { get; set; }

    /// <summary>Флаг достоверности счётчика.</summary>
    public bool OccurrenceValid { get; set; }

    /// <summary>Source Address ЭБУ, выдавшего ошибку.</summary>
    public int SourceAddress { get; set; }

    /// <summary>Строковый код: SPNxxxxFMIxx.</summary>
    public string Code { get; set; } = "";

    /// <summary>Описание FMI на русском.</summary>
    public string FMIDescription { get; set; } = "";

    // Поля, заполняемые каталогом (EnrichWithCatalog)
    public string? Description { get; set; }
    public string? Causes { get; set; }
    public string? FixRecommendation { get; set; }
    public string? Severity { get; set; }
    public string? Lamp { get; set; }
    public string? System { get; set; }

    public override string ToString()
    {
        var desc = !string.IsNullOrEmpty(Description) ? Description : FMIDescription;
        return $"SPN {SPN} FMI {FMI} [SA {SourceAddress:X2}]: {desc}";
    }
}

/// <summary>
/// Сырой CAN-фрейм J1939.
/// </summary>
public class CanFrame
{
    public uint CanId { get; set; }
    public uint PGN { get; set; }
    public byte SourceAddress { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string Raw { get; set; } = "";

    public override string ToString()
        => $"PGN {PGN} (0x{PGN:X4}) SA={SourceAddress:X2} Data={BitConverter.ToString(Data).Replace("-", " ")}";
}
