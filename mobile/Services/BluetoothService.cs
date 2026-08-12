using System.Collections.ObjectModel;
using System.Text;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис Bluetooth-связи с ELM327.
/// Работает через IBluetoothTransport (классический Bluetooth RFCOMM/SPP).
/// </summary>
public class BluetoothService
{

    private static BluetoothService? _instance;
public static BluetoothService Instance => 
    _instance ??= IPlatformApplication.Current!.Services.GetRequiredService<BluetoothService>();
    private readonly IBluetoothTransport _transport;
    private readonly TimeSpan _cmdDelay = TimeSpan.FromMilliseconds(250);

    public bool IsConnected => _transport.IsConnected;

    public BluetoothService(IBluetoothTransport transport)
    {
        _transport = transport;
    }

    // ─── Подключение ────────────────────────────────────────────

    /// <summary>
    /// Поиск ELM327-устройства и подключение.
    /// Возвращает имя устройства.
    /// </summary>
    public async Task<string> ConnectAsync(int scanTimeoutMs = 5000)
    {
        var name = await _transport.ConnectAsync(scanTimeoutMs);

        // Инициализация ELM327
        await SendAsync("ATZ");
        await Task.Delay(500);
        await SendAsync("ATE0");
        await Task.Delay(_cmdDelay);
        await SendAsync("ATL0");
        await Task.Delay(_cmdDelay);
        await SendAsync("ATH1");
        await Task.Delay(_cmdDelay);
        await SendAsync("ATSP0");
        await Task.Delay(500);

        return name;
    }

    /// <summary>
    /// Отключение от устройства.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _transport.DisconnectAsync();
    }

    // ─── VIN и идентификация ──────────────────────────────────────

    /// <summary>
    /// Читает VIN автомобиля (Mode 09 PID 02).
    /// Поддерживает single-frame и multi-frame (ISO-TP) ответы ELM327.
    /// </summary>
    public async Task<string> ReadVINAsync()
    {
        var raw = await SendAsync("0902");
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("NO DATA", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("UNABLE", StringComparison.OrdinalIgnoreCase))
        {
            raw = await SendAsync("09 02");
        }

        var vin = ParseVinFromResponse(raw);
        if (VinDecoderService.IsPlausibleVin(vin))
            return VinDecoderService.NormalizeVin(vin);

        await Task.Delay(400);
        raw = await SendAsync("0902");
        vin = ParseVinFromResponse(raw);
        return VinDecoderService.IsPlausibleVin(vin)
            ? VinDecoderService.NormalizeVin(vin)
            : "";
    }

    /// <summary>
    /// Читает CALID (калибровочный ID, Mode 09 PID 04).
    /// </summary>
    public async Task<string> ReadCalibrationIdAsync()
    {
        var raw = await SendAsync("0904");
        if (string.IsNullOrWhiteSpace(raw)) return "";
        return ParseAsciiFromMode09(raw, "04");
    }

    /// <summary>
    /// Парсит VIN из сырого ответа ELM327 (hex-байты ASCII).
    /// </summary>
    public static string ParseVinFromResponse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var cleaned = raw
            .Replace("SEARCHING...", "", StringComparison.OrdinalIgnoreCase)
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Replace(">", " ")
            .Replace(":", " ")
            .Replace("-", " ")
            .Replace("\t", " ");

        var asciiDirect = System.Text.RegularExpressions.Regex.Match(
            cleaned.ToUpperInvariant(), @"\b([A-HJ-NPR-Z0-9]{17})\b");
        if (asciiDirect.Success && VinDecoderService.IsPlausibleVin(asciiDirect.Groups[1].Value))
            return asciiDirect.Groups[1].Value;

        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hexBytes = new List<byte>();

        bool seen4902 = false;
        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim().ToUpperInvariant();
            if (p.Length == 1 && char.IsDigit(p[0])) continue;
            if (p is "49" or "02") { seen4902 = true; continue; }
            if (seen4902 && p.Length == 2 && IsHexByte(p))
            {
                hexBytes.Add(byte.Parse(p, System.Globalization.NumberStyles.HexNumber));
            }
        }

        var vin = ExtractVinFromBytes(hexBytes);
        if (!string.IsNullOrEmpty(vin)) return vin;

        // Fallback
        hexBytes.Clear();
        bool inVin = false;
        foreach (var part in parts)
        {
            var p = part.Trim().ToUpperInvariant();
            if (p == "01" && !inVin) { inVin = true; continue; }
            if (inVin && p.Length == 2 && IsHexByte(p))
            {
                hexBytes.Add(byte.Parse(p, System.Globalization.NumberStyles.HexNumber));
                if (hexBytes.Count >= 17)
                {
                    vin = Encoding.ASCII.GetString(hexBytes.Take(17).ToArray());
                    if (VinDecoderService.IsPlausibleVin(vin)) return VinDecoderService.NormalizeVin(vin);
                    break;
                }
            }
        }

        return "";
    }

    private static string ExtractVinFromBytes(List<byte> bytes)
    {
        if (bytes.Count < 17) return "";

        for (int start = 0; start <= bytes.Count - 17; start++)
        {
            var slice = bytes.Skip(start).Take(17).ToArray();
            if (slice.All(b => b is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A))
            {
                var s = Encoding.ASCII.GetString(slice).ToUpperInvariant();
                if (VinDecoderService.IsPlausibleVin(s))
                    return VinDecoderService.NormalizeVin(s);
            }
        }

        if (bytes.Count >= 18)
        {
            var slice = bytes.Skip(1).Take(17).ToArray();
            var s = Encoding.ASCII.GetString(slice).ToUpperInvariant();
            if (VinDecoderService.IsPlausibleVin(s))
                return VinDecoderService.NormalizeVin(s);
        }

        return "";
    }

    private static string ParseAsciiFromMode09(string raw, string pid)
    {
        var cleaned = raw.Replace("\r", " ").Replace("\n", " ").Replace(">", " ");
        var parts = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = new List<byte>();
        bool capture = false;
        foreach (var part in parts)
        {
            var p = part.Trim().ToUpperInvariant();
            if (p == "49") { capture = true; continue; }
            if (capture && (p == pid || p.Length == 1)) continue;
            if (capture && p.Length == 2 && IsHexByte(p))
            {
                var b = byte.Parse(p, System.Globalization.NumberStyles.HexNumber);
                if (b is >= 32 and <= 126) bytes.Add(b);
            }
        }
        var s = Encoding.ASCII.GetString(bytes.ToArray()).Trim().Trim('\0');
        return s.Length >= 3 ? s : "";
    }

    // ─── Чтение ошибок ──────────────────────────────────────────

    public async Task<bool> ClearDTCsAsync()
    {
        var raw = await SendAsync("04");
        return raw.Contains("44");
    }

    public async Task<List<ObdError>> ReadAllDTC()
    {
        var all = new List<ObdError>();

        var currentRaw = await SendAsync("03");
        all.AddRange(ParseDTCResponse(currentRaw, "43", ObdErrorType.Current));

        var pendingRaw = await SendAsync("07");
        all.AddRange(ParseDTCResponse(pendingRaw, "47", ObdErrorType.Pending));

        var permanentRaw = await SendAsync("0A");
        all.AddRange(ParseDTCResponse(permanentRaw, "4A", ObdErrorType.Permanent));

        return all;
    }

    public async Task<Dictionary<string, string>> ReadFreezeFrameAsync(string code)
    {
        var result = new Dictionary<string, string>();

        var hexPart = code.StartsWith("P") ? code[1..] : code;
        if (!int.TryParse(hexPart, System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return result;

        var dtcHigh = (hex >> 8) & 0xFF;
        var dtcLow = hex & 0xFF;
        var cmd = $"02 {dtcHigh:X2} {dtcLow:X2}";

        var raw = await SendAsync(cmd);
        if (string.IsNullOrEmpty(raw)) return result;

        var parts = raw.Replace("\r", "").Replace("\n", "").Replace(">", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 3; i < parts.Length - 1; i += 2)
        {
            if (parts[i].Length == 2 && parts[i + 1].Length == 2)
            {
                var pid = ParsePidName(parts[i]);
                if (!string.IsNullOrEmpty(pid))
                    result[pid] = parts[i + 1];
            }
        }

        return result;
    }

    // ─── Живые данные (Mode 01) ─────────────────────────────────

    public async Task<HashSet<int>> ReadPidSupportAsync()
    {
        var supported = new HashSet<int>();

        var mask00 = await ReadPidBitmaskAsync("00");
        if (mask00.HasValue)
            for (int i = 0; i < 32; i++)
                if ((mask00.Value & (1u << (31 - i))) != 0)
                    supported.Add(i + 1);

        if (supported.Contains(0x20))
        {
            var mask20 = await ReadPidBitmaskAsync("20");
            if (mask20.HasValue)
                for (int i = 0; i < 32; i++)
                    if ((mask20.Value & (1u << (31 - i))) != 0)
                        supported.Add(0x20 + i + 1);
        }

        if (supported.Contains(0x40))
        {
            var mask40 = await ReadPidBitmaskAsync("40");
            if (mask40.HasValue)
                for (int i = 0; i < 32; i++)
                    if ((mask40.Value & (1u << (31 - i))) != 0)
                        supported.Add(0x40 + i + 1);
        }

        if (supported.Contains(0x60))
        {
            var mask60 = await ReadPidBitmaskAsync("60");
            if (mask60.HasValue)
                for (int i = 0; i < 32; i++)
                    if ((mask60.Value & (1u << (31 - i))) != 0)
                        supported.Add(0x60 + i + 1);
        }

        return supported;
    }

    private async Task<uint?> ReadPidBitmaskAsync(string pidHex)
    {
        var hex = pidHex.Replace("0x", "").Trim();
        var raw = await SendAsync($"01 {hex}");
        if (string.IsNullOrEmpty(raw)) return null;

        var parts = raw.Replace("\r", "").Replace("\n", "").Replace(">", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var bytes = new List<int>();
        bool inResponse = false;
        foreach (var part in parts)
        {
            if (part == "41") { inResponse = true; continue; }
            if (inResponse && part == hex) continue;
            if (inResponse && part.Length == 2 && IsHexByte(part))
            {
                bytes.Add(int.Parse(part, System.Globalization.NumberStyles.HexNumber));
                if (bytes.Count >= 4) break;
            }
        }

        if (bytes.Count < 4)
        {
            bytes.Clear();
            for (int i = parts.Length - 1; i >= 0 && bytes.Count < 4; i--)
                if (parts[i].Length == 2 && IsHexByte(parts[i]))
                    bytes.Insert(0, int.Parse(parts[i], System.Globalization.NumberStyles.HexNumber));
        }

        if (bytes.Count == 4)
            return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);

        return null;
    }

    /// <summary>
    /// Готовность мониторов OBD-II (Mode 01 PID 01):
    /// лампа MIL, число ошибок в памяти, статус мониторов (бензин/дизель).
    /// </summary>
    public async Task<ReadinessStatus?> ReadReadinessAsync()
    {
        var mask = await ReadPidBitmaskAsync("01");
        if (!mask.HasValue) return null;

        var a = (byte)(mask.Value >> 24);
        var b = (byte)(mask.Value >> 16);
        var c = (byte)(mask.Value >> 8);
        var d = (byte)(mask.Value & 0xFF);

        var status = new ReadinessStatus
        {
            MilOn = (a & 0x80) != 0,
            DtcCount = a & 0x7F,
            IsDiesel = (b & 0x08) != 0,
        };

        if (!status.IsDiesel)
        {
            // Бензин: общие мониторы из байта B (биты готовности: 1 = НЕ завершён)
            (string Name, int Sup, int Rdy)[] common =
            {
                ("Пропуски зажигания", 0, 4),
                ("Топливная система", 1, 5),
                ("Компоненты", 2, 6),
            };
            foreach (var (name, sup, rdy) in common)
            {
                bool supported = (b & (1 << sup)) != 0;
                status.Monitors.Add(new ReadinessMonitor
                {
                    Name = name,
                    Supported = supported,
                    Complete = supported && (b & (1 << rdy)) == 0,
                });
            }
        }

        // Таблица мониторов C/D (бит в C = поддерживается, бит в D = НЕ завершён)
        string[] names = status.IsDiesel
            ? new[] { "Катализатор NMHC", "Нейтрализатор NOx/SCR", "Давление наддува", "", "Датчик выхлопных газов", "Сажевый фильтр", "EGR/VVT", "" }
            : new[] { "Катализатор", "Катализатор с подогревом", "Улавливание паров (EVAP)", "Вторичный воздух", "Хладагент A/C", "Кислородный датчик", "Подогрев O2", "EGR/VVT" };

        for (int i = 0; i < 8; i++)
        {
            if (string.IsNullOrEmpty(names[i])) continue;
            if ((c & (1 << i)) == 0) continue; // неподдерживаемые не показываем
            status.Monitors.Add(new ReadinessMonitor
            {
                Name = names[i],
                Supported = true,
                Complete = (d & (1 << i)) == 0,
            });
        }

        return status;
    }

    public async Task<int[]> ReadPidRawAsync(string pidHex)
    {
        var hex = pidHex.Replace("0x", "").Trim();
        var raw = await SendAsync($"01 {hex}");
        if (string.IsNullOrEmpty(raw)) return Array.Empty<int>();

        var parts = raw.Replace("\r", "").Replace("\n", "").Replace(">", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var bytes = new List<int>();
        bool inResponse = false;
        foreach (var part in parts)
        {
            if (part == "41") { inResponse = true; continue; }
            if (inResponse && part == hex) continue;
            if (inResponse && part.Length == 2 && IsHexByte(part))
                bytes.Add(int.Parse(part, System.Globalization.NumberStyles.HexNumber));
        }

        if (bytes.Count == 0 && parts.Length >= 4)
            for (int i = Math.Max(0, parts.Length - 4); i < parts.Length; i++)
                if (parts[i].Length == 2 && IsHexByte(parts[i]))
                    bytes.Add(int.Parse(parts[i], System.Globalization.NumberStyles.HexNumber));

        return bytes.ToArray();
    }

    public async Task<double> ReadPidValueAsync(LiveDataPid pid)
    {
        var bytes = await ReadPidRawAsync(pid.PidHex);
        if (bytes.Length == 0) return double.NaN;

        var a = bytes.Length > 0 ? bytes[0] : 0;
        var b = bytes.Length > 1 ? bytes[1] : 0;
        var c = bytes.Length > 2 ? bytes[2] : 0;
        var d = bytes.Length > 3 ? bytes[3] : 0;

        var value = pid.Compute(a, b, c, d);
        pid.IsSupported = true;
        return value;
    }

    internal async Task<string> SendAsync(string command)
    {
        if (!_transport.IsConnected) return "";

        try
        {
            var cmdBytes = Encoding.UTF8.GetBytes(command + "\r");
            var text = await _transport.SendAsync(cmdBytes);

            var allLines = new List<string>();
            foreach (var line in text.Split('\r', StringSplitOptions.RemoveEmptyEntries))
            {
                var clean = line.Trim();
                if (clean == ">" || clean == "SEARCHING..." ||
                    clean.Contains("UNABLE", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("NO DATA", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(clean))
                    allLines.Add(clean);
            }

            return string.Join(" ", allLines);
        }
        catch
        {
            return "";
        }
    }

    private static List<ObdError> ParseDTCResponse(string raw, string modePrefix, ObdErrorType type)
    {
        var codes = new List<ObdError>();
        if (string.IsNullOrEmpty(raw)) return codes;
        if (raw.Contains("NO DATA", StringComparison.OrdinalIgnoreCase)) return codes;

        // Нормализуем: убираем заголовки CAN (7Ex), разбиваем слитный hex на байты
        var clean = raw.Replace("\r", " ").Replace("\n", " ").Replace(">", " ").Replace("\t", " ");
        var tokens = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        foreach (var token in tokens)
        {
            var t = token.Trim().ToUpperInvariant();
            // CAN-заголовок / PCI length часто 3 hex-символа (7E8) — пропускаем
            if (t.Length == 3 && IsHexByte("0" + t[0]) && t.StartsWith("7E"))
                continue;
            if (t.Length > 2 && t.Length % 2 == 0 && t.All(c =>
                    (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
            {
                for (int k = 0; k < t.Length; k += 2)
                    parts.Add(t.Substring(k, 2));
            }
            else if (IsHexByte(t))
            {
                parts.Add(t);
            }
        }

        var hexPairs = new List<(string, string)>();

        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] != modePrefix) continue;

            // После 43/47/4A: опционально число DTC, затем пары байт
            int j = i + 1;
            int? expected = null;
            if (j < parts.Count &&
                int.TryParse(parts[j], System.Globalization.NumberStyles.HexNumber, null, out var count) &&
                count <= 40)
            {
                // count — число DTC (не часть кода): пропускаем байт
                expected = count;
                j++;
            }

            int added = 0;
            while (j + 1 < parts.Count)
            {
                if (parts[j] == modePrefix || parts[j] == "43" || parts[j] == "47" || parts[j] == "4A")
                    break;

                var high = parts[j];
                var low = parts[j + 1];
                j += 2;

                // Паддинг 00 00 — не код
                if (high == "00" && low == "00") continue;
                if (!IsHexByte(high) || !IsHexByte(low)) continue;

                hexPairs.Add((high, low));
                added++;
                if (expected.HasValue && added >= expected.Value)
                    break;
            }

            i = Math.Max(i, j - 1);
        }

        foreach (var (high, low) in hexPairs)
        {
            var code = DecodeDTC(high, low);
            if (!string.IsNullOrEmpty(code) && code is not ("P0000" or "C0000" or "B0000" or "U0000"))
                codes.Add(new ObdError { Code = code, Type = type });
        }

        return codes.DistinctBy(c => c.Code).ToList();
    }

    /// <summary>Стандартный SAE J2012: 2 байта → P0xxx / C0xxx / B0xxx / U0xxx.</summary>
    private static string DecodeDTC(string highHex, string lowHex)
    {
        if (!int.TryParse(highHex, System.Globalization.NumberStyles.HexNumber, null, out var high)) return "";
        if (!int.TryParse(lowHex, System.Globalization.NumberStyles.HexNumber, null, out var low)) return "";

        var category = (high >> 6) & 0x03;
        var categoryChar = category switch
        {
            0 => 'P', 1 => 'C', 2 => 'B', 3 => 'U', _ => 'P'
        };

        var firstDigit = (high >> 4) & 0x03;
        var secondNibble = high & 0x0F;
        return $"{categoryChar}{firstDigit}{secondNibble:X}{low:X2}";
    }

    private static bool IsHexByte(string s)
    {
        return s.Length == 2 && s.All(c =>
            (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'));
    }

    private static string ParsePidName(string hex)
    {
        return hex.ToUpper() switch
        {
            "04" => "Нагрузка двигателя (%)",
            "05" => "Температура ОЖ (°C)",
            "0B" => "Давление впуска (кПа)",
            "0C" => "Обороты (RPM)",
            "0D" => "Скорость (км/ч)",
            "0F" => "Температура воздуха (°C)",
            "10" => "MAF (г/с)",
            "11" => "Положение дросселя (%)",
            "21" => "Пробег с CEL (км)",
            "2F" => "Уровень топлива (%)",
            "33" => "Барометрическое давление (кПа)",
            "42" => "Напряжение модуля (В)",
            "44" => "Эквивалентное соотношение",
            "46" => "Температура ОЖ впуск (°C)",
            "49" => "Педаль D (%)",
            "4D" => "Время на ХХ (с)",
            "5A" => "Педаль акселератора (%)",
            _ => $"[PID 0x{hex}]"
        };
    }
}
