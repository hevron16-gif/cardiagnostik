using System.Text.RegularExpressions;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Результат расшифровки VIN: марка, модель, год, уверенность.
/// </summary>
public sealed class VinDecodeResult
{
    public string Vin { get; init; } = "";
    public string Brand { get; init; } = "";
    public string Model { get; init; } = "";
    public int? Year { get; init; }
    public string Manufacturer { get; init; } = "";
    public string Wmi { get; init; } = "";
    public string Plant { get; init; } = "";
    public double Confidence { get; init; }
    public string Summary { get; init; } = "";
    public bool IsValid { get; init; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Brand)) parts.Add(Brand);
        if (!string.IsNullOrWhiteSpace(Model)) parts.Add(Model);
        if (Year is > 0) parts.Add(Year.Value.ToString());
        return parts.Count > 0 ? string.Join(" ", parts) : Vin;
    }
}

/// <summary>
/// Декодирование VIN (ISO 3779): WMI → марка, pos.10 → год, VDS → модель (РФ + популярные).
/// </summary>
public static class VinDecoderService
{
    // ── WMI → (Brand, Manufacturer) ──────────────────────────────
    private static readonly Dictionary<string, (string Brand, string Manufacturer)> WmiMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── АвтоВАЗ / LADA ──
            ["XTA"] = ("LADA", "АвтоВАЗ"),
            ["XTB"] = ("LADA", "АвтоВАЗ"),
            ["XTC"] = ("LADA", "АвтоВАЗ"),
            ["X7L"] = ("LADA", "АвтоВАЗ / Renault Russia"),
            ["X7M"] = ("LADA", "ИжАвто / АвтоВАЗ"),
            ["XUF"] = ("LADA", "GM-АвтоВАЗ / Chevrolet Niva"),

            // ── УАЗ ──
            ["X89"] = ("УАЗ", "УАЗ"),
            ["X90"] = ("УАЗ", "УАЗ"),
            ["X9L"] = ("УАЗ", "УАЗ"),

            // ── ГАЗ ──
            ["X96"] = ("ГАЗ", "ГАЗ"),
            ["X9P"] = ("ГАЗ", "ГАЗ"),
            ["X9W"] = ("ГАЗ", "ГАЗ"),

            // ── КАМАЗ ──
            ["XTH"] = ("TagAZ", "ТагАЗ"),
            ["XTT"] = ("TagAZ", "ТагАЗ"),

            // ── Прочие РФ ──
            ["Z8T"] = ("Renault", "Renault Russia"),
            ["Z8N"] = ("Nissan", "Nissan Manufacturing Rus"),
            ["Z94"] = ("Hyundai", "HMMR / Хёндэ Мотор Мануфактуринг Рус"),
            ["XWB"] = ("Volkswagen", "Volkswagen Group Rus"),
            ["XW8"] = ("Volkswagen", "Volkswagen Group Rus"),
            ["X4X"] = ("BMW", "BMW Russia"),
            ["X4U"] = ("BMW", "BMW"),
            ["XWE"] = ("Ford", "Ford Sollers"),
            ["X9F"] = ("Ford", "Ford Sollers"),


            // ── Мировые ──
            ["WVW"] = ("Volkswagen", "Volkswagen AG"),
            ["WBA"] = ("BMW", "BMW AG"),
            ["WDB"] = ("Mercedes-Benz", "Mercedes-Benz"),
            ["WDD"] = ("Mercedes-Benz", "Mercedes-Benz"),
            ["WAU"] = ("Audi", "Audi AG"),
            ["VF1"] = ("Renault", "Renault"),
            ["VF3"] = ("Peugeot", "Peugeot"),
            ["VF7"] = ("Citroen", "Citroën"),
            ["SAJ"] = ("Jaguar", "Jaguar"),
            ["SAL"] = ("Land Rover", "Land Rover"),
            ["JT"]  = ("Toyota", "Toyota"), // 2-char handled specially
            ["JTD"] = ("Toyota", "Toyota"),
            ["JTE"] = ("Toyota", "Toyota"),
            ["JTN"] = ("Toyota", "Toyota"),
            ["JN1"] = ("Nissan", "Nissan"),
            ["KMH"] = ("Hyundai", "Hyundai"),
            ["KNA"] = ("Kia", "Kia"),
            ["TMB"] = ("Skoda", "Škoda"),
            ["TMA"] = ("Hyundai", "Hyundai"),
            ["UU1"] = ("Dacia", "Dacia"),
            ["ZFA"] = ("Fiat", "Fiat"),
            ["1G1"] = ("Chevrolet", "GM"),
            ["1FA"] = ("Ford", "Ford"),
            ["1HG"] = ("Honda", "Honda"),
            ["5YJ"] = ("Tesla", "Tesla"),
            ["LFM"] = ("Chery", "Chery"),
            ["LVV"] = ("Chery", "Chery"),
            ["LGB"] = ("Geely", "Geely"),
            ["LSV"] = ("Haval", "Great Wall / Haval"),
            ["LGW"] = ("Great Wall", "Great Wall"),
        };

    // Модельные коды в VDS (позиции 4–8) — РФ
    private static readonly (string Pattern, string Brand, string Model)[] VdsPatterns =
    {
        // LADA classic / modern
        ("21099", "LADA", "2109"),
        ("21093", "LADA", "2109"),
        ("2109", "LADA", "2109"),
        ("2108", "LADA", "2108"),
        ("2110", "LADA", "2110"),
        ("2112", "LADA", "2112"),
        ("2114", "LADA", "2114"),
        ("2115", "LADA", "2115"),
        ("1118", "LADA", "Kalina"),
        ("1119", "LADA", "Kalina"),
        ("2192", "LADA", "Granta"),
        ("2190", "LADA", "Granta"),
        ("2194", "LADA", "Granta"),
        ("2170", "LADA", "Priora"),
        ("2172", "LADA", "Priora"),
        ("2180", "LADA", "Largus"),
        ("2181", "LADA", "Largus"),
        ("21214", "LADA", "Niva Legend"),
        ("2121", "LADA", "Niva Legend"),
        ("2123", "LADA", "Niva Travel"),
        ("GFL11", "LADA", "Vesta"),
        ("GFL12", "LADA", "Vesta"),
        ("GFK11", "LADA", "Vesta"),
        ("GFK12", "LADA", "Vesta"),
        ("2180", "LADA", "Largus"),
        // УАЗ
        ("3163", "УАЗ", "Patriot"),
        ("2363", "УАЗ", "Pickup"),
        ("3151", "УАЗ", "Hunter"),
        ("2206", "УАЗ", "СГР (Буханка)"),
        ("3741", "УАЗ", "СГР (Буханка)"),
        ("2360", "УАЗ", "Профи"),
        // ГАЗ
        ("A21R22", "ГАЗ", "Газель NEXT"),
        ("A21R32", "ГАЗ", "Газель NEXT"),
        ("A21R23", "ГАЗ", "Газель NEXT"),
        ("3302", "ГАЗ", "Газель"),
        ("2705", "ГАЗ", "Газель"),
        ("2752", "ГАЗ", "Соболь"),
        ("2217", "ГАЗ", "Соболь"),
        ("A31R32", "ГАЗ", "Газель NN"),
        ("C41R13", "ГАЗ", "Валдай NEXT"),
        // КАМАЗ
        ("54901", "КАМАЗ", "54901"),
        ("5490", "КАМАЗ", "5490"),
        ("65115", "КАМАЗ", "65115"),
        ("65117", "КАМАЗ", "65117"),
        ("5320", "КАМАЗ", "5320"),
        ("43118", "КАМАЗ", "43118"),
        ("6520", "КАМАЗ", "6520"),
        ("4308", "КАМАЗ", "4308"),
    };

    // Год: pos.10 (ISO 3779, 30-летний цикл; без I,O,Q,U,Z)
    private static readonly Dictionary<char, int> YearCodeBase = new()
    {
        ['A'] = 2010, ['B'] = 2011, ['C'] = 2012, ['D'] = 2013, ['E'] = 2014,
        ['F'] = 2015, ['G'] = 2016, ['H'] = 2017, ['J'] = 2018, ['K'] = 2019,
        ['L'] = 2020, ['M'] = 2021, ['N'] = 2022, ['P'] = 2023, ['R'] = 2024,
        ['S'] = 2025, ['T'] = 2026, ['V'] = 2027, ['W'] = 2028, ['X'] = 2029,
        ['Y'] = 2030,
        ['1'] = 2001, ['2'] = 2002, ['3'] = 2003, ['4'] = 2004, ['5'] = 2005,
        ['6'] = 2006, ['7'] = 2007, ['8'] = 2008, ['9'] = 2009,
    };

    /// <summary>
    /// Полная расшифровка VIN.
    /// </summary>
    public static VinDecodeResult Decode(string? vin)
    {
        var cleaned = NormalizeVin(vin);
        if (cleaned.Length != 17)
        {
            return new VinDecodeResult
            {
                Vin = cleaned,
                IsValid = false,
                Summary = cleaned.Length == 0 ? "VIN не получен" : $"Некорректный VIN ({cleaned.Length} симв.)",
                Confidence = 0,
            };
        }

        var wmi = cleaned[..3];
        var vds = cleaned.Substring(3, 6);
        var yearChar = cleaned[9];
        var plant = cleaned[10].ToString();

        string brand = "";
        string manufacturer = "";
        string model = "";
        double confidence = 0.3;

        // WMI
        if (WmiMap.TryGetValue(wmi, out var wmiInfo))
        {
            brand = wmiInfo.Brand;
            manufacturer = wmiInfo.Manufacturer;
            confidence = 0.7;
        }
        else if (wmi.Length >= 2 && WmiMap.TryGetValue(wmi[..2], out wmiInfo))
        {
            brand = wmiInfo.Brand;
            manufacturer = wmiInfo.Manufacturer;
            confidence = 0.55;
        }
        else
        {
            // Частичные правила по первой букве региона
            brand = GuessBrandByRegion(wmi);
            if (!string.IsNullOrEmpty(brand))
                confidence = 0.4;
        }

        // VDS → модель (и иногда переопределение марки, напр. КАМАЗ)
        foreach (var (pattern, pBrand, pModel) in VdsPatterns.OrderByDescending(p => p.Pattern.Length))
        {
            if (vds.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                cleaned.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                model = pModel;
                // КАМАЗ/ГАЗ по VDS надёжнее WMI
                if (pBrand is "КАМАЗ" or "ГАЗ" or "УАЗ" or "LADA")
                {
                    if (string.IsNullOrEmpty(brand) || pBrand == "КАМАЗ" ||
                        string.Equals(brand, pBrand, StringComparison.OrdinalIgnoreCase) ||
                        (brand is "LADA" or "ВАЗ" && pBrand == "LADA"))
                    {
                        brand = pBrand;
                    }
                }
                confidence = Math.Max(confidence, 0.85);
                break;
            }
        }

        // Доп. эвристики LADA по VDS
        if ((brand is "LADA" or "ВАЗ" or "") && string.IsNullOrEmpty(model))
            model = GuessLadaModel(vds, cleaned);

        if (!string.IsNullOrEmpty(model) && confidence < 0.8)
            confidence = Math.Max(confidence, 0.75);

        var year = DecodeYear(yearChar, brand, model);

        // Нормализация «ВАЗ» ↔ «LADA»
        if (brand is "ВАЗ") brand = "LADA";

        var summary = BuildSummary(brand, model, year, manufacturer, cleaned);
        return new VinDecodeResult
        {
            Vin = cleaned,
            Brand = brand,
            Model = model,
            Year = year,
            Manufacturer = manufacturer,
            Wmi = wmi,
            Plant = plant,
            Confidence = confidence,
            Summary = summary,
            IsValid = true,
        };
    }

    /// <summary>
    /// Подбирает значение марки из списка пикера (алиасы LADA/ВАЗ и т.п.).
    /// </summary>
    public static string? MatchBrandInList(string decodedBrand, IEnumerable<string> available)
    {
        if (string.IsNullOrWhiteSpace(decodedBrand)) return null;
        var list = available.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        if (list.Count == 0) return null;

        // Точное
        var exact = list.FirstOrDefault(b =>
            string.Equals(b, decodedBrand, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Алиасы
        var aliases = GetBrandAliases(decodedBrand);
        foreach (var a in aliases)
        {
            var hit = list.FirstOrDefault(b =>
                string.Equals(b, a, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        // Содержит
        foreach (var a in aliases)
        {
            var hit = list.FirstOrDefault(b =>
                b.Contains(a, StringComparison.OrdinalIgnoreCase) ||
                a.Contains(b, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>
    /// Подбирает модель из списка (нечёткое совпадение).
    /// </summary>
    public static string? MatchModelInList(string decodedModel, IEnumerable<string> available)
    {
        if (string.IsNullOrWhiteSpace(decodedModel)) return null;
        var list = available.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
        if (list.Count == 0) return null;

        var exact = list.FirstOrDefault(m =>
            string.Equals(m, decodedModel, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // «Vesta NG» vs «Vesta»
        var contains = list.FirstOrDefault(m =>
            m.Contains(decodedModel, StringComparison.OrdinalIgnoreCase) ||
            decodedModel.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (contains != null) return contains;

        // Слова
        var tokens = Regex.Split(decodedModel, @"[\s\-_]+")
            .Where(t => t.Length >= 3)
            .ToArray();
        foreach (var t in tokens)
        {
            var hit = list.FirstOrDefault(m =>
                m.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }

        return null;
    }

    /// <summary>
    /// Базовый офлайн-список марок/моделей (если API недоступен).
    /// </summary>
    public static List<Models.CarBrand> GetOfflineBrandCatalog()
    {
        return new List<Models.CarBrand>
        {
            new() { brand = "LADA", models = new() { "Vesta", "Vesta NG", "Granta", "Granta FL", "Niva Legend", "Niva Travel", "Largus", "Aura", "Priora", "Kalina", "XRAY", "2107", "2109", "2110", "2114" } },
            new() { brand = "ВАЗ", models = new() { "2107", "2109", "2110", "2114", "Priora", "Kalina", "Granta", "Vesta", "Niva" } },
            new() { brand = "УАЗ", models = new() { "Patriot", "Patriot FL", "Pickup", "Hunter", "СГР (Буханка)", "Профи" } },
            new() { brand = "ГАЗ", models = new() { "Газель NEXT", "Газель NN", "Газель", "Соболь NN", "Соболь", "Валдай NEXT" } },
            new() { brand = "КАМАЗ", models = new() { "54901", "5490", "65115", "65117", "5320", "43118", "6520", "4308" } },
            new() { brand = "Москвич", models = new() { "Москвич 3", "Москвич 6", "Москвич 8" } },
            new() { brand = "Toyota", models = new() { "Camry", "Corolla", "RAV4", "Land Cruiser", "Hilux" } },
            new() { brand = "Hyundai", models = new() { "Solaris", "Creta", "Tucson", "Santa Fe", "Elantra" } },
            new() { brand = "Kia", models = new() { "Rio", "Sportage", "Ceed", "Sorento", "K5" } },
            new() { brand = "Volkswagen", models = new() { "Polo", "Jetta", "Tiguan", "Passat", "Golf" } },
            new() { brand = "Renault", models = new() { "Logan", "Sandero", "Duster", "Kaptur", "Arkana" } },
            new() { brand = "Nissan", models = new() { "Qashqai", "X-Trail", "Almera", "Terrano" } },
            new() { brand = "Skoda", models = new() { "Octavia", "Rapid", "Kodiaq", "Karoq" } },
            new() { brand = "BMW", models = new() { "3 Series", "5 Series", "X3", "X5" } },
            new() { brand = "Mercedes-Benz", models = new() { "C-Class", "E-Class", "GLC", "GLE" } },
            new() { brand = "Ford", models = new() { "Focus", "Mondeo", "Kuga", "Transit" } },
            new() { brand = "Chevrolet", models = new() { "Niva", "Cruze", "Lacetti", "Aveo" } },
            new() { brand = "Chery", models = new() { "Tiggo 4", "Tiggo 7", "Tiggo 8", "Arrizo" } },
            new() { brand = "Haval", models = new() { "Jolion", "F7", "H6", "Dargo" } },
            new() { brand = "Geely", models = new() { "Coolray", "Atlas", "Monjaro", "Emgrand" } },
        };
    }

    // ══════════════ helpers ══════════════

    public static string NormalizeVin(string? vin)
    {
        if (string.IsNullOrWhiteSpace(vin)) return "";
        var s = vin.Trim().ToUpperInvariant()
            .Replace(" ", "")
            .Replace("-", "")
            .Replace("\r", "")
            .Replace("\n", "");
        // I,O,Q в VIN недопустимы — иногда адаптер отдаёт мусор
        return s;
    }

    public static bool IsPlausibleVin(string? vin)
    {
        var v = NormalizeVin(vin);
        if (v.Length != 17) return false;
        if (!Regex.IsMatch(v, @"^[A-HJ-NPR-Z0-9]{17}$")) return false;
        return true;
    }

    /// <summary>
    /// Проверяет check-digit VIN (позиция 9) по алгоритму ISO 3779.
    /// </summary>
    public static bool ValidateVinCheckDigit(string? vin)
    {
        var v = NormalizeVin(vin);
        if (v.Length != 17) return false;

        // Весовые коэффициенты для позиций 1-17
        var weights = new[] { 8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2 };
        // Транслитерация букв в цифры
        var transliteration = new Dictionary<char, int>
        {
            ['A'] = 1, ['B'] = 2, ['C'] = 3, ['D'] = 4, ['E'] = 5, ['F'] = 6, ['G'] = 7, ['H'] = 8,
            ['J'] = 1, ['K'] = 2, ['L'] = 3, ['M'] = 4, ['N'] = 5, ['P'] = 7, ['R'] = 9,
            ['S'] = 2, ['T'] = 3, ['U'] = 4, ['V'] = 5, ['W'] = 6, ['X'] = 7, ['Y'] = 8, ['Z'] = 9,
            ['0'] = 0, ['1'] = 1, ['2'] = 2, ['3'] = 3, ['4'] = 4, ['5'] = 5, ['6'] = 6, ['7'] = 7, ['8'] = 8, ['9'] = 9,
        };

        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            if (i == 8) continue; // Позиция 9 — check-digit, пропускаем
            if (!transliteration.TryGetValue(v[i], out int value))
                return false;
            sum += value * weights[i];
        }

        int checkDigit = sum % 11;
        char expectedCheckDigit = checkDigit == 10 ? 'X' : (char)('0' + checkDigit);
        return v[8] == expectedCheckDigit;
    }

    private static IEnumerable<string> GetBrandAliases(string brand)
    {
        yield return brand;
        var u = brand.ToUpperInvariant();
        if (u is "LADA" or "LAДА" or "ВАЗ" or "AVTOVAZ" or "АВТОВАЗ")
        {
            yield return "LADA";
            yield return "Lada";
            yield return "ВАЗ";
            yield return "АвтоВАЗ";
        }
        if (u is "KAMAZ" or "КАМАЗ" or "КАМAЗ")
        {
            yield return "КАМАЗ";
            yield return "KAMAZ";
            yield return "КамАЗ";
        }
        if (u is "GAZ" or "ГАЗ")
        {
            yield return "ГАЗ";
            yield return "GAZ";
        }
        if (u is "UAZ" or "УАЗ")
        {
            yield return "УАЗ";
            yield return "UAZ";
        }
        if (u.Contains("MERCEDES"))
        {
            yield return "Mercedes-Benz";
            yield return "Mercedes";
        }
    }

    private static string GuessBrandByRegion(string wmi)
    {
        if (wmi.StartsWith('X') || wmi.StartsWith('Z'))
        {
            // РФ/СНГ без точного WMI
            return "";
        }
        return "";
    }

    private static string GuessLadaModel(string vds, string fullVin)
    {
        // Современные Vesta: часто GFL/GFK в VDS
        if (vds.Contains("GFL", StringComparison.OrdinalIgnoreCase) ||
            vds.Contains("GFK", StringComparison.OrdinalIgnoreCase))
            return "Vesta";
        if (fullVin.Contains("2190") || fullVin.Contains("2192") || fullVin.Contains("2194"))
            return "Granta";
        if (fullVin.Contains("2180") || fullVin.Contains("2181"))
            return "Largus";
        if (fullVin.Contains("2123"))
            return "Niva Travel";
        if (fullVin.Contains("2121") || fullVin.Contains("21214"))
            return "Niva Legend";
        if (fullVin.Contains("2170") || fullVin.Contains("2172"))
            return "Priora";
        if (fullVin.Contains("1118") || fullVin.Contains("1119"))
            return "Kalina";
        return "";
    }

    private static int? DecodeYear(char yearCode, string brand, string model)
    {
        yearCode = char.ToUpperInvariant(yearCode);
        if (!YearCodeBase.TryGetValue(yearCode, out var y2010s))
        {
            // Старый цикл 1980-2009 для букв A-Y
            var legacy = "ABCDEFGHJKLMNPRSTVWXY";
            var idx = legacy.IndexOf(yearCode);
            if (idx >= 0)
                y2010s = 1980 + idx;
            else
                return null;
        }

        // 30-летний цикл: для цифр 1-9 база 2001-2009, альтернатива 2031-2039 (ещё рано)
        // Для букв A-Y: 2010-2030, альтернатива 1980-2000
        var now = DateTime.Now.Year + 1;
        var candidates = new List<int> { y2010s };
        if (y2010s >= 2010)
            candidates.Add(y2010s - 30); // 1980-2000
        else if (y2010s >= 2001 && y2010s <= 2009)
            candidates.Add(y2010s + 30); // 2031+

        // Фильтр разумного диапазона
        candidates = candidates.Where(y => y >= 1980 && y <= now).ToList();
        if (candidates.Count == 0) return null;

        // Учитываем год старта модели
        var modelStart = GuessModelStartYear(brand, model);
        if (modelStart.HasValue)
        {
            var fit = candidates.Where(y => y >= modelStart.Value - 1).ToList();
            if (fit.Count > 0) candidates = fit;
        }

        // Ближе к текущему году, но не в будущем
        return candidates.OrderBy(y => Math.Abs(now - 1 - y)).First();
    }

    private static int? GuessModelStartYear(string brand, string model)
    {
        if (string.IsNullOrEmpty(model)) return null;
        var m = model.ToLowerInvariant();
        if (m.Contains("vesta")) return 2015;
        if (m.Contains("granta")) return 2011;
        if (m.Contains("largus")) return 2012;
        if (m.Contains("prior")) return 2007;
        if (m.Contains("kalina")) return 2004;
        if (m.Contains("patriot")) return 2005;
        if (m.Contains("next")) return 2013;
        if (m.Contains("5490")) return 2013;
        if (m.Contains("54901")) return 2020;
        return null;
    }

    private static string BuildSummary(string brand, string model, int? year, string manufacturer, string vin)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(brand)) parts.Add(brand);
        if (!string.IsNullOrWhiteSpace(model)) parts.Add(model);
        if (year is > 0) parts.Add(year.Value.ToString());
        var car = parts.Count > 0 ? string.Join(" ", parts) : "авто не определено";
        var mfr = string.IsNullOrWhiteSpace(manufacturer) ? "" : $" · {manufacturer}";
        return $"VIN {vin}: {car}{mfr}";
    }
}
