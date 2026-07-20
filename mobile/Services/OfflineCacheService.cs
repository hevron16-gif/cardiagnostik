namespace CarDiagnosticApp.Services;

/// <summary>
/// Кеш и офлайн-диагностика. Марки НЕ смешиваются: кеш только для своей марки (+алиасы).
/// </summary>
public class OfflineCacheService
{
    private readonly OfflineDatabase _db;

    public OfflineCacheService(OfflineDatabase db)
    {
        _db = db;
    }

    public async Task CacheDiagnosisAsync(string errorCode, string carBrand,
        string carModel, string diagnosis, string source = "online")
    {
        await _db.InitAsync();
        // Нормализуем ключ марки, чтобы LADA и ВАЗ не плодили разные записи
        var brandKey = CarDiagnosticApp.Data.DiagramDatabase.NormalizeBrand(carBrand);
        if (brandKey is "*" or "") brandKey = (carBrand ?? "").Trim();
        await _db.Cache.UpsertAsync(
            (errorCode ?? "").Trim().ToUpperInvariant(),
            brandKey,
            carModel ?? "",
            diagnosis,
            source);
    }

    public async Task<string?> GetCachedDiagnosisAsync(string errorCode, string carBrand)
    {
        await _db.InitAsync();
        var record = await _db.Cache.FindAsync(errorCode, carBrand);
        return record?.Diagnosis;
    }

    public async Task<OfflineCacheRecord?> GetCachedRecordAsync(string errorCode, string carBrand)
    {
        await _db.InitAsync();
        return await _db.Cache.FindAsync(errorCode, carBrand);
    }

    /// <summary>
    /// Офлайн-диагностика строго по марке:
    /// 1) кеш своей марки  2) БЗ своей марки  3) справочник OBD2 (с пометкой марки).
    /// </summary>
    public async Task<(string Diagnosis, string SourceLabel)?> OfflineDiagnoseAsync(
        string errorCode, string brand, string model)
    {
        await _db.InitAsync();
        var code = (errorCode ?? "").Trim().ToUpperInvariant();
        brand ??= "";
        model ??= "";
        var brandNorm = CarDiagnosticApp.Data.DiagramDatabase.NormalizeBrand(brand);

        // 1. Кеш только своей марки (алиасы LADA/ВАЗ внутри FindAsync)
        var cached = await _db.Cache.FindAsync(code, brand);
        if (cached != null && BrandOk(cached.CarBrand, brand))
        {
            var text = StampBrand(
                EnsureSectioned(cached.Diagnosis, code, brand, model),
                brand, model, code);
            return (text, $"Офлайн-кеш ({brand})");
        }

        // 2. База знаний — только своя марка
        try
        {
            var knowledge = await _db.Knowledge.FindAsync(code, brand);
            if (knowledge != null && BrandOk(knowledge.CarBrand, brand))
            {
                var text = StampBrand(
                    EnsureSectioned(knowledge.Diagnosis, code, brand, model),
                    brand, model, code);
                return (text, $"База знаний ({brand})");
            }

            // Алиасы марки в БЗ
            foreach (var alias in CarDiagnosticApp.Data.DiagramDatabase.BrandAliases(brand))
            {
                if (string.Equals(alias, brand, StringComparison.OrdinalIgnoreCase)) continue;
                knowledge = await _db.Knowledge.FindAsync(code, alias);
                if (knowledge != null && BrandOk(knowledge.CarBrand, brand))
                {
                    var text = StampBrand(
                        EnsureSectioned(knowledge.Diagnosis, code, brand, model),
                        brand, model, code);
                    return (text, $"База знаний ({brand})");
                }
            }
        }
        catch { }

        // 3. ErrorCodeDb + OBD2 (универсальный справочник, но текст помечаем маркой)
        string? solution = null;
        string? description = null;
        try
        {
            var codeDb = new ErrorCodeDbService();
            await codeDb.InitializeAsync();
            var entries = await codeDb.SearchByCodeAsync(code);
            var best = entries
                .Where(e => string.IsNullOrWhiteSpace(e.Brand) || BrandOk(e.Brand, brand))
                .OrderByDescending(e => BrandOk(e.Brand, brand) ? 2 : 0)
                .ThenByDescending(e => e.UseCount)
                .FirstOrDefault();
            if (best != null)
            {
                description = best.Description;
                solution = best.Solution;
            }
        }
        catch { }

        CarDiagnosticApp.Models.KnowledgeItem? obd2 = null;
        try
        {
            obd2 = CarDiagnosticApp.Data.OBD2Codes.All
                .FirstOrDefault(k => string.Equals(k.Code, code, StringComparison.OrdinalIgnoreCase));
        }
        catch { }

        if (obd2 == null && string.IsNullOrWhiteSpace(description) && string.IsNullOrWhiteSpace(solution))
            return null;

        var built = BuildSectionedDiagnosis(
            code, brand, model,
            description ?? obd2?.Description ?? $"Код {code}",
            obd2?.Causes ?? "",
            obd2?.Symptoms ?? "",
            solution ?? "",
            obd2?.Category ?? "");

        return (built, string.IsNullOrWhiteSpace(solution)
            ? $"Справочник OBD2 ({brandNorm})"
            : $"Справочник + рекомендации ({brandNorm})");
    }

    private static bool BrandOk(string? recordBrand, string requestedBrand)
    {
        if (string.IsNullOrWhiteSpace(requestedBrand)) return false;
        if (string.IsNullOrWhiteSpace(recordBrand)) return false; // пустой = неизвестно, не берём
        return CarDiagnosticApp.Data.DiagramDatabase.BrandsMatch(recordBrand, requestedBrand)
            || string.Equals(recordBrand.Trim(), requestedBrand.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Переписывает/добавляет строку «Автомобиль:» чтобы UI не показывал чужую марку.</summary>
    private static string StampBrand(string text, string brand, string model, string code)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var header = $"Автомобиль: {brand} {model}".Trim();
        // Убираем старые строки «Автомобиль: …»
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        lines = lines.Where(l => !l.TrimStart().StartsWith("Автомобиль:", StringComparison.OrdinalIgnoreCase)).ToList();

        // Вставляем после первой секции / кода
        var insertAt = 0;
        for (int i = 0; i < Math.Min(lines.Count, 6); i++)
        {
            if (lines[i].Contains(code, StringComparison.OrdinalIgnoreCase) ||
                lines[i].TrimStart().StartsWith("1."))
            {
                insertAt = i + 1;
                break;
            }
        }
        lines.Insert(insertAt, header);
        return string.Join("\n", lines);
    }

    private static string EnsureSectioned(string? raw, string code, string brand, string model)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return BuildSectionedDiagnosis(code, brand, model, $"Код {code}", "", "", "", "");

        if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"(?:^|\n)\s*\*?1\.\s+"))
            return raw;

        return BuildSectionedDiagnosis(code, brand, model, raw.Trim(), "", "", "", "");
    }

    public static string BuildSectionedDiagnosis(
        string code, string brand, string model,
        string description, string causes, string symptoms, string solution, string category)
    {
        var sb = new System.Text.StringBuilder();
        var brandKey = CarDiagnosticApp.Data.DiagramDatabase.NormalizeBrand(brand);

        sb.AppendLine("1. Расшифровка ошибки");
        sb.AppendLine($"{code}: {description}");
        if (!string.IsNullOrWhiteSpace(category))
            sb.AppendLine($"Категория: {category}");
        sb.AppendLine($"Автомобиль: {brand} {model}".Trim());
        sb.AppendLine();

        sb.AppendLine("2. Вероятные причины");
        var causeLines = SplitList(causes);
        if (causeLines.Count == 0)
            causeLines.Add("Требуется уточнение по симптомам и live-данным");
        foreach (var c in causeLines)
            sb.AppendLine($"- {c}");
        foreach (var tip in BrandTypicalCauses(brandKey, code))
            sb.AppendLine($"- {tip}");
        sb.AppendLine();

        sb.AppendLine("3. Способы устранения");
        var fixLines = SplitList(solution);
        if (fixLines.Count == 0)
            fixLines = GenerateFixesFromCauses(causeLines, brandKey, code);
        foreach (var f in fixLines)
            sb.AppendLine($"- {f}");
        sb.AppendLine();

        sb.AppendLine("4. Рекомендация");
        if (!string.IsNullOrWhiteSpace(symptoms))
            sb.AppendLine($"Симптомы: {symptoms}");
        sb.AppendLine(BrandFinalAdvice(brandKey, code, model));
        sb.AppendLine("После ремонта сбросьте ошибки и проверьте, что код не вернулся.");

        return sb.ToString().Trim();
    }

    private static List<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return text
            .Split(new[] { ',', ';', '\n', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().TrimStart('-', '•', '*', ' '))
            .Where(s => s.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
    }

    private static List<string> GenerateFixesFromCauses(List<string> causes, string brandKey, string code)
    {
        var fixes = new List<string>();
        foreach (var c in causes.Take(4))
        {
            if (c.Contains("датчик", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("MAF", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("O₂", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("лямбда", StringComparison.OrdinalIgnoreCase))
                fixes.Add($"[Осторожно] Проверить разъём и проводку: {c}");
            else if (c.Contains("форсунк", StringComparison.OrdinalIgnoreCase) ||
                     c.Contains("топлив", StringComparison.OrdinalIgnoreCase))
                fixes.Add($"[Осторожно] Диагностика топливной системы: {c}");
            else if (c.Contains("свеч", StringComparison.OrdinalIgnoreCase) ||
                     c.Contains("катуш", StringComparison.OrdinalIgnoreCase))
                fixes.Add($"[Безопасно] Проверить/заменить: {c}");
            else
                fixes.Add($"Проверить: {c}");
        }

        fixes.AddRange(BrandTypicalFixes(brandKey, code));
        if (fixes.Count == 0)
            fixes.Add("[Только специалист] Полная диагностика по коду на дилерском сканере");
        return fixes.Distinct().Take(6).ToList();
    }

    private static IEnumerable<string> BrandTypicalCauses(string brandKey, string code)
    {
        if (brandKey == "ВАЗ")
        {
            yield return "ВАЗ/LADA: окисление разъёмов датчиков / плохая масса ЭБУ";
            if (code.StartsWith("P03"))
                yield return "ВАЗ: свечи и катушки (16-кл. 1.6/1.8)";
            if (code is "P0171" or "P0172")
                yield return "ВАЗ: подсос воздуха после ДМРВ, РХХ, прокладка впуска";
        }
        else if (brandKey == "КАМАЗ")
        {
            yield return "КАМАЗ: топливный фильтр-отстойник / воздух в топливе";
            if (code.StartsWith("P02") || code.StartsWith("P00"))
                yield return "КАМАЗ: ТНВД / давление common rail";
            if (code is "P0234" or "P0299" or "P0236")
                yield return "КАМАЗ: турбина / интеркулер / датчик наддува";
        }
        else if (brandKey == "ГАЗ")
            yield return "ГАЗ: ДМРВ/MAP и качество топлива";
        else if (brandKey == "УАЗ")
            yield return "УАЗ: ДПКВ/ДМРВ и проводка подкапотного пространства";
    }

    private static IEnumerable<string> BrandTypicalFixes(string brandKey, string code)
    {
        if (brandKey == "ВАЗ")
        {
            yield return "[Безопасно] Очистить/переподключить разъёмы датчиков и «массу» двигателя";
            yield return "[Осторожно] Считать freeze frame и live-данные до замены деталей";
        }
        else if (brandKey == "КАМАЗ")
        {
            yield return "[Безопасно] Слить отстой топлива, заменить топливный фильтр";
            yield return "[Осторожно] Проверить давление наддува и герметичность интеркулера";
            yield return "[Только специалист] Диагностика ТНВД / Common Rail на стенде";
        }
        else if (brandKey == "ГАЗ")
            yield return "[Осторожно] Проверить MAF/MAP и вакуумные шланги";
    }

    private static string BrandFinalAdvice(string brandKey, string code, string model)
    {
        return brandKey switch
        {
            "ВАЗ" => $"Для LADA/ВАЗ {model}: сначала разъёмы, свечи, фильтры; не меняйте катализатор по одному P0420 без проверки ДК.".Trim(),
            "КАМАЗ" => $"Для КАМАЗ {model}: приоритет — топливо, фильтры, наддув (дизель ≠ бензин).".Trim(),
            "ГАЗ" => $"Для ГАЗ {model}: сверьте код с live-данными MAF/давления топлива.".Trim(),
            "УАЗ" => $"Для УАЗ {model}: проверьте ДПКВ/проводку и качество топлива.".Trim(),
            _ => $"Учитывайте особенности {brandKey} при выборе запчастей. При отсутствии результата — диагностика у специалиста."
        };
    }
}
