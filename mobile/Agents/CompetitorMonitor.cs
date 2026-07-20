using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CarDiagnosticApp.Agents;

/// <summary>
/// Агент мониторинга конкурентов.
/// Периодически проверяет конкурентов на изменения, ищет новых,
/// генерирует алерты и отчёты.
/// </summary>
public class CompetitorMonitor
{
    // ──────────────────────────────────────────────
    // Singleton
    // ──────────────────────────────────────────────

    private static CompetitorMonitor? _instance;
    public static CompetitorMonitor Instance => _instance ??= new CompetitorMonitor();

    private CompetitorMonitor() { }

    // ──────────────────────────────────────────────
    // Состояние
    // ──────────────────────────────────────────────

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private DateTime _lastCheckAt = DateTime.MinValue;
    private readonly CompetitorService _svc = new();

    /// <summary>Интервал проверки (по умолчанию 24 часа).</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Максимум конкурентов для проверки за цикл.</summary>
    public int MaxPerCycle { get; set; } = 5;

    /// <summary>Порог изменения рейтинга для алерта.</summary>
    public double RatingAlertThreshold { get; set; } = 0.2;

    /// <summary>Событие: обнаружены изменения.</summary>
    public event Action<string>? OnAlert;

    // ──────────────────────────────────────────────
    // Жизненный цикл
    // ──────────────────────────────────────────────

    /// <summary>
    /// Запускает фоновый мониторинг. Безопасно для многократного вызова.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
            return;

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _ = Task.Run(() => RunLoopAsync(_cts.Token));

        Debug.WriteLine("[CompetitorMonitor] Started.");
    }

    /// <summary>
    /// Останавливает фоновый мониторинг.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
        Debug.WriteLine("[CompetitorMonitor] Stopped.");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        // Первый запуск: seed + проверка через 30 секунд после старта
        await Task.Delay(TimeSpan.FromSeconds(30), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunCheckAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CompetitorMonitor] Check error: {ex.Message}");
            }

            try
            {
                await Task.Delay(CheckInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // ──────────────────────────────────────────────
    // Основной цикл проверки
    // ──────────────────────────────────────────────

    /// <summary>
    /// Принудительный запуск проверки (из UI/админки).
    /// Возвращает сводку найденных изменений.
    /// </summary>
    public async Task<string> ForceCheckAsync()
    {
        try
        {
            return await RunCheckAsync();
        }
        catch (Exception ex)
        {
            return $"❌ Ошибка мониторинга: {ex.Message}";
        }
    }

    private async Task<string> RunCheckAsync()
    {
        var alerts = new List<string>();
        int totalChanges = 0;

        // 1. Seed при первом запуске
        int seeded = await _svc.SeedDefaultCompetitorsAsync();
        if (seeded > 0)
        {
            alerts.Add($"📥 Загружено {seeded} конкурентов в базу");
            Debug.WriteLine($"[CompetitorMonitor] Seeded {seeded} competitors");
        }

        // 2. Обновление данных из магазинов
        int webChanges = await _svc.RefreshAllFromWebAsync();
        totalChanges += webChanges;
        if (webChanges > 0)
            alerts.Add($"🔄 Обнаружено {webChanges} изменений у конкурентов");

        // 3. Поиск новых конкурентов
        var newComps = await DiscoverNewCompetitorsAsync();
        if (newComps > 0)
            alerts.Add($"🆕 Найдено {newComps} новых конкурентов");

        // 4. Анализ значимых изменений (алерты)
        var significantChanges = await GetSignificantChangesAsync();
        foreach (var sc in significantChanges)
            alerts.Add($"⚠️ {sc}");

        _lastCheckAt = DateTime.UtcNow;

        // 5. Сравнительный анализ (функции/цены/обновления)
        var comparisonSummary = await BuildComparisonSummaryAsync();
        if (!string.IsNullOrEmpty(comparisonSummary))
            alerts.Add(comparisonSummary);

        // 6. Сохраняем отчёт
        await SaveReportAsync();

        // 7. Отправляем уведомление если есть значимые изменения
        if (alerts.Count > 0)
        {
            var msg = string.Join("\n", alerts);
            OnAlert?.Invoke(msg);
        }

        var result = alerts.Count == 0
            ? $"✅ Мониторинг выполнен. Конкурентов: {await _svc.CountAsync()}, изменений: нет."
            : string.Join("\n", alerts.Prepend($"📊 Мониторинг завершён. Изменений: {totalChanges}"));

        Debug.WriteLine($"[CompetitorMonitor] {result.Replace('\n', ' ')}");
        return result;
    }

    // ──────────────────────────────────────────────
    // Обнаружение новых конкурентов
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ищет новых конкурентов через DuckDuckGo и добавляет в БД.
    /// Возвращает количество найденных.
    /// </summary>
    private async Task<int> DiscoverNewCompetitorsAsync()
    {
        int found = 0;
        var existing = await _svc.GetAllAsync();
        var existingNames = existing.Select(c => c.Name.ToLowerInvariant()).ToHashSet();

        // Поисковые запросы на русском и английском
        var queries = new[]
        {
            "OBD2 диагностика приложение Android 2025 2026 рейтинг",
            "лучшие OBD2 приложения автомобильная диагностика сравнение",
            "OBD2 scanner app Android iOS best rated 2025 2026",
            "car diagnostic app ELM327 Bluetooth comparison",
        };

        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.Timeout = TimeSpan.FromSeconds(15);

        foreach (var query in queries.Take(2)) // не больше 2 запросов
        {
            try
            {
                var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                var html = await client.GetStringAsync(url);

                // Ищем упоминания приложений
                var appMentions = Regex.Matches(html,
                    @"(?:OBD|ELM|Car\s*Scanner|Torque|Diagnos|Diagnostic)[^<]{10,80}",
                    RegexOptions.IgnoreCase);

                foreach (Match m in appMentions.Take(5))
                {
                    var text = m.Value.Trim();
                    if (string.IsNullOrEmpty(text) || text.Length < 5)
                        continue;

                    // Грубый фильтр: не добавлять дубликаты
                    var nameCandidate = ExtractAppName(text);
                    if (string.IsNullOrEmpty(nameCandidate) ||
                        existingNames.Contains(nameCandidate.ToLowerInvariant()))
                        continue;

                    var comp = new Competitor
                    {
                        Name = nameCandidate,
                        Notes = $"Авто-обнаружен: {DateTime.UtcNow:yyyy-MM-dd}. Источник: DDG query '{query}'.",
                        AddedAt = DateTime.UtcNow,
                    };

                    await _svc.SaveAsync(comp);
                    existingNames.Add(nameCandidate.ToLowerInvariant());
                    found++;
                }

                await Task.Delay(1000); // вежливая пауза между запросами
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CompetitorMonitor] Discovery query failed: {ex.Message}");
            }
        }

        return found;
    }

    /// <summary>
    /// Грубое извлечение названия приложения из текста.
    /// </summary>
    private static string ExtractAppName(string text)
    {
        // Ищем паттерны: "Car Scanner ELM OBD2", "Torque Pro", "OBD Fusion" etc.
        var patterns = new[]
        {
            @"(?:Car\s*Scanner\s*(?:ELM\s*)?OBD2?)",
            @"(?:Torque\s*(?:Pro|Lite))",
            @"(?:OBD\s*(?:Fusion|Auto\s*Doctor|Link|Car\s*Doctor|EasyDiag|Mate|AI))",
            @"(?:inCarDoc|DashCommand|EOBD\s*Facile|Carista|Piston)",
            @"(?:Engine\s*Link|ScanMaster|OBD\s*Mary|MotorData)",
            @"(?:Автодиагност|Автосканер|Автодок|Диагност)",
            @"(?:OpenDiag|OBDAI|Carly|OBDeleven)",
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
            if (m.Success)
                return m.Value.Trim();
        }

        // Если нет точного совпадения — не добавляем
        return "";
    }

    // ──────────────────────────────────────────────
    // Значимые изменения
    // ──────────────────────────────────────────────

    /// <summary>
    /// Возвращает список значимых изменений, требующих внимания.
    /// </summary>
    private async Task<List<string>> GetSignificantChangesAsync()
    {
        var significant = new List<string>();

        // Берём изменения с последней проверки
        var changes = await _svc.GetAllChangesAsync(50);
        changes = changes
            .Where(c => c.DetectedAt > _lastCheckAt && _lastCheckAt != DateTime.MinValue)
            .ToList();

        var competitors = await _svc.GetAllAsync();

        foreach (var ch in changes)
        {
            var comp = competitors.FirstOrDefault(c => c.Id == ch.CompetitorId);
            var name = comp?.Name ?? $"ID:{ch.CompetitorId}";

            switch (ch.ChangeType)
            {
                case "rating":
                    if (double.TryParse(ch.OldValue, out var oldR) &&
                        double.TryParse(ch.NewValue, out var newR) &&
                        Math.Abs(newR - oldR) >= RatingAlertThreshold)
                    {
                        var direction = newR > oldR ? "вырос" : "упал";
                        significant.Add($"{name}: рейтинг {direction} с {oldR:F1} до {newR:F1}");
                    }
                    break;

                case "version":
                    significant.Add($"{name}: новая версия {ch.NewValue} (была {ch.OldValue})");
                    break;

                case "pricing":
                    significant.Add($"{name}: изменена цена — {ch.OldValue} → {ch.NewValue}");
                    break;

                case "price":
                    significant.Add($"{name}: цена изменена на ${ch.NewValue} (была ${ch.OldValue})");
                    break;

                case "features":
                    significant.Add($"{name}: обновлён список функций");
                    break;

                case "reviews":
                    significant.Add($"{name}: количество отзывов изменилось ({ch.OldValue} → {ch.NewValue})");
                    break;
            }
        }

        return significant;
    }

    // ──────────────────────────────────────────────
    // Отчёт
    // ──────────────────────────────────────────────

    // ──────────────────────────────────────────────
    // Сравнение функций, цен, обновлений
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ключевые функции CarDiagnosticApp (для сравнения с конкурентами).
    /// </summary>
    private static readonly string[] OurFeatures =
    [
        "AI-диагностика ошибок (GPT)",
        "Схемы узлов с авто-поиском",
        "Чтение текущих/исторических/подтверждённых DTC",
        "Сброс ошибок (Mode 04)",
        "Живые данные (Live Data)",
        "Графики параметров",
        "Стоп-кадр (Freeze Frame)",
        "История диагностики с самообучением",
        "Облачная синхронизация",
        "Русский язык (полный интерфейс)",
        "Авто-поиск решений в интернете",
        "Работа без интернета (офлайн)",
        "Поддержка Android + Windows",
        "Админ-панель для управления данными",
    ];

    /// <summary>
    /// Сравнивает функции нашего приложения с конкурентами.
    /// Возвращает текстовую матрицу сравнения.
    /// </summary>
    public async Task<string> CompareFeaturesAsync()
    {
        var competitors = await _svc.GetAllAsync();
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine("  СРАВНЕНИЕ ФУНКЦИЙ");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();
        sb.AppendLine("Наши функции:");
        for (int i = 0; i < OurFeatures.Length; i++)
            sb.AppendLine($"  {i + 1,2}. {OurFeatures[i]}");
        sb.AppendLine();

        // Анализ по каждому конкуренту
        sb.AppendLine("── Сравнение с конкурентами ──");
        sb.AppendLine();

        foreach (var comp in competitors.OrderBy(c => c.Name))
        {
            var compFeatures = ParseFeatureList(comp.KeyFeatures);
            var overlap = OurFeatures.Count(f => compFeatures.Any(cf =>
                cf.Contains(f.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase) ||
                f.Contains(cf.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase)));

            var missingFromUs = compFeatures.Where(cf =>
                !OurFeatures.Any(f =>
                    f.StartsWith(cf.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase) ||
                    cf.StartsWith(f.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var missingFromThem = OurFeatures.Where(f =>
                !compFeatures.Any(cf =>
                    cf.Contains(f.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase) ||
                    f.Contains(cf.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var overlapPct = OurFeatures.Length > 0 ? (double)overlap / OurFeatures.Length * 100 : 0;
            var icon = overlapPct >= 50 ? "🟢" : overlapPct >= 25 ? "🟡" : "🔴";

            sb.AppendLine($"  {icon} {comp.Name}");
            sb.AppendLine($"     Совпадение функций: {overlap}/{OurFeatures.Length} ({overlapPct:F0}%)");
            sb.AppendLine($"     Их фичи ({compFeatures.Count}): {string.Join(", ", compFeatures)}");

            if (missingFromThem.Count > 0)
                sb.AppendLine($"     ★ Наше преимущество ({missingFromThem.Count}): {string.Join(", ", missingFromThem.Take(5))}");

            if (missingFromUs.Count > 0)
                sb.AppendLine($"     ⚠ У них есть, у нас нет ({missingFromUs.Count}): {string.Join(", ", missingFromUs.Take(5))}");

            sb.AppendLine();
        }

        // Сводка: фичи которых нет ни у кого (уникальные для нас)
        var allCompFeatures = competitors
            .SelectMany(c => ParseFeatureList(c.KeyFeatures))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uniqueToUs = OurFeatures.Where(f =>
            !allCompFeatures.Any(cf =>
                cf.Contains(f.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase) ||
                f.Contains(cf.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (uniqueToUs.Count > 0)
        {
            sb.AppendLine("🏆 УНИКАЛЬНЫЕ ДЛЯ НАС ФУНКЦИИ:");
            foreach (var f in uniqueToUs)
                sb.AppendLine($"   ✅ {f}");
            sb.AppendLine();
        }

        // Фичи конкурентов, которых у нас нет (возможности для развития)
        var allOurNormalized = OurFeatures
            .Select(f => f.Split('(')[0].Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var opportunities = competitors
            .SelectMany(c => ParseFeatureList(c.KeyFeatures))
            .Where(cf => !allOurNormalized.Any(o =>
                o.Contains(cf.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase) ||
                cf.Contains(o.Split('(')[0].Trim(), StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (opportunities.Count > 0)
        {
            sb.AppendLine("💡 ВОЗМОЖНОСТИ ДЛЯ РАЗВИТИЯ (есть у конкурентов):");
            foreach (var o in opportunities)
                sb.AppendLine($"   📋 {o}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Сравнивает цены всех конкурентов.
    /// Возвращает таблицу сравнения цен.
    /// </summary>
    public async Task<string> ComparePricingAsync()
    {
        var competitors = await _svc.GetAllAsync();
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine("  СРАВНЕНИЕ ЦЕН");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        // Наша цена
        sb.AppendLine("Наша цена: freemium (базовые функции бесплатно, AI-анализ — подписка)");
        sb.AppendLine();

        // Таблица
        sb.AppendLine($"{"Приложение",-25} {"Модель",-15} {"Цена USD",-10} {"AI",-5} {"RU",-5}");
        sb.AppendLine(new string('─', 65));

        foreach (var comp in competitors.OrderBy(c => c.PriceUsd).ThenBy(c => c.Name))
        {
            var pricingLabel = comp.Pricing switch
            {
                "free" => "Бесплатно",
                "freemium" => "Freemium",
                "paid" => "Платно",
                "subscription" => "Подписка/год",
                _ => comp.Pricing
            };

            var priceStr = comp.PriceUsd == 0 ? "$0" : $"${comp.PriceUsd:F2}";
            sb.AppendLine($"{comp.Name,-25} {pricingLabel,-15} {priceStr,-10} {(comp.HasAiFeatures ? "✓" : "✗"),-5} {(comp.HasRussianLanguage ? "✓" : "✗"),-5}");
        }

        sb.AppendLine();

        // Сводка
        sb.AppendLine("── Сводка по ценам ──");
        var free = competitors.Where(c => c.Pricing == "free" || c.PriceUsd == 0).ToList();
        var freemium = competitors.Where(c => c.Pricing == "freemium" && c.PriceUsd > 0).ToList();
        var paid = competitors.Where(c => c.Pricing == "paid").ToList();
        var sub = competitors.Where(c => c.Pricing == "subscription").ToList();

        sb.AppendLine($"  Полностью бесплатных: {free.Count} — {string.Join(", ", free.Select(c => c.Name))}");
        sb.AppendLine($"  Freemium (бесплатно + Premium): {freemium.Count}");
        sb.AppendLine($"  Единоразовая оплата: {paid.Count} (среднее ${paid.Select(c => c.PriceUsd).DefaultIfEmpty(0).Average():F2})");
        sb.AppendLine($"  Подписка: {sub.Count} (среднее ${sub.Select(c => c.PriceUsd).DefaultIfEmpty(0).Average():F2}/год)");
        sb.AppendLine();

        // Конкурентное позиционирование
        var avgPrice = competitors.Where(c => c.PriceUsd > 0).Select(c => c.PriceUsd).DefaultIfEmpty(0).Average();
        sb.AppendLine($"  Средняя цена рынка: ${avgPrice:F2}");
        sb.AppendLine($"  Наша позиция: {(avgPrice > 5 ? "ниже рынка ✅" : "на уровне рынка")}");

        return sb.ToString();
    }

    /// <summary>
    /// Сравнивает частоту и даты обновлений конкурентов.
    /// Возвращает сводку по обновлениям.
    /// </summary>
    public async Task<string> CompareUpdatesAsync()
    {
        var competitors = await _svc.GetAllAsync();
        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine("  СРАВНЕНИЕ ОБНОВЛЕНИЙ");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        sb.AppendLine($"{"Приложение",-25} {"Версия",-12} {"Обновлено",-12} {"Статус"}");
        sb.AppendLine(new string('─', 65));

        var now = DateTime.UtcNow;

        foreach (var comp in competitors.OrderBy(c => c.Name))
        {
            var version = !string.IsNullOrEmpty(comp.LatestVersion) ? comp.LatestVersion : "?";
            var lastDate = comp.LastVersionDate;
            var dateStr = lastDate?.ToString("yyyy-MM-dd") ?? "нет данных";

            string status;
            if (lastDate == null)
                status = "⚪ неизвестно";
            else if ((now - lastDate.Value).TotalDays <= 30)
                status = "🟢 активно";
            else if ((now - lastDate.Value).TotalDays <= 180)
                status = "🟡 редко";
            else
                status = $"🔴 заброшено ({(now - lastDate.Value).TotalDays:F0} дн.)";

            sb.AppendLine($"{comp.Name,-25} v{version,-11} {dateStr,-12} {status}");
        }

        sb.AppendLine();

        // Статистика
        var withDates = competitors.Where(c => c.LastVersionDate.HasValue).ToList();
        if (withDates.Count > 0)
        {
            var activeCount = withDates.Count(c => (now - c.LastVersionDate!.Value).TotalDays <= 180);
            var abandonedCount = withDates.Count - activeCount;

            sb.AppendLine("── Статистика обновлений ──");
            sb.AppendLine($"  Активно обновляются (≤6 мес.): {activeCount}");
            sb.AppendLine($"  Заброшены (>6 мес.): {abandonedCount}");
            sb.AppendLine($"  Без данных: {competitors.Count - withDates.Count}");

            // Версионная история из competitor_history
            var allChanges = await _svc.GetAllChangesAsync(200);
            var versionChanges = allChanges.Where(c => c.ChangeType == "version").ToList();
            if (versionChanges.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("── История версий ──");
                foreach (var vc in versionChanges.OrderByDescending(c => c.DetectedAt).Take(15))
                {
                    var comp = competitors.FirstOrDefault(c => c.Id == vc.CompetitorId);
                    sb.AppendLine($"  {vc.DetectedAt:yyyy-MM-dd} | {(comp?.Name ?? "?"),-20} | {vc.OldValue} → {vc.NewValue}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Генерирует полный сравнительный отчёт (функции + цены + обновления).
    /// </summary>
    public async Task<string> GenerateComparisonReportAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine(await CompareFeaturesAsync());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(await ComparePricingAsync());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(await CompareUpdatesAsync());
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(await GenerateRecommendationsAsync());
        return sb.ToString();
    }

    // ──────────────────────────────────────────────
    // Генерация рекомендаций по улучшению
    // ──────────────────────────────────────────────

    /// <summary>
    /// Анализирует все сравнения и генерирует приоритезированные рекомендации.
    /// </summary>
    public async Task<string> GenerateRecommendationsAsync()
    {
        var competitors = await _svc.GetAllAsync();
        if (competitors.Count == 0)
            return "Нет данных для анализа.";

        var sb = new StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine("  РЕКОМЕНДАЦИИ ПО УЛУЧШЕНИЮ");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        var allOurNormalized = OurFeatures
            .Select(f => NormalizeFeature(f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Собираем все фичи конкурентов и считаем частоту
        var compFeatureFreq = new Dictionary<string, (int Count, List<string> Competitors)>(StringComparer.OrdinalIgnoreCase);

        foreach (var comp in competitors)
        {
            var feats = ParseFeatureList(comp.KeyFeatures);
            foreach (var feat in feats)
            {
                var norm = NormalizeFeature(feat);
                if (!compFeatureFreq.ContainsKey(norm))
                    compFeatureFreq[norm] = (0, new());

                var (count, comps) = compFeatureFreq[norm];
                compFeatureFreq[norm] = (count + 1, comps.Append(comp.Name).ToList());
            }
        }

        // Фильтруем: фичи конкурентов, которых у нас нет
        var gapsWeDontHave = compFeatureFreq
            .Where(kvp => !allOurNormalized.Contains(kvp.Key))
            .OrderByDescending(kvp => kvp.Value.Count)
            .ToList();

        // ── 🔴 Срочные рекомендации ──
        sb.AppendLine("🔴 СРОЧНЫЕ (конкуренты с AI, которых у нас нет):");
        sb.AppendLine();

        var aiCompetitors = competitors.Where(c => c.HasAiFeatures).ToList();
        var aiGaps = new List<(string Feature, List<string> Comps)>();

        foreach (var aiComp in aiCompetitors)
        {
            var feats = ParseFeatureList(aiComp.KeyFeatures);
            foreach (var feat in feats)
            {
                var norm = NormalizeFeature(feat);
                if (!allOurNormalized.Contains(norm))
                {
                    var existing = aiGaps.FirstOrDefault(g => g.Feature == feat);
                    if (existing == default)
                        aiGaps.Add((feat, new List<string> { aiComp.Name }));
                    else
                        existing.Comps.Add(aiComp.Name);
                }
            }
        }

        if (aiGaps.Count > 0)
        {
            foreach (var (feat, comps) in aiGaps
                .DistinctBy(g => NormalizeFeature(g.Feature))
                .Take(5))
            {
                sb.AppendLine($"   ❗ Добавить \"{feat}\" — есть у AI-конкурентов: {string.Join(", ", comps)}");
            }
        }
        else
        {
            sb.AppendLine("   ✅ Срочных пробелов не выявлено.");
        }

        sb.AppendLine();

        // ── 🟡 Среднесрочные рекомендации (3+ конкурента имеют) ──
        sb.AppendLine("🟡 СРЕДНЕСРОЧНЫЕ (есть у 3+ конкурентов):");
        sb.AppendLine();

        var popularGaps = gapsWeDontHave.Where(g => g.Value.Count >= 3).Take(5).ToList();
        if (popularGaps.Count > 0)
        {
            foreach (var (feat, (count, comps)) in popularGaps)
            {
                sb.AppendLine($"   📋 \"{feat}\" — {count} конкурентов: {string.Join(", ", comps.Take(3))}{(comps.Count > 3 ? "…" : "")}");
            }
        }
        else
        {
            sb.AppendLine("   ✅ Среднесрочных пробелов не выявлено.");
        }

        sb.AppendLine();

        // ── 🟢 Нишевые возможности (1-2 конкурента) ──
        sb.AppendLine("🟢 НИШЕВЫЕ ВОЗМОЖНОСТИ (отдельные конкуренты):");
        sb.AppendLine();

        var nicheGaps = gapsWeDontHave.Where(g => g.Value.Count <= 2).Take(5).ToList();
        if (nicheGaps.Count > 0)
        {
            foreach (var (feat, (count, comps)) in nicheGaps)
            {
                sb.AppendLine($"   💡 \"{feat}\" — {string.Join(", ", comps)}");
            }
        }
        else
        {
            sb.AppendLine("   ✅ Нишевых пробелов не выявлено.");
        }

        sb.AppendLine();

        // ── 💰 Ценовые рекомендации ──
        sb.AppendLine("💰 ЦЕНОВЫЕ РЕКОМЕНДАЦИИ:");
        sb.AppendLine();

        var competitorsWithPrice = competitors.Where(c => c.PriceUsd > 0).ToList();
        if (competitorsWithPrice.Count > 0)
        {
            var avgPrice = competitorsWithPrice.Average(c => c.PriceUsd);
            var minPrice = competitorsWithPrice.Min(c => c.PriceUsd);
            var maxPrice = competitorsWithPrice.Max(c => c.PriceUsd);

            sb.AppendLine($"   Рынок: ${minPrice:F2} — ${maxPrice:F2}, средняя ${avgPrice:F2}");

            // Анализ ниш
            var budgetSegment = competitors.Where(c => c.PriceUsd <= 5 && c.PriceUsd > 0).ToList();
            var midSegment = competitors.Where(c => c.PriceUsd > 5 && c.PriceUsd <= 20).ToList();
            var premiumSegment = competitors.Where(c => c.PriceUsd > 20).ToList();

            if (budgetSegment.Count == 0)
                sb.AppendLine($"   🎯 Ниша: бюджетный сегмент (≤$5) почти пуст — занять первым!");
            else
                sb.AppendLine($"   📊 Бюджетный сегмент (≤$5): {budgetSegment.Count} конкурентов — {string.Join(", ", budgetSegment.Select(c => c.Name))}");

            sb.AppendLine($"   📊 Средний сегмент ($5-$20): {midSegment.Count} конкурентов");
            sb.AppendLine($"   📊 Премиум (>$20): {premiumSegment.Count} — {string.Join(", ", premiumSegment.Select(c => c.Name))}");

            // Конкретная рекомендация
            if (avgPrice > 5)
                sb.AppendLine($"   💵 Рекомендация: цена ниже средней (${avgPrice:F0}) даст конкурентное преимущество.");
            else
                sb.AppendLine($"   💵 Рекомендация: цена на уровне рынка, акцент на уникальные AI-функции.");
        }

        sb.AppendLine();

        // ── 📣 Маркетинговые рекомендации (наши уникальные преимущества) ──
        sb.AppendLine("📣 МАРКЕТИНГ — наши уникальные преимущества:");
        sb.AppendLine();

        var allCompFeaturesNorm = competitors
            .SelectMany(c => ParseFeatureList(c.KeyFeatures))
            .Select(NormalizeFeature)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var uniqueToUs = OurFeatures
            .Where(f => !allCompFeaturesNorm.Contains(NormalizeFeature(f)))
            .ToList();

        if (uniqueToUs.Count > 0)
        {
            sb.AppendLine($"   У нас {uniqueToUs.Count} уникальных функций — подсветить в маркетинге:");
            foreach (var f in uniqueToUs)
                sb.AppendLine($"   🏆 {f}");
        }

        // Ключевые дифференциаторы
        sb.AppendLine();
        sb.AppendLine("   Ключевые дифференциаторы против конкурентов:");

        var ruCompetitors = competitors.Where(c => c.HasRussianLanguage).ToList();
        if (ruCompetitors.Count <= 2)
            sb.AppendLine($"   🇷🇺 Русский язык — только у нас и {string.Join(", ", ruCompetitors.Where(c => c.Name != "CarDiagnosticApp").Select(c => c.Name))}");

        var aiCount = competitors.Count(c => c.HasAiFeatures);
        sb.AppendLine($"   🤖 AI-диагностика — у нас и ещё {aiCount} конкурентов, наш AI-анализ глубже (исторические ошибки, контекст)");

        if (competitors.Count(c => c.Pricing == "free" || c.PriceUsd == 0) <= 3)
            sb.AppendLine($"   🆓 Бесплатный старт — мало кто предлагает полноценный freemium");

        sb.AppendLine();

        // ── ⚡ Активные угрозы ──
        sb.AppendLine("⚡ АКТИВНЫЕ УГРОЗЫ (недавно обновлялись + AI):");
        sb.AppendLine();

        var now = DateTime.UtcNow;
        var activeThreats = competitors
            .Where(c => c.LastVersionDate.HasValue
                && (now - c.LastVersionDate!.Value).TotalDays <= 90
                && c.HasAiFeatures)
            .ToList();

        if (activeThreats.Count > 0)
        {
            foreach (var t in activeThreats)
                sb.AppendLine($"   ⚠ {t.Name} — обновлялся {t.LastVersionDate:yyyy-MM-dd}, есть AI. Следить!");
        }
        else
        {
            sb.AppendLine("   ✅ Прямых AI-угроз нет.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Нормализует название функции для сравнения: удаляет скобки и обрезает.
    /// </summary>
    private static string NormalizeFeature(string feature)
    {
        if (string.IsNullOrEmpty(feature))
            return "";

        return feature
            .Replace("\"", "")
            .Trim()
            .ToLowerInvariant();
    }

    // ──────────────────────────────────────────────
    // Сводка сравнения для алертов
    // ──────────────────────────────────────────────

    /// <summary>
    /// Быстрая сводка сравнения (для отображения в UI-алертах).
    /// </summary>
    private async Task<string> BuildComparisonSummaryAsync()
    {
        var competitors = await _svc.GetAllAsync();
        if (competitors.Count == 0)
            return "";

        var sb = new StringBuilder();

        // Цены
        var withSub = competitors.Where(c => c.Pricing == "subscription").ToList();
        var avgSubPrice = withSub.Select(c => c.PriceUsd).DefaultIfEmpty(0).Average();
        var avgAllPrice = competitors.Where(c => c.PriceUsd > 0).Select(c => c.PriceUsd).DefaultIfEmpty(0).Average();

        sb.AppendLine($"📊 Сравнение: {competitors.Count} конкурентов, ср. цена рынка ${avgAllPrice:F0}");

        // AI-конкуренты
        var withAi = competitors.Where(c => c.HasAiFeatures).ToList();
        if (withAi.Count > 0)
            sb.AppendLine($"   🤖 С AI: {string.Join(", ", withAi.Select(c => c.Name))}");

        // Русский язык
        var withRu = competitors.Where(c => c.HasRussianLanguage).ToList();
        sb.AppendLine($"   🇷🇺 С русским: {(withRu.Count > 0 ? string.Join(", ", withRu.Select(c => c.Name)) : "только мы")}");

        // Частота обновлений
        var now = DateTime.UtcNow;
        var activeComps = competitors
            .Where(c => c.LastVersionDate.HasValue && (now - c.LastVersionDate!.Value).TotalDays <= 180)
            .ToList();
        var abandoned = competitors
            .Where(c => c.LastVersionDate.HasValue && (now - c.LastVersionDate!.Value).TotalDays > 180)
            .ToList();

        if (abandoned.Count > 0)
            sb.AppendLine($"   🔴 Заброшены: {string.Join(", ", abandoned.Select(c => c.Name))}");

        return sb.ToString();
    }

    /// <summary>
    /// Парсит JSON-массив или строку с функциями в список.
    /// </summary>
    private static List<string> ParseFeatureList(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "[]")
            return new List<string>();

        try
        {
            if (raw.StartsWith('['))
                return JsonSerializer.Deserialize<List<string>>(raw) ?? new();
        }
        catch { }

        // Fallback: разделители ; или ,
        return raw.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Сохраняет полный отчёт на рабочий стол.
    /// </summary>
    public async Task SaveReportAsync()
    {
        try
        {
            var baseReport = await _svc.GenerateReportAsync();
            var comparisonReport = await GenerateComparisonReportAsync();
            var fullReport = baseReport + "\n\n" + comparisonReport;

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"competitor_report_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            await File.WriteAllTextAsync(path, fullReport, Encoding.UTF8);
            Debug.WriteLine($"[CompetitorMonitor] Report saved to {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompetitorMonitor] Report error: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Статистика
    // ──────────────────────────────────────────────

    /// <summary>
    /// Возвращает статус мониторинга.
    /// </summary>
    public async Task<string> GetStatusAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Статус: {(_isRunning ? "▶️ Активен" : "⏸️ Остановлен")}");
        sb.AppendLine($"Последняя проверка: {(_lastCheckAt == DateTime.MinValue ? "ещё не было" : _lastCheckAt.ToString("yyyy-MM-dd HH:mm"))}");
        sb.AppendLine($"Интервал: {CheckInterval.TotalHours:F0} часов");
        sb.AppendLine(await _svc.BuildSummaryAsync());
        return sb.ToString();
    }

    /// <summary>
    /// Метод для вызова из UpdateAgent (совместимость).
    /// </summary>
    public async Task<string?> RunFromUpdateAgentAsync()
    {
        try
        {
            return await RunCheckAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CompetitorMonitor] UpdateAgent run error: {ex.Message}");
            return null;
        }
    }
}
