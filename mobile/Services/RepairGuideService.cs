using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис хранения ремонтных руководств и шагов.
/// База: repair_guides.db, таблицы repair_guides + repair_steps.
/// </summary>
public class RepairGuideService
{
    private SQLiteAsyncConnection? _db;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public RepairGuideService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "repair_guides.db");
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
            await _db.CreateTableAsync<RepairGuide>();
            await _db.CreateTableAsync<RepairStep>();
            return _db;
        }
        finally { _lock.Release(); }
    }

    // ══════════════ Руководства ══════════════

    public async Task<int> InsertGuideAsync(RepairGuide guide)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(guide);
        return guide.Id;
    }

    public async Task<List<RepairGuide>> FindGuidesAsync(string errorCode, string? brand, string? model)
    {
        var db = await GetDbAsync();

        // Приоритет: точное совпадение код+бренд+модель
        var exact = await db.Table<RepairGuide>()
            .Where(g => g.ErrorCode == errorCode
                     && g.Brand == brand
                     && g.ModelName == model)
            .OrderByDescending(g => g.CompletionCount)
            .Take(10)
            .ToListAsync();

        // Бренд + код (без модели)
        var brandOnly = new List<RepairGuide>();
        if (brand != null && exact.Count < 5)
        {
            brandOnly = await db.Table<RepairGuide>()
                .Where(g => g.ErrorCode == errorCode
                         && g.Brand == brand
                         && g.ModelName == null)
                .OrderByDescending(g => g.CompletionCount)
                .Take(5)
                .ToListAsync();
        }

        // Универсальные (только код)
        var generic = new List<RepairGuide>();
        if (exact.Count + brandOnly.Count < 5)
        {
            generic = await db.Table<RepairGuide>()
                .Where(g => g.ErrorCode == errorCode
                         && g.Brand == null)
                .OrderByDescending(g => g.CompletionCount)
                .Take(5)
                .ToListAsync();
        }

        return exact.Concat(brandOnly).Concat(generic).DistinctBy(g => g.Id).Take(10).ToList();
    }

    public async Task<List<RepairGuide>> GetGuidesByBrandAsync(string brand, int limit = 20)
    {
        var db = await GetDbAsync();
        return await db.Table<RepairGuide>()
            .Where(g => g.Brand == brand)
            .OrderByDescending(g => g.ViewCount)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<RepairGuide>> GetAllGuidesAsync(int limit = 100)
    {
        var db = await GetDbAsync();
        return await db.Table<RepairGuide>()
            .OrderByDescending(g => g.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<RepairGuide?> GetGuideByIdAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.Table<RepairGuide>().Where(g => g.Id == id).FirstOrDefaultAsync();
    }

    public async Task IncrementViewAsync(int guideId)
    {
        var db = await GetDbAsync();
        var guide = await db.Table<RepairGuide>().Where(g => g.Id == guideId).FirstOrDefaultAsync();
        if (guide != null)
        {
            guide.ViewCount++;
            await db.UpdateAsync(guide);
        }
    }

    public async Task RecordFeedbackAsync(int guideId, bool helpful)
    {
        var db = await GetDbAsync();
        var guide = await db.Table<RepairGuide>().Where(g => g.Id == guideId).FirstOrDefaultAsync();
        if (guide == null) return;
        if (helpful) guide.HelpfulCount++;
        else guide.NotHelpfulCount++;
        await db.UpdateAsync(guide);
    }

    public async Task RecordCompletionAsync(int guideId)
    {
        var db = await GetDbAsync();
        var guide = await db.Table<RepairGuide>().Where(g => g.Id == guideId).FirstOrDefaultAsync();
        if (guide != null)
        {
            guide.CompletionCount++;
            await db.UpdateAsync(guide);
        }
    }

    public async Task<int> DeleteGuideAsync(int id)
    {
        var db = await GetDbAsync();
        await db.Table<RepairStep>().DeleteAsync(s => s.GuideId == id);
        return await db.DeleteAsync<RepairGuide>(id);
    }

    // ══════════════ Шаги ══════════════

    public async Task<int> InsertStepAsync(RepairStep step)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(step);
        return step.Id;
    }

    public async Task<int> InsertAllStepsAsync(IEnumerable<RepairStep> steps)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(steps);
    }

    public async Task<List<RepairStep>> GetStepsAsync(int guideId)
    {
        var db = await GetDbAsync();
        return await db.Table<RepairStep>()
            .Where(s => s.GuideId == guideId)
            .OrderBy(s => s.StepNumber)
            .ToListAsync();
    }

    public async Task<int> CountStepsAsync(int guideId)
    {
        var db = await GetDbAsync();
        return await db.Table<RepairStep>()
            .Where(s => s.GuideId == guideId)
            .CountAsync();
    }

    public async Task<int> UpdateStepAsync(RepairStep step)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(step);
    }

    public async Task<int> DeleteStepsForGuideAsync(int guideId)
    {
        var db = await GetDbAsync();
        return await db.Table<RepairStep>().DeleteAsync(s => s.GuideId == guideId);
    }

    // ══════════════ Статистика ══════════════

    public async Task<int> CountGuidesAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<RepairGuide>().CountAsync();
    }

    public async Task<int> TotalViewsAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<RepairGuide>().ToListAsync();
        return all.Sum(g => g.ViewCount);
    }

    public async Task<int> TotalCompletionsAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<RepairGuide>().ToListAsync();
        return all.Sum(g => g.CompletionCount);
    }

    public async Task<double> AverageRatingAsync()
    {
        var db = await GetDbAsync();
        var all = await db.Table<RepairGuide>().ToListAsync();
        var rated = all.Where(g => g.HelpfulCount + g.NotHelpfulCount > 0).ToList();
        return rated.Count == 0 ? 0 : rated.Average(g => g.Rating);
    }

    // ══════════════ Seed-данные ══════════════

    public async Task<int> SeedDefaultsAsync()
    {
        var db = await GetDbAsync();
        var existing = await db.Table<RepairGuide>().CountAsync();
        if (existing > 0) return 0;

        var guides = BuildSeedGuides();
        var stepCount = 0;

        foreach (var guide in guides)
        {
            var steps = guide.steps;
            guide.guide.CreatedAt = DateTime.UtcNow;
            await db.InsertAsync(guide.guide);

            foreach (var step in steps)
            {
                step.GuideId = guide.guide.Id;
                await db.InsertAsync(step);
                stepCount++;
            }
        }

        System.Diagnostics.Debug.WriteLine($"[RepairGuideService] Seeded {guides.Count} guides, {stepCount} steps.");
        return guides.Count;
    }

    // ══════════════════════════════════════════════════════
    //  ГЕНЕРАЦИЯ РУКОВОДСТВ С КАРТИНКАМИ И СХЕМАМИ
    // ══════════════════════════════════════════════════════

    /// <summary>
    /// Генерирует пошаговое руководство для кода ошибки + авто.
    /// Обогащает каждым шаг: схемой из локальной базы, картинкой из интернета,
    /// и знаниями из самообучения.
    /// Возвращает ID созданного руководства или -1 при ошибке.
    /// </summary>
    public async Task<int> GenerateGuideAsync(string errorCode, string brand, string? model, string? engineCode = null)
    {
        try
        {
            // 1. Ищем схемы в локальной базе
            var diagramDb = new DiagramDbService();
            var diagSvc = diagramDb;
            string? diagramPath = null;
            string? diagramSourceUrl = null;

            if (!string.IsNullOrWhiteSpace(model))
            {
                diagramPath = await diagSvc.GetImageDiagramPathAsync(brand, model, errorCode);
                if (diagramPath == null)
                {
                    // Пробуем векторную схему — преобразуем в заглушку
                    var vectorDiagram = await diagSvc.GetDiagramAsync(brand, model, errorCode)
                                       ?? await diagSvc.GetDiagramByCodeAsync(errorCode);
                    if (vectorDiagram != null)
                        diagramPath = "vector:" + errorCode; // плейсхолдер для UI
                }
            }

            // 2. Ищем накопленные знания
            string? enrichment = null;
            try
            {
                var learningDb = new LearningDbService();
                enrichment = await learningDb.BuildEnrichmentAsync(errorCode, brand, model ?? "");
            }
            catch { /* не критично */ }

            // 3. Ищем картинки ремонта в интернете
            var imageUrls = await SearchRepairImagesAsync(errorCode, brand, model ?? "", 3);

            // 4. Определяем симптоматику по коду ошибки
            var symptoms = GetSymptomsForCode(errorCode);
            var causes = GetCausesForCode(errorCode);

            // 5. Строим шаги
            var steps = BuildDiagnosticSteps(errorCode, brand, model ?? "", diagramPath, imageUrls, enrichment);
            if (steps.Count == 0) return -1;

            // 6. Создаём руководство
            var guide = new RepairGuide
            {
                ErrorCode = errorCode,
                Brand = brand,
                ModelName = model,
                EngineCode = engineCode,
                Title = $"Диагностика {errorCode} — {brand} {(model ?? "")}".Trim(),
                Description = $"Автоматически сгенерированное руководство по диагностике и устранению ошибки {errorCode}.",
                Difficulty = EstimateDifficulty(errorCode),
                EstimatedMinutes = steps.Sum(s => s.EstimatedMinutes),
                ToolsRequired = "OBD2-сканер, мультиметр, набор ключей, отвёртки",
                PartsRequired = SuggestPartsForCode(errorCode),
                SafetyNotes = "Отключите АКБ перед работой с электрикой. Дайте двигателю остыть.",
                Symptoms = symptoms,
                PossibleCauses = causes,
                Source = "Generated",
                SourceUrl = diagramSourceUrl ?? "",
                CreatedAt = DateTime.UtcNow,
            };

            var db = await GetDbAsync();
            await db.InsertAsync(guide);

            foreach (var step in steps)
            {
                step.GuideId = guide.Id;
                await db.InsertAsync(step);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[RepairGuideService] Generated guide #{guide.Id} for {errorCode} ({brand} {model}) with {steps.Count} steps, {steps.Count(s => !string.IsNullOrEmpty(s.ImageUrl))} images.");

            return guide.Id;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RepairGuideService] GenerateGuide error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Ищет в интернете картинки по ремонту для заданного кода ошибки и авто.
    /// </summary>
    private async Task<List<string>> SearchRepairImagesAsync(string errorCode, string brand, string model, int maxResults)
    {
        var results = new List<string>();
        try
        {
            var queries = new[]
            {
                $"ремонт {errorCode} {brand} {model} схема",
                $"{errorCode} repair diagram {brand}",
                $"замена датчика {errorCode} {brand} фото",
                $"{errorCode} OBD2 fix {brand}",
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("User-Agent", "CarDiagnosticApp/1.0");

            foreach (var query in queries.Take(2)) // не больше 2 запросов в интернет
            {
                if (results.Count >= maxResults) break;
                try
                {
                    var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                    var html = await http.GetStringAsync(searchUrl);

                    // Ищем URL картинок в результатах
                    var imgMatches = System.Text.RegularExpressions.Regex.Matches(html,
                        @"https?://[^\s""'<>]+\.(?:jpg|jpeg|png|webp|gif)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    foreach (System.Text.RegularExpressions.Match m in imgMatches)
                    {
                        if (results.Count >= maxResults) break;
                        var url = m.Value;
                        // Фильтруем иконки и миниатюры
                        if (url.Contains("icon", StringComparison.OrdinalIgnoreCase)
                         || url.Contains("thumb", StringComparison.OrdinalIgnoreCase)
                         || url.Contains("avatar", StringComparison.OrdinalIgnoreCase)
                         || url.Contains("favicon", StringComparison.OrdinalIgnoreCase)
                         || url.Length < 30)
                            continue;
                        results.Add(url);
                    }
                }
                catch { /* fallback queries can fail */ }
                await Task.Delay(800);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RepairGuideService] Image search error: {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Строит диагностические шаги на основе кода ошибки, обогащая схемами и картинками.
    /// </summary>
    private static List<RepairStep> BuildDiagnosticSteps(
        string errorCode, string brand, string model,
        string? diagramPath, List<string> imageUrls, string? enrichment)
    {
        var steps = new List<RepairStep>();
        var cat = CategorizeErrorCode(errorCode);
        int stepNum = 1;
        int imgIdx = 0;

        // ── Шаг 1: Считать коды ──
        steps.Add(new RepairStep
        {
            StepNumber = stepNum++, Title = "Считать все коды ошибок",
            Instruction = $"Подключите OBD2-сканер к диагностическому разъёму {brand} {model}. Считайте все активные и сохранённые коды. Запишите freeze-frame данные (обороты, температура, скорость в момент ошибки).",
            ExpectedResult = $"Подтверждён код {errorCode}, записаны freeze-frame данные.",
            ImageHint = $"Диагностический разъём OBD2 (обычно под рулевой колонкой {brand})",
            ImageUrl = imageUrls.Count > imgIdx ? imageUrls[imgIdx++] : null,
            EstimatedMinutes = 2,
        });

        // ── Шаг 2: Визуальный осмотр ──
        var component = GetComponentForCode(errorCode);
        var location = GetComponentLocationForBrand(brand, errorCode);

        steps.Add(new RepairStep
        {
            StepNumber = stepNum++, Title = $"Визуальный осмотр {component}",
            Instruction = $"Осмотрите {component}. {location} Проверьте: целостность проводки и разъёмов, следы коррозии/окислов, механические повреждения, надёжность крепления. Отсоедините и снова подсоедините разъём — часто окислы вызывают плохой контакт.",
            ExpectedResult = $"Визуальных дефектов {component} не обнаружено ИЛИ найден дефект.",
            ImageHint = $"Расположение {component} на {brand} {model}. {location}",
            ImageUrl = diagramPath != null ? diagramPath : (imageUrls.Count > imgIdx ? imageUrls[imgIdx++] : null),
            EstimatedMinutes = 5,
        });

        // ── Шаг 3: Электрическая проверка ──
        if (cat == "sensor" || cat == "circuit")
        {
            steps.Add(new RepairStep
            {
                StepNumber = stepNum++, Title = $"Электрическая проверка цепи {component}",
                Instruction = $"Мультиметром проверьте: (1) Напряжение питания на разъёме {component} (обычно 5V или 12V — сверьте со схемой); (2) Сопротивление датчика (см. тех. характеристики); (3) Целостность проводки до ЭБУ (прозвонка). При обрыве или КЗ — восстановите проводку.",
                ExpectedResult = $"Питание в норме, сопротивление в допуске, проводка цела.",
                ImageHint = $"Схема подключения {component} на {brand} {model}",
                ImageUrl = imageUrls.Count > imgIdx ? imageUrls[imgIdx++] : null,
                WarningNotes = "Отключите АКБ перед прозвонкой цепей!",
                EstimatedMinutes = 10,
            });

            steps.Add(new RepairStep
            {
                StepNumber = stepNum++, IsDecisionPoint = true,
                DecisionQuestion = "Электрическая часть исправна?",
                Instruction = "На основе проверки шага 3: питание есть, сопротивление в норме, проводка цела.",
                ExpectedResult = "Да → замена компонента. Нет → поиск обрыва в проводке.",
                NextOnSuccess = stepNum + 1, NextOnFailure = stepNum,
                EstimatedMinutes = 1,
            });

            steps.Add(new RepairStep
            {
                StepNumber = stepNum++, Title = "Поиск обрыва/КЗ в проводке",
                Instruction = $"Прозвоните каждый провод от разъёма {component} до контактов ЭБУ. На {brand} частые места обрыва: гофра между двигателем и кузовом, около разъёмов (перетирание), места крепления хомутами. Восстановите повреждённый участок или замените жгут.",
                ExpectedResult = "Повреждение найдено и устранено.",
                ImageHint = $"Схема контактов ЭБУ {brand} {model}",
                ImageUrl = imageUrls.Count > imgIdx ? imageUrls[imgIdx++] : null,
                EstimatedMinutes = 15,
            });
        }

        // ── Шаг 4: Замена компонента ──
        var partName = GetPartNameForCode(errorCode);
        steps.Add(new RepairStep
        {
            StepNumber = stepNum++, Title = $"Замена {partName}",
            Instruction = $"Отключите АКБ. Отсоедините разъём {component}. Открутите крепёж (обычно 1-2 болта). Установите новый {partName}. Момент затяжки: согласно спецификации. Подключите разъём до щелчка. Сбросьте коды ошибок через сканер.",
            ExpectedResult = $"{partName} заменён.",
            ImageHint = $"Установка нового {partName} на {brand} {model}",
            ImageUrl = imageUrls.Count > imgIdx ? imageUrls[imgIdx++] : null,
            WarningNotes = "Используйте только оригинальные или качественные аналоги запчастей!",
            EstimatedMinutes = cat == "sensor" ? 15 : 30,
        });

        // ── Шаг 5: Сброс и проверка ──
        steps.Add(new RepairStep
        {
            StepNumber = stepNum++, Title = "Сброс адаптаций и проверка",
            Instruction = $"Сбросьте коды ошибок через OBD2-сканер (режим 04). На {brand} также выполните адаптацию: включите зажигание на 30 сек (не заводя), затем заведите и дайте поработать на ХХ 5 минут. Совершите тестовую поездку 10-15 км в разных режимах.",
            ExpectedResult = $"Ошибка {errorCode} не возвращается, автомобиль работает исправно.",
            ImageHint = "Экран OBD2-сканера: ошибок нет, статус Ready",
            EstimatedMinutes = 15,
        });

        // ── Шаг: Обогащение из знаний (если есть) ──
        if (!string.IsNullOrEmpty(enrichment) && enrichment.Length > 20)
        {
            steps.Add(new RepairStep
            {
                StepNumber = stepNum++, Title = "💡 Дополнительная информация из базы знаний",
                Instruction = enrichment[..Math.Min(enrichment.Length, 500)],
                ExpectedResult = "Учтены накопленные знания по данной ошибке.",
                EstimatedMinutes = 1,
            });
        }

        // ── Шаг: Схема узла (если найдена) ──
        if (!string.IsNullOrEmpty(diagramPath))
        {
            steps.Add(new RepairStep
            {
                StepNumber = stepNum++, Title = $"🗺️ Схема узла {component}",
                Instruction = $"На прилагаемой схеме показано расположение {component} и связанных элементов. Используйте схему для точного определения деталей и их взаимного расположения.",
                ExpectedResult = "Схема изучена, расположение компонентов понятно.",
                ImageHint = $"Схема {component} для {brand} {model}",
                ImageUrl = diagramPath,
                EstimatedMinutes = 2,
            });
        }

        return steps;
    }

    // ──────────────── Классификация кодов ────────────────

    private static string CategorizeErrorCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length < 3) return "unknown";
        var first = code[0];
        var cat = code.Length >= 4 ? code[..3] : "";

        return cat switch
        {
            "P00" or "P01" or "P02" or "P03" or "P04" or "P05" or "P06" or "P07" or "P08" or "P09" => "sensor",
            "P10" or "P11" or "P12" or "P13" or "P14" or "P15" or "P16" or "P17" or "P18" or "P19" => "circuit",
            _ => first switch { 'P' => "powertrain", 'C' => "chassis", 'B' => "body", 'U' => "network", _ => "unknown" }
        };
    }

    private static string GetComponentForCode(string code)
    {
        return code switch
        {
            "P0030" or "P0031" or "P0032" or "P0130" or "P0131" or "P0132" or "P0133" or "P0134" or "P0135" or "P0141" => "датчика кислорода (лямбда-зонда)",
            "P0100" or "P0101" or "P0102" or "P0103" => "ДМРВ (датчика массового расхода воздуха)",
            "P0110" or "P0111" or "P0112" or "P0113" or "P0115" or "P0116" or "P0117" or "P0118" => "датчика температуры",
            "P0120" or "P0121" or "P0122" or "P0123" => "датчика положения дроссельной заслонки (TPS)",
            "P0171" or "P0172" or "P0174" or "P0175" => "топливной системы (бедная/богатая смесь)",
            "P0200" or "P0201" or "P0202" or "P0203" or "P0204" or "P0261" or "P0262" or "P0264" or "P0265" or "P0267" or "P0268" => "форсунки",
            "P0300" or "P0301" or "P0302" or "P0303" or "P0304" => "системы зажигания (пропуски)",
            "P0325" or "P0326" or "P0327" or "P0328" or "P0330" or "P0331" or "P0332" or "P0333" => "датчика детонации",
            "P0335" or "P0336" or "P0337" or "P0338" or "P0339" => "датчика положения коленвала (ДПКВ)",
            "P0340" or "P0341" or "P0342" or "P0343" or "P0344" => "датчика положения распредвала (ДПРВ)",
            "P0351" or "P0352" or "P0353" or "P0354" => "катушки зажигания",
            "P0400" or "P0401" or "P0402" or "P0403" or "P0404" => "системы EGR (рециркуляции)",
            "P0420" or "P0421" or "P0430" => "каталитического нейтрализатора",
            "P0440" or "P0441" or "P0442" or "P0443" or "P0444" or "P0445" or "P0446" or "P0455" or "P0456" => "системы улавливания паров топлива (EVAP)",
            "P0500" or "P0501" or "P0502" or "P0503" => "датчика скорости (ДСА)",
            "P0600" or "P0601" or "P0602" or "P0603" or "P0604" or "P0605" or "P0606" or "P0607" => "блока управления (ЭБУ)",
            "P0700" or "P0715" or "P0720" or "P0722" or "P0730" => "трансмиссии/АКПП",
            _ => "неисправного компонента",
        };
    }

    private static string GetComponentLocationForBrand(string brand, string errorCode)
    {
        return errorCode switch
        {
            "P0130" or "P0131" or "P0132" or "P0133" or "P0134" or "P0135" => brand switch
            {
                "LADA" => "Находится на выпускном коллекторе (до катализатора).",
                "УАЗ" => "На приёмной трубе глушителя, со стороны двигателя.",
                _ => "На выпускном коллекторе, до катализатора.",
            },
            "P0335" or "P0336" => brand switch
            {
                "LADA" => "Спереди двигателя, у шкива коленвала (низ).",
                "УАЗ" => "Справа-снизу двигателя, у шкива коленвала. Часто загрязняется.",
                _ => "На блоке цилиндров, напротив шкива коленвала.",
            },
            "P0340" => brand switch
            {
                "LADA" => "На головке блока, со стороны распредвала (слева по ходу).",
                "УАЗ" => "На крышке ГБЦ, со стороны впуска.",
                _ => "На головке блока цилиндров, около шкива распредвала.",
            },
            "P0300" or "P0301" or "P0302" or "P0303" or "P0304" => "Свечи — сверху двигателя, под катушками зажигания.",
            _ => "Расположение зависит от двигателя. См. схему.",
        };
    }

    private static string GetPartNameForCode(string code)
    {
        return code switch
        {
            "P0130" or "P0131" or "P0132" or "P0133" or "P0134" or "P0135" or "P0141" => "лямбда-зонда",
            "P0100" or "P0101" or "P0102" or "P0103" => "ДМРВ",
            "P0110" or "P0111" or "P0112" or "P0113" or "P0115" or "P0116" or "P0117" or "P0118" => "датчика температуры",
            "P0120" or "P0121" or "P0122" or "P0123" => "датчика положения дроссельной заслонки",
            "P0335" or "P0336" => "датчика коленвала (ДПКВ)",
            "P0340" or "P0341" => "датчика распредвала (ДПРВ)",
            "P0351" or "P0352" or "P0353" or "P0354" => "катушки зажигания",
            "P0325" or "P0327" or "P0330" or "P0332" => "датчика детонации",
            "P0420" => "катализатора",
            "P0442" or "P0455" => "клапана адсорбера / крышки бензобака",
            _ => "неисправного компонента",
        };
    }

    private static string GetSymptomsForCode(string code)
    {
        return code switch
        {
            "P0130" => "Check Engine, повышенный расход топлива, плавают обороты",
            "P0134" => "Check Engine, богатая смесь, чёрный дым",
            "P0171" => "Check Engine, потеря мощности, провалы при разгоне, свист из-под капота",
            "P0172" => "Check Engine, чёрный дым, запах бензина, повышенный расход",
            "P0300" => "троение двигателя, вибрация, Check Engine мигает, потеря мощности",
            "P0335" => "Двигатель не заводится или глохнет, нет искры, тахометр падает в 0",
            "P0340" => "Двигатель не заводится, нет синхронизации фаз, ошибка ЭБУ",
            "P0420" => "Check Engine, снижение мощности, запах серы из выхлопа",
            "P0500" => "Спидометр не работает, ошибка АБС/ESP",
            _ => "Check Engine, возможна неровная работа двигателя",
        };
    }

    private static string GetCausesForCode(string code)
    {
        return code switch
        {
            "P0130" or "P0131" or "P0132" or "P0134" => "неисправный лямбда-зонд, обрыв проводки, подсос воздуха, некачественное топливо",
            "P0171" => "подсос воздуха после ДМРВ, забитый топливный фильтр, неисправный ДМРВ, засор форсунок",
            "P0172" => "завышенное давление топлива, льющая форсунка, неисправный ДМРВ, забитый воздушный фильтр",
            "P0300" => "свечи зажигания, катушки, высоковольтные провода, форсунки, компрессия, подсос воздуха",
            "P0335" => "неисправный ДПКВ, обрыв проводки, загрязнение, неправильный зазор",
            "P0340" => "неисправный ДПРВ, обрыв проводки, растянута цепь/ремень ГРМ, сбиты метки",
            "P0420" => "неисправный катализатор, датчик кислорода, утечка выпуска, бедная смесь, пропуски зажигания",
            _ => "неисправность датчика/исполнительного механизма, обрыв/КЗ проводки, механическое повреждение",
        };
    }

    private static string SuggestPartsForCode(string code)
    {
        return code switch
        {
            "P0130" or "P0131" or "P0132" or "P0134" => "лямбда-зонд, прокладка выпуска",
            "P0171" => "прокладка впускного коллектора, ДМРВ, топливный фильтр, очиститель дросселя",
            "P0300" => "свечи зажигания (комплект), катушки зажигания, высоковольтные провода",
            "P0335" => "датчик коленвала (ДПКВ)",
            "P0340" => "датчик распредвала (ДПРВ)",
            "P0420" => "катализатор, датчик кислорода, прокладки выпуска",
            _ => "запасная часть согласно коду ошибки",
        };
    }

    private static string EstimateDifficulty(string code)
    {
        return code switch
        {
            "P0335" or "P0340" or "P0115" or "P0118" => "easy",
            "P0171" or "P0172" or "P0130" or "P0134" or "P0300" or "P0500" => "medium",
            "P0420" or "P0601" or "P0606" => "hard",
            _ => "medium",
        };
    }

    private static List<(RepairGuide guide, List<RepairStep> steps)> BuildSeedGuides()
    {
        var result = new List<(RepairGuide, List<RepairStep>)>();

        // ════════════════════════════════════════════
        // P0300 — Пропуски зажигания (универсальный)
        // ════════════════════════════════════════════
        result.Add((
            new RepairGuide
            {
                ErrorCode = "P0300", Brand = null, ModelName = null,
                Title = "Множественные пропуски зажигания — диагностика и ремонт",
                Description = "Пошаговая диагностика причин пропусков зажигания. Подходит для большинства бензиновых двигателей.",
                Difficulty = "medium", EstimatedMinutes = 60,
                ToolsRequired = "OBD2-сканер, свечной ключ, мультиметр, компрессометр, стробоскоп",
                PartsRequired = "Свечи зажигания, катушки зажигания, высоковольтные провода, топливный фильтр",
                SafetyNotes = "Дайте двигателю остыть перед работой. Отключите АКБ перед заменой электрических компонентов.",
                Symptoms = "троение двигателя, потеря мощности, Check Engine, вибрация на холостом ходу, повышенный расход топлива",
                PossibleCauses = "свечи зажигания, катушки зажигания, высоковольтные провода, форсунки, компрессия, топливный насос, подсос воздуха, датчик положения коленвала",
                Source = "CarDiagnosticApp"
            },
            new List<RepairStep>
            {
                new() { StepNumber=1, Title="Считать коды ошибок", Instruction="Подключите OBD2-сканер и считайте все активные коды. Запишите коды пропусков по цилиндрам (P0301-P0312). Это покажет, какие цилиндры пропускают.", ExpectedResult="Определены конкретные цилиндры с пропусками.", ImageHint="Экран сканера с кодами P0300-P0312", EstimatedMinutes=3 },
                new() { StepNumber=2, Title="Визуальный осмотр свечей", Instruction="Выкрутите свечи из цилиндров с пропусками. Осмотрите на наличие: нагара, масляных отложений, трещин изолятора, эрозии электродов. Сравните с исправной свечой.", ExpectedResult="Выявлены свечи с дефектами ИЛИ свечи в норме.", ImageHint="Свеча с чёрным нагаром рядом с нормальной светло-коричневой", WarningNotes="Не выкручивайте свечи на горячем двигателе!", EstimatedMinutes=15 },
                new() { StepNumber=3, IsDecisionPoint=true, DecisionQuestion="Свечи в плохом состоянии (нагар, масло, трещины)?", Instruction="Оцените состояние всех свечей из проблемных цилиндров.", ExpectedResult="Решение о замене свечей.", NextOnSuccess=4, NextOnFailure=5, EstimatedMinutes=1 },
                new() { StepNumber=4, Title="Замена свечей зажигания", Instruction="Замените ВСЕ свечи комплектом (не только проблемные). Зазор должен соответствовать спецификации (обычно 1.0-1.1 мм). Момент затяжки: 25-30 Н·м. Нанесите тонкий слой антипригарной смазки на резьбу.", ExpectedResult="Новые свечи установлены. Запустите двигатель — пропуски должны исчезнуть. Если остались — переходите к шагу 5.", EstimatedMinutes=20 },
                new() { StepNumber=5, Title="Проверка катушек зажигания", Instruction="Поменяйте катушку проблемного цилиндра на катушку исправного. Сбросьте коды и заведите. Если код перешёл на другой цилиндр — катушка неисправна. Проверьте сопротивление первичной обмотки мультиметром: 0.3-1.0 Ом.", ExpectedResult="Выявлена неисправная катушка ИЛИ катушки исправны.", ImageHint="Мультиметр, измеряющий сопротивление катушки", WarningNotes="Высокое напряжение! Не касайтесь контактов при работающем двигателе.", EstimatedMinutes=15 },
                new() { StepNumber=6, IsDecisionPoint=true, DecisionQuestion="Катушка неисправна?", Instruction="На основе проверки шага 5.", NextOnSuccess=7, NextOnFailure=8, EstimatedMinutes=1 },
                new() { StepNumber=7, Title="Замена катушки зажигания", Instruction="Замените неисправную катушку. Рекомендуется менять комплектом если пробег >100 000 км. После замены сбросьте коды и проверьте.", ExpectedResult="Двигатель работает ровно, коды не возвращаются.", EstimatedMinutes=15 },
                new() { StepNumber=8, Title="Проверка высоковольтных проводов", Instruction="В темноте откройте капот при работающем двигателе. Ищите искрение вдоль проводов. Измерьте сопротивление каждого провода: должно быть 5-15 кОм/м. Осмотрите наконечники на предмет коррозии.", ExpectedResult="Провода в норме ИЛИ найдено повреждение.", WarningNotes="Осторожно: вращающиеся части и высокое напряжение!", EstimatedMinutes=10 },
                new() { StepNumber=9, IsDecisionPoint=true, DecisionQuestion="Провода повреждены?", Instruction="Обнаружены ли искрение, обрывы, высокое сопротивление?", NextOnSuccess=10, NextOnFailure=11, EstimatedMinutes=1 },
                new() { StepNumber=10, Title="Замена высоковольтных проводов", Instruction="Замените комплект проводов. Укладывайте строго по схеме (порядок зажигания). Не перекрещивайте провода соседних цилиндров — возможны наводки.", ExpectedResult="Провода заменены.", EstimatedMinutes=20 },
                new() { StepNumber=11, Title="Проверка компрессии", Instruction="Выкрутите все свечи. Вкрутите компрессометр в первый цилиндр. Крутите стартером 5-7 оборотов (педаль газа в пол). Запишите значение. Повторите для всех цилиндров. Разброс >15% = проблема.", ExpectedResult="Компрессия в норме (≥10 бар, разброс <15%) ИЛИ низкая компрессия.", WarningNotes="Отключите зажигание и топливный насос перед проверкой!", EstimatedMinutes=30 },
                new() { StepNumber=12, IsDecisionPoint=true, DecisionQuestion="Компрессия в норме (разброс < 15%)?", Instruction="Сравните результаты с допусками.", NextOnSuccess=13, NextOnFailure=14, EstimatedMinutes=1 },
                new() { StepNumber=13, Title="Проверка топливной системы", Instruction="Измерьте давление топлива манометром (должно быть 3-4 бар). Проверьте форсунки: снимите разъёмы по очереди на холостом ходу — падение оборотов = форсунка работает. Без изменений = форсунка/цепь неисправна.", ExpectedResult="Давление топлива в норме. Форсунки работают.", EstimatedMinutes=20 },
                new() { StepNumber=14, Title="Механическая проблема", Instruction="Низкая компрессия указывает на: прогоревший клапан, износ поршневых колец, повреждение прокладки ГБЦ. Требуется углублённая диагностика (эндоскоп, пневмотест). Рекомендуется обратиться в сервис.", ExpectedResult="Определён характер механической проблемы.", EstimatedMinutes=5 },
                new() { StepNumber=15, Title="Завершение диагностики", Instruction="Если все проверки пройдены, а пропуски сохраняются — проверьте: датчик положения коленвала (ДПКВ), датчик распредвала (ДПРВ), подсос воздуха после ДМРВ, клапан EGR (заклинил открытым), метки ГРМ.", ExpectedResult="Выявлена и устранена причина пропусков.", EstimatedMinutes=10 },
            }
        ));

        // ════════════════════════════════════════════
        // P0420 — Низкая эффективность катализатора
        // ════════════════════════════════════════════
        result.Add((
            new RepairGuide
            {
                ErrorCode = "P0420", Brand = null, ModelName = null,
                Title = "P0420 — низкая эффективность катализатора (банк 1)",
                Description = "Диагностика каталитического нейтрализатора, датчиков кислорода и выпускной системы.",
                Difficulty = "medium", EstimatedMinutes = 45,
                ToolsRequired = "OBD2-сканер с графиком, мультиметр, набор ключей",
                PartsRequired = "Датчик кислорода (лямбда-зонд), катализатор, прокладки выпуска",
                SafetyNotes = "Работайте ТОЛЬКО на холодном двигателе — выпускная система сильно нагревается. Используйте перчатки.",
                Symptoms = "Check Engine, снижение мощности, повышенный расход топлива, запах серы/тухлых яиц из выхлопа",
                PossibleCauses = "неисправный катализатор, датчик кислорода, утечка выпуска, бедная смесь, пропуски зажигания, некачественное топливо",
                Source = "CarDiagnosticApp"
            },
            new List<RepairStep>
            {
                new() { StepNumber=1, Title="Считать данные O2-датчиков", Instruction="Подключите сканер и выведите график напряжения датчиков O2. Датчик 1 (до катализатора) должен колебаться 0.1-0.9V с частотой ~1 Гц. Датчик 2 (после катализатора) должен быть стабильным ~0.5-0.7V.", ExpectedResult="Графики датчиков записаны для анализа.", ImageHint="График O2: верхний колеблется, нижний почти ровный", EstimatedMinutes=3 },
                new() { StepNumber=2, IsDecisionPoint=true, DecisionQuestion="Датчик 2 (после катализатора) колеблется как датчик 1?", Instruction="Если оба датчика показывают одинаковые колебания — катализатор не работает.", NextOnSuccess=3, NextOnFailure=7, EstimatedMinutes=1 },
                new() { StepNumber=3, Title="Проверка утечек выпуска", Instruction="Осмотрите выпускную систему от коллектора до катализатора. Ищите: чёрный нагар вокруг прокладок, трещины, свищи. На заведённом двигателе слушайте шипение/свист. Особое внимание — гофра и прокладка коллектора.", ExpectedResult="Утечки найдены и локализованы ИЛИ утечек нет.", WarningNotes="Двигатель должен быть холодным при осмотре!", EstimatedMinutes=10 },
                new() { StepNumber=4, IsDecisionPoint=true, DecisionQuestion="Найдены утечки выпуска?", Instruction="Утечки до катализатора или в районе датчиков искажают показания.", NextOnSuccess=5, NextOnFailure=6, EstimatedMinutes=1 },
                new() { StepNumber=5, Title="Устранение утечек", Instruction="Замените прокладки, подтяните болты, заварите трещины. После ремонта сбросьте коды и проедьте ~50 км — если P0420 не возвращается, проблема решена.", ExpectedResult="Утечки устранены.", EstimatedMinutes=30 },
                new() { StepNumber=6, Title="Диагностика катализатора", Instruction="Измерьте температуру ДО и ПОСЛЕ катализатора ИК-термометром. Исправный катализатор: выход на 30-50°C горячее входа. Проверьте противодавление: выкрутите датчик O2 до катализатора, вкрутите манометр — должно быть <0.15 бар на 2500 об/мин.", ExpectedResult="Катализатор исправен (темп. выше на выходе) ИЛИ неисправен.", WarningNotes="Осторожно: очень горячие поверхности!", EstimatedMinutes=15 },
                new() { StepNumber=7, Title="Проверка топливной коррекции", Instruction="Считайте параметры STFT и LTFT. Норма: ±10%. Сильно положительная (>+15%) = бедная смесь (подсос воздуха). Сильно отрицательная (<-15%) = богатая смесь (форсунки льют, давление топлива завышено).", ExpectedResult="Коррекция в норме ИЛИ выявлено отклонение.", EstimatedMinutes=3 },
                new() { StepNumber=8, Title="Конечная рекомендация", Instruction="Если катализатор неисправен: замена катализатора или установка пламегасителя + обманка (для off-road). После замены сбросьте адаптации ЭБУ. Если катализатор исправен, а код возвращается — обновите прошивку ЭБУ (порог срабатывания).", ExpectedResult="План ремонта определён.", EstimatedMinutes=3 },
            }
        ));

        // ════════════════════════════════════════════
        // LADA — P0171/P0172 — бедная/богатая смесь
        // ════════════════════════════════════════════
        result.Add((
            new RepairGuide
            {
                ErrorCode = "P0171", Brand = "LADA", ModelName = null,
                Title = "LADA: P0171 — бедная смесь (банк 1)",
                Description = "Диагностика бедной смеси на двигателях LADA. Характерная проблема для ВАЗ-21129 (1.6 16V) и ВАЗ-21179 (1.8 16V).",
                Difficulty = "easy", EstimatedMinutes = 30,
                ToolsRequired = "OBD2-сканер, отвёртка, карбклинер, WD-40",
                PartsRequired = "прокладка впускного коллектора, ДМРВ, шланг вакуумного усилителя, заглушки впуска",
                SafetyNotes = "Не распыляйте карбклинер на горячий двигатель и вблизи источников искры.",
                Symptoms = "Check Engine, плавают обороты, провалы при разгоне, свист из-под капота",
                PossibleCauses = "подсос воздуха после ДМРВ, трещина впускного коллектора, неисправный ДМРВ, забитый топливный фильтр, засор форсунок",
                Source = "CarDiagnosticApp"
            },
            new List<RepairStep>
            {
                new() { StepNumber=1, Title="Считать STFT (краткосрочную коррекцию)", Instruction="Подключите OBD2-сканер. Найдите параметр STFT (Short Term Fuel Trim). На холостом ходу значение > +15% = бедная смесь. Поднимите обороты до 2500: если коррекция снижается к 0 — проблема на холостом ходу (подсос).", ExpectedResult="STFT > 15% на ХХ, снижается на оборотах → подсос.", EstimatedMinutes=2 },
                new() { StepNumber=2, IsDecisionPoint=true, DecisionQuestion="STFT на холостом > +15%, на 2500 об/мин снижается к норме?", NextOnSuccess=3, NextOnFailure=7, EstimatedMinutes=1 },
                new() { StepNumber=3, Title="Поиск подсоса воздуха", Instruction="Заведите двигатель. Распылите карбклинер/WD-40 поочерёдно: стык ДМРВ-патрубок, патрубок-дроссель, прокладка впускного коллектора, вакуумные шланги, штуцер вакуумного усилителя. При попадании на подсос — обороты изменятся (вырастут или упадут).", ExpectedResult="Найдено место подсоса ИЛИ подсоса нет.", WarningNotes="Осторожно: карбклинер легковоспламеняем!", EstimatedMinutes=10 },
                new() { StepNumber=4, IsDecisionPoint=true, DecisionQuestion="Найдено место подсоса?", NextOnSuccess=5, NextOnFailure=6, EstimatedMinutes=1 },
                new() { StepNumber=5, Title="Устранение подсоса", Instruction="Если подсос через прокладку — замена прокладки с герметиком. Трещина шланга — замена шланга. Ослабленный хомут — подтяжка. После ремонта сбросьте адаптации ЭБУ.", ExpectedResult="Подсос устранён.", EstimatedMinutes=20 },
                new() { StepNumber=6, Title="Проверка дроссельной заслонки и РХХ", Instruction="Снимите патрубок, осмотрите заслонку. При сильном загрязнении — очистите очистителем дросселя. На LADA с электронной заслонкой — проверьте адаптацию (включить зажигание на 30 сек, не заводя).", ExpectedResult="Заслонка чистая.", EstimatedMinutes=10 },
                new() { StepNumber=7, Title="Проверка ДМРВ", Instruction="Считайте показания ДМРВ: на ХХ ~8-12 кг/ч (для 1.6) или ~10-14 кг/ч (для 1.8). Отключите ДМРВ — если двигатель заработал лучше, ДМРВ неисправен. Проверьте чистоту чувствительного элемента (очиститель ДМРВ).", ExpectedResult="ДМРВ исправен ИЛИ неисправен.", EstimatedMinutes=10 },
                new() { StepNumber=8, Title="Проверка топливной системы", Instruction="Проверьте давление топлива (3.8-4.0 бар для LADA). Замените топливный фильтр если >30 000 км. Проверьте форсунки: на слух (цоканье) и снимите рампу для визуального осмотра распыла.", ExpectedResult="Давление в норме, форсунки работают.", EstimatedMinutes=15 },
            }
        ));

        // ════════════════════════════════════════════
        // УАЗ — P0335/P0336 — ДПКВ
        // ════════════════════════════════════════════
        result.Add((
            new RepairGuide
            {
                ErrorCode = "P0335", Brand = "УАЗ", ModelName = null,
                Title = "УАЗ: P0335/P0336 — датчик положения коленвала",
                Description = "Распространённая проблема на УАЗ Patriot/Hunter с двигателями ЗМЗ-409. Датчик расположен неудобно и часто повреждается.",
                Difficulty = "easy", EstimatedMinutes = 20,
                ToolsRequired = "ключ на 10, мультиметр, OBD2-сканер",
                PartsRequired = "ДПКВ (ЗМЗ-409), прокладка",
                SafetyNotes = "Отключите АКБ перед заменой датчика.",
                Symptoms = "двигатель не заводится или глохнет на ходу, Check Engine, нет искры, тахометр падает в 0",
                PossibleCauses = "неисправный ДПКВ, обрыв проводки, загрязнение датчика, неправильный зазор между датчиком и шкивом",
                Source = "CarDiagnosticApp"
            },
            new List<RepairStep>
            {
                new() { StepNumber=1, Title="Проверка ошибок", Instruction="Считайте коды OBD. Если только P0335 — проблема в цепи ДПКВ. Если ещё и P0336 — проблема в сигнале (грязный датчик, зазор, проводка).", ExpectedResult="Коды считаны.", EstimatedMinutes=2 },
                new() { StepNumber=2, Title="Осмотр датчика и проводки", Instruction="На УАЗ Patriot ДПКВ находится спереди двигателя, у шкива коленвала (нижняя часть). Осмотрите разъём: нет ли окислов, влаги, масла. Проверьте целостность провода до ЭБУ.", ExpectedResult="Осмотр выполнен.", ImageHint="Расположение ДПКВ на ЗМЗ-409", EstimatedMinutes=5 },
                new() { StepNumber=3, Title="Проверка зазора", Instruction="Зазор между датчиком и зубьями шкива должен быть 0.5-1.5 мм. Если зазор увеличен (грязь/смещение) — датчик не видит метку. Очистите посадочное место и шкив.", ExpectedResult="Зазор в норме.", EstimatedMinutes=5 },
                new() { StepNumber=4, Title="Проверка сопротивления датчика", Instruction="Отсоедините разъём ДПКВ. Измерьте сопротивление между контактами датчика: исправный = 500-700 Ом. Обрыв или короткое замыкание = замена.", ExpectedResult="Сопротивление в норме (500-700 Ом).", EstimatedMinutes=3 },
                new() { StepNumber=5, IsDecisionPoint=true, DecisionQuestion="Сопротивление в норме?", NextOnSuccess=6, NextOnFailure=7, EstimatedMinutes=1 },
                new() { StepNumber=6, Title="Проверка проводки до ЭБУ", Instruction="Проверьте целостность провода от разъёма датчика до ЭБУ. На ЗМЗ-409 ЭБУ (Микас 12.3) находится под капотом справа. Контакты ДПКВ: 48 и 49 разъёма ЭБУ. Прозвоните цепь мультиметром.", ExpectedResult="Проводка целая.", EstimatedMinutes=10 },
                new() { StepNumber=7, Title="Замена ДПКВ", Instruction="Купите датчик ЗМЗ-409 (каталожный номер 406.3847010). Открутите 1 болт на 10. Установите новый датчик, проверьте зазор, затяните. Подключите разъём. Сбросьте коды.", ExpectedResult="Датчик заменён, двигатель заводится.", EstimatedMinutes=10 },
            }
        ));

        return result;
    }
}
