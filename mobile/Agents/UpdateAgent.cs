using CarDiagnosticApp.Services;
using System.Diagnostics;

namespace CarDiagnosticApp.Agents;

/// <summary>
/// Фоновый сервис планового обслуживания.
/// Запускается раз в 2 недели и выполняет:
/// - Проверку обновлений приложения
/// - Обновление справочников (марки/модели)
/// - Синхронизацию базы знаний с сервером
/// - Очистку устаревших данных
/// - Поиск новых кодов ошибок в интернете
/// - Обогащение существующих ошибок новыми решениями
/// - Поиск и загрузка картинок-схем из интернета
/// - Отправку анонимной статистики использования
/// </summary>
public class UpdateAgent
{
    private static UpdateAgent? _instance;
    public static UpdateAgent Instance => _instance ??= new UpdateAgent();

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private DateTime _lastRunAt = DateTime.MinValue;

    /// <summary>
    /// Дата последнего успешного запуска (UTC).
    /// </summary>
    public DateTime LastRunAt => _lastRunAt;

    private UpdateAgent() { }

    /// <summary>
    /// Запускает агента обновлений. Первый запуск — сразу после старта приложения.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _ = RunLoopAsync(_cts.Token);
        Debug.WriteLine("[UpdateAgent] Started — runs every 14 days");
    }

    /// <summary>
    /// Останавливает агента.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
        Debug.WriteLine("[UpdateAgent] Stopped");
    }

    /// <summary>
    /// Принудительный запуск обслуживания (по требованию пользователя).
    /// </summary>
    public async Task<string> ForceRunAsync()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("🔧 Принудительное обслуживание...\n");

        var tasks = new (string label, Func<Task<string?>> action)[]
        {
            ("Справочники", UpdateCarDatabaseAsync),
            ("База знаний", SyncKnowledgeBaseAsync),
            ("Схемы", UpdateDiagramDatabaseAsync),
            ("Очистка", CleanupOldDataAsync),
            ("Проверка обновлений", CheckForAppUpdateAsync),
            ("Новые коды ошибок", DiscoverNewErrorCodesAsync),
            ("Обогащение решений", EnrichExistingErrorCodesAsync),
            ("Сид кодирования", SeedCodingDatabaseAsync),
            ("Картинки-схемы", SearchAndDownloadSchemeImagesAsync),
        };

        foreach (var (label, action) in tasks)
        {
            try
            {
                var result = await action();
                sb.AppendLine($"  ✅ {label}: {result ?? "OK"}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ❌ {label}: {ex.Message}");
            }
        }

        _lastRunAt = DateTime.UtcNow;

        // Отчёт
        try
        {
            var since = DateTime.Now.AddDays(-14);
            var reportPath = await Services.ReportService.GenerateAndSaveAsync(newCodesSince: since);
            if (reportPath != null)
                sb.AppendLine($"\n📄 Отчёт сохранён: {Path.GetFileName(reportPath)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"\n⚠️ Отчёт не создан: {ex.Message}");
        }

        sb.AppendLine($"\nГотово — {_lastRunAt:dd.MM.yyyy HH:mm} UTC");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════
    //  ГЛАВНЫЙ ЦИКЛ
    // ═══════════════════════════════════════════════════

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // Первый запуск — сразу после старта
            await RunAllMaintenanceAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Initial run error: {ex.Message}");
        }

        // Периодический цикл: каждые 14 дней
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromDays(14), ct);
                await RunAllMaintenanceAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateAgent] Loop error: {ex.Message}");
            }
        }

        _isRunning = false;
    }

    private async Task RunAllMaintenanceAsync()
    {
        Debug.WriteLine($"[UpdateAgent] === Maintenance run: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC ===");

        var tasks = new Func<Task>[]
        {
            async () => { await UpdateCarDatabaseAsync(); },
            async () => { await CleanupOldDataAsync(); },
            async () => { await SyncKnowledgeBaseAsync(); },
            async () => { await CheckForAppUpdateAsync(); },
            async () => { await UpdateDiagramDatabaseAsync(); },
            async () => { await DiscoverNewErrorCodesAsync(); },
            async () => { await EnrichExistingErrorCodesAsync(); },
            async () => { await SeedCodingDatabaseAsync(); },
            async () => { await SearchAndDownloadSchemeImagesAsync(); },
            async () => { await MonitorCompetitorsAsync(); },
            async () => { await MonitorAutoIndustryAsync(); },
            async () => { await MonitorAutoMarketAsync(); },
        };

        foreach (var task in tasks)
        {
            try { await task(); }
            catch (Exception ex) { Debug.WriteLine($"[UpdateAgent] Task error: {ex.Message}"); }
        }

        // Этап 6.1 — отчёт после обслуживания
        try
        {
            var since = DateTime.Now.AddDays(-14);
            var reportPath = await Services.ReportService.GenerateAndSaveAsync(newCodesSince: since);
            if (reportPath != null)
                Debug.WriteLine($"[UpdateAgent] Report saved: {reportPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Report error: {ex.Message}");
        }

        _lastRunAt = DateTime.UtcNow;
        Debug.WriteLine($"[UpdateAgent] === Maintenance complete ===");
    }

    // ═══════════════════════════════════════════════════
    //  1. ОБНОВЛЕНИЕ СПРАВОЧНИКОВ (марки/модели)
    // ═══════════════════════════════════════════════════

    private async Task<string?> UpdateCarDatabaseAsync()
    {
        var api = IPlatformApplication.Current!.Services.GetRequiredService<ApiService>();

        // Пытаемся загрузить актуальный список марок с сервера
        var brands = await api.GetCarBrands();
        if (brands == null || brands.Count == 0) return "нет новых данных";

        var cache = new CarBrandCacheService();
        await cache.SaveBrandsAsync(brands);

        Debug.WriteLine($"[UpdateAgent] Car database updated: {brands.Count} brands");
        return $"загружено {brands.Count} марок";
    }

    // ═══════════════════════════════════════════════════
    //  2. СИНХРОНИЗАЦИЯ БАЗЫ ЗНАНИЙ
    // ═══════════════════════════════════════════════════

    private async Task<string?> SyncKnowledgeBaseAsync()
    {
        var learning = App.Learning;
        var stats = await learning.GetStatsAsync();

        if (stats.totalKnowledge == 0) return "база пуста — нечего синхронизировать";

        Debug.WriteLine($"[UpdateAgent] Knowledge base: {stats.totalKnowledge} records, " +
                        $"{stats.highConfidence} high-confidence, {stats.totalDiagnoses} diagnoses");

        // Здесь можно добавить отправку агрегированной статистики на сервер
        // для улучшения глобальной базы знаний.
        // await api.SyncKnowledgeStatsAsync(stats);

        return $"{stats.totalKnowledge} записей ({stats.highConfidence} c высокой достоверностью)";
    }

    // ═══════════════════════════════════════════════════
    //  3. ОБНОВЛЕНИЕ БАЗЫ СХЕМ
    // ═══════════════════════════════════════════════════

    private async Task<string?> UpdateDiagramDatabaseAsync()
    {
        var diagramDb = new DiagramDbService();

        // Проверяем ожидающие запросы
        var pendingCount = await diagramDb.GetPendingCountAsync();
        if (pendingCount == 0) return "нет ожидающих запросов";

        Debug.WriteLine($"[UpdateAgent] Pending diagram requests: {pendingCount}");

        // Запускаем лёгкий повтор (не более 2 запросов за раз)
        // Основной повтор делает BackgroundAgent раз в 30 мин
        return $"{pendingCount} ожидающих запросов";
    }

    // ═══════════════════════════════════════════════════
    //  4. ОЧИСТКА УСТАРЕВШИХ ДАННЫХ
    // ═══════════════════════════════════════════════════

    private async Task<string?> CleanupOldDataAsync()
    {
        int removed = 0;

        // ── Очистка PendingDiagramRequests (старше 90 дней, более 10 попыток) ──
        try
        {
            var diagramDb = new DiagramDbService();
            removed += await diagramDb.CleanupAbandonedRequestsAsync(maxRetries: 10, maxAgeDays: 90);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Diagram cleanup error: {ex.Message}");
        }

        // ── Очистка истории ошибок (старше 1 года без VIN) ──
        try
        {
            var historyService = new ErrorHistoryService();
            removed += await historyService.CleanupOldHistoryAsync(maxAgeDays: 365);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] History cleanup error: {ex.Message}");
        }

        // ── Очистка офлайн-кеша (старше 30 дней) ──
        try
        {
            var offlineDb = new OfflineDatabase();
            await offlineDb.InitAsync();
            removed += await offlineDb.CleanupExpiredCacheAsync(maxAgeDays: 30);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Offline cache cleanup error: {ex.Message}");
        }

        return removed > 0 ? $"удалено {removed} устаревших записей" : "нечего удалять";
    }

    // ═══════════════════════════════════════════════════
    //  5. ПРОВЕРКА ОБНОВЛЕНИЙ ПРИЛОЖЕНИЯ
    // ═══════════════════════════════════════════════════

    private async Task<string?> CheckForAppUpdateAsync()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("CarDiagnosticApp-UpdateChecker/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);

            // Запрашиваем метаданные последнего релиза с GitHub
            var json = await client.GetStringAsync(
                "https://api.github.com/repos/hevron16-gif/cardiagnostik/releases/latest");

            // Простой парсинг tag_name без Newtonsoft.Json
            var tagMatch = System.Text.RegularExpressions.Regex.Match(json, @"""tag_name""\s*:\s*""([^""]+)""");
            if (!tagMatch.Success) return "не удалось определить версию";

            var latestVersion = tagMatch.Groups[1].Value.TrimStart('v');
            var currentVersion = AppInfo.Current.VersionString;

            if (Version.TryParse(latestVersion, out var latest) &&
                Version.TryParse(currentVersion, out var current))
            {
                if (latest > current)
                {
                    Debug.WriteLine($"[UpdateAgent] Update available: {currentVersion} → {latestVersion}");
                    // Сохраняем результат в SQLite
                    await new CarBrandCacheService().SaveUpdateCheckAsync(currentVersion, latestVersion, true);
                    return $"доступна версия {latestVersion} (текущая: {currentVersion})";
                }
            }

            // Сохраняем результат проверки (обновлений нет)
            await new CarBrandCacheService().SaveUpdateCheckAsync(currentVersion, latestVersion, false);
            return $"актуально ({currentVersion})";
        }
        catch
        {
            return "сервер GitHub недоступен";
        }
    }

    // ═══════════════════════════════════════════════════
    //  6. ПОИСК НОВЫХ КОДОВ ОШИБОК В ИНТЕРНЕТЕ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Ищет в интернете OBD2-коды, которых ещё нет в локальной базе.
    /// Каждый код снабжается описанием и сохраняется в LearnedKnowledge
    /// с пометкой source="internet" и низкой начальной достоверностью (0.30).
    /// Лимит: до 10 новых кодов за один запуск, чтобы не перегружать сеть.
    /// </summary>
    private async Task<string?> DiscoverNewErrorCodesAsync()
    {
        const int maxNewCodes = 10;

        // ── 1. Собираем все известные коды из локальных БД ──
        var knownCodes = await GatherKnownErrorCodesAsync();
        Debug.WriteLine($"[UpdateAgent] Known codes in local DB: {knownCodes.Count}");

        // ── 2. Ищем популярные коды через DuckDuckGo ──
        var discoveredCodes = await SearchWebForErrorCodesAsync(knownCodes, maxNewCodes);

        if (discoveredCodes.Count == 0)
            return "новых кодов не найдено";

        // ── 3. Для каждого нового кода — ищем описание ──
        int stored = 0;
        foreach (var code in discoveredCodes)
        {
            try
            {
                var description = await FetchErrorDescriptionAsync(code);
                if (string.IsNullOrWhiteSpace(description)) continue;

                await App.Learning.RecordDiagnosisAsync(
                    errorCode: code,
                    carBrand: "",        // универсальный — для любой марки
                    carModel: "",
                    diagnosisText: description,
                    summary: description.Length > 200 ? description[..200] : description,
                    likelyCause: ""
                );
                stored++;
                Debug.WriteLine($"[UpdateAgent] Discovered: {code}");

                // Пауза чтобы не заспамить DuckDuckGo
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateAgent] Failed to fetch {code}: {ex.Message}");
            }
        }

        return $"обнаружено {discoveredCodes.Count} кодов, сохранено {stored}";
    }

    /// <summary>
    /// Собирает все известные коды из LearnedKnowledge, ErrorHistory и OfflineCache.
    /// </summary>
    private async Task<HashSet<string>> GatherKnownErrorCodesAsync()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var stats = await App.Learning.GetStatsAsync();
        }
        catch { }

        // Из истории ошибок
        try
        {
            var historyService = new ErrorHistoryService();
            var history = await historyService.GetHistorySinceAsync(DateTime.MinValue);
            foreach (var h in history)
                if (!string.IsNullOrWhiteSpace(h.ErrorCode))
                    codes.Add(h.ErrorCode.Trim().ToUpperInvariant());
        }
        catch { }

        return codes;
    }

    /// <summary>
    /// Ищет в интернете списки OBD2-кодов через DuckDuckGo Lite.
    /// Возвращает до maxResults новых кодов, отсутствующих в knownCodes.
    /// </summary>
    private async Task<List<string>> SearchWebForErrorCodesAsync(
        HashSet<string> knownCodes, int maxResults)
    {
        var found = new List<string>();

        // Списки поисковых запросов — самые популярные категории OBD2
        var searchQueries = new[]
        {
            "site:drive2.ru коды ошибок OBD2 расшифровка",
            "site:auto.ru коды ошибок OBD2 расшифровка ремонт",
            "site:diagnost.ru коды ошибок OBD2 расшифровка",
            "site:kodobd.ru коды ошибок OBD2 расшифровка",
            "site:diagnost7.ru коды ошибок OBD2 расшифровка",
            "site:diagnost54.ru коды ошибок OBD2 расшифровка",
            "OBD2 P0xxx коды ошибок список расшифровка",
            "OBD2 P0300-P0399 misfire error codes list",
            "OBD2 P0400-P0499 emissions error codes",
            "OBD2 U0100-U0299 network communication codes",
            "OBD2 C0000-C0999 chassis error codes",
        };

        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.Timeout = TimeSpan.FromSeconds(15);

        var allCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var query in searchQueries)
        {
            if (found.Count >= maxResults) break;

            try
            {
                // DuckDuckGo Lite — text-only, no JS
                var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                var html = await client.GetStringAsync(url);

                // Ищем OBD2-коды: P, C, B, U + 4 цифры/буквы (hex)
                var matches = System.Text.RegularExpressions.Regex.Matches(
                    html, @"\b([PCBU][0-9A-Fa-f]{4})\b");

                foreach (System.Text.RegularExpressions.Match m in matches)
                {
                    var code = m.Groups[1].Value.ToUpperInvariant();
                    if (!knownCodes.Contains(code))
                        allCandidates.Add(code);
                }

                await Task.Delay(800); // вежливая пауза между запросами
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateAgent] Search failed for '{query}': {ex.Message}");
            }
        }

        // Берём до maxResults кандидатов
        found.AddRange(allCandidates.Take(maxResults));
        return found;
    }

    /// <summary>
    /// Ищет описание конкретного кода ошибки через DuckDuckGo Lite.
    /// Возвращает текст описания (причина + возможные решения).
    /// Приоритет: Drive2 → общий поиск.
    /// </summary>
    private async Task<string?> FetchErrorDescriptionAsync(string errorCode)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            // Ищем описание — сначала Drive2, потом общий поиск
            var queries = new[]
            {
                // ── Русскоязычные (приоритет) ──
                $"site:drive2.ru {errorCode} ошибка расшифровка",
                $"site:auto.ru {errorCode} ошибка расшифровка ремонт",
                $"site:diagnost.ru {errorCode} расшифровка причины",
                $"site:kodobd.ru {errorCode} расшифровка",
                $"site:diagnost7.ru {errorCode} расшифровка",
                $"site:diagnost54.ru {errorCode} расшифровка",
                $"site:bmwpost.ru {errorCode}",
                // ── Международные ──
                $"site:obd-en.avto.pro {errorCode}",
                $"site:geekobd.com {errorCode}",
                $"site:carmasters.org {errorCode}",
                $"site:smartland.am {errorCode}",
                $"site:otomotiv-forum.com {errorCode}",
                $"EngineGuide {errorCode} OBD2",
                // ── Специализированные (чип-тюнинг/ECU) ──
                $"site:binunlock.com {errorCode}",
                $"site:iprog.pro {errorCode}",
                // ── Общий фолбек ──
                $"{errorCode} расшифровка код ошибки OBD2",
            };

            foreach (var q in queries)
            {
                var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(q)}";
                var html = await client.GetStringAsync(url);

                // Парсим сниппеты из результатов
                var snippetMatches = System.Text.RegularExpressions.Regex.Matches(
                    html, @"<td class=""result-snippet"">(.+?)</td>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                var snippets = new List<string>();
                foreach (System.Text.RegularExpressions.Match m in snippetMatches)
                {
                    var text = StripHtmlTags(m.Groups[1].Value).Trim();
                    if (!string.IsNullOrWhiteSpace(text) && text.Length > 20)
                        snippets.Add(text);
                }

                if (snippets.Count == 0) continue;

                // Собираем описание из 2-3 лучших сниппетов
                var description = string.Join(" ", snippets.Take(3));

                // Ограничиваем длину
                if (description.Length > 800)
                    description = description[..800] + "…";

                return $"[Drive2] {description}";
            }

            return null; // ничего не нашли ни в Drive2, ни в общем поиске
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Description fetch failed for {errorCode}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Убирает HTML-теги и HTML-entities из строки.</summary>
    private static string StripHtmlTags(string input)
    {
        // Убираем теги
        var noTags = System.Text.RegularExpressions.Regex.Replace(input, "<[^>]+>", " ");
        // Декодируем HTML entities
        return System.Net.WebUtility.HtmlDecode(noTags)
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"");
    }

    // ═══════════════════════════════════════════════════
    //  7. ОБОГАЩЕНИЕ СУЩЕСТВУЮЩИХ ОШИБОК НОВЫМИ РЕШЕНИЯМИ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Для уже известных кодов ошибок (с низкой достоверностью или старых)
    /// ищет в интернете свежие решения и обновляет базу знаний.
    /// Лимит: до 5 кодов за запуск.
    /// </summary>
    private async Task<string?> EnrichExistingErrorCodesAsync()
    {
        const int maxToEnrich = 5;

        // ── 1. Получаем коды, нуждающиеся в обогащении ──
        var stale = await App.Learning.GetStaleKnowledgeAsync(
            maxConfidence: 0.6, staleDays: 30);

        if (stale.Count == 0)
            return "все коды актуальны";

        Debug.WriteLine($"[UpdateAgent] Candidates for enrichment: {stale.Count}");

        int enriched = 0;
        int searched = 0;

        foreach (var record in stale)
        {
            if (searched >= maxToEnrich) break;
            searched++;

            try
            {
                // ── 2. Ищем решения в интернете ──
                var solutions = await SearchSolutionsForCodeAsync(
                    record.ErrorCode, record.CarBrand);

                if (string.IsNullOrWhiteSpace(solutions))
                {
                    Debug.WriteLine($"[UpdateAgent] No new solutions for {record.ErrorCode}");
                    continue;
                }

                // ── 3. Обновляем запись ──
                await App.Learning.EnrichKnowledgeAsync(
                    record.Id, solutions, confidenceBoost: 0.10);

                enriched++;
                Debug.WriteLine($"[UpdateAgent] Enriched: {record.ErrorCode} (conf: {record.Confidence:F2})");

                await Task.Delay(1200); // пауза между запросами
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateAgent] Enrich failed for {record.ErrorCode}: {ex.Message}");
            }
        }

        return enriched > 0
            ? $"обогащено {enriched} из {searched} проверенных"
            : $"проверено {searched} — новых решений не найдено";
    }

    /// <summary>
    /// Ищет в интернете способы решения конкретного кода ошибки.
    /// Приоритет: Drive2 → общий поиск.
    /// Возвращает строку с решениями, разделёнными ; или null.
    /// </summary>
    private async Task<string?> SearchSolutionsForCodeAsync(string errorCode, string carBrand)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        client.Timeout = TimeSpan.FromSeconds(15);

        try
        {
            // Приоритет: Русские → Международные → Специализированные
            var queries = string.IsNullOrWhiteSpace(carBrand)
                ? new[]
                {
                    // ── Русскоязычные (приоритет) ──
                    $"site:drive2.ru {errorCode} причины неисправности способы устранения",
                    $"site:auto.ru {errorCode} причины неисправности ремонт",
                    $"site:diagnost.ru {errorCode} причины способы устранения",
                    $"site:kodobd.ru {errorCode} причины устранение",
                    $"site:diagnost7.ru {errorCode} причины устранение",
                    $"site:diagnost54.ru {errorCode} причины ремонт",
                    $"site:bmwpost.ru {errorCode}",
                    // ── Международные ──
                    $"site:obd-en.avto.pro {errorCode}",
                    $"site:geekobd.com {errorCode}",
                    $"site:carmasters.org {errorCode}",
                    $"site:smartland.am {errorCode}",
                    $"site:otomotiv-forum.com {errorCode}",
                    $"EngineGuide {errorCode} fix repair",
                    // ── Специализированные ──
                    $"site:binunlock.com {errorCode}",
                    $"site:iprog.pro {errorCode}",
                    // ── Общий фолбек ──
                    $"{errorCode} причины неисправности способы устранения",
                }
                : new[]
                {
                    $"site:drive2.ru {errorCode} {carBrand} причины неисправности ремонт",
                    $"site:auto.ru {errorCode} {carBrand} ремонт эксплуатация",
                    $"site:diagnost.ru {errorCode} {carBrand} ремонт диагностика",
                    $"site:kodobd.ru {errorCode} {carBrand} ремонт",
                    $"site:diagnost7.ru {errorCode} {carBrand} ремонт",
                    $"site:diagnost54.ru {errorCode} {carBrand} ремонт",
                    $"site:bmwpost.ru {errorCode} {carBrand}",
                    // ── Международные ──
                    $"site:obd-en.avto.pro {errorCode} {carBrand}",
                    $"site:geekobd.com {errorCode} {carBrand}",
                    $"site:carmasters.org {errorCode} {carBrand}",
                    $"site:smartland.am {errorCode} {carBrand}",
                    $"site:otomotiv-forum.com {errorCode} {carBrand}",
                    $"EngineGuide {errorCode} {carBrand} repair",
                    // ── Специализированные ──
                    $"site:binunlock.com {errorCode} {carBrand}",
                    $"site:iprog.pro {errorCode} {carBrand}",
                    // ── Общий фолбек ──
                    $"{errorCode} {carBrand} причины неисправности ремонт",
                };

            List<string> allText = new();

            foreach (var query in queries)
            {
                var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                var html = await client.GetStringAsync(url);

                allText = ParseSnippets(html);
                if (allText.Count > 0) break; // нашли в Drive2 — хватит
            }

            if (allText.Count == 0) return null;

            // Извлекаем глагольные фразы — вероятные действия по ремонту
            var solutions = ExtractActionPhrases(string.Join(" ", allText));

            return solutions.Count > 0 ? string.Join("; ", solutions) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Solutions search failed for {errorCode}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Парсит сниппеты результатов из HTML DuckDuckGo Lite.
    /// </summary>
    private static List<string> ParseSnippets(string html)
    {
        var result = new List<string>();
        var snippetMatches = System.Text.RegularExpressions.Regex.Matches(
            html, @"<td class=""result-snippet"">(.+?)</td>",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match m in snippetMatches)
        {
            var text = StripHtmlTags(m.Groups[1].Value).Trim();
            if (!string.IsNullOrWhiteSpace(text) && text.Length > 25)
                result.Add(text);
        }
        return result;
    }

    /// <summary>
    /// Выделяет из текста фразы, похожие на действия по ремонту:
    /// «проверить X», «заменить Y», «очистить Z», «прошить ЭБУ» и т.п.
    /// </summary>
    private static List<string> ExtractActionPhrases(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lower = text.ToLowerInvariant();

        // Глаголы-маркеры ремонтных действий (русские)
        var actionVerbs = new[]
        {
            "проверить", "заменить", "очистить", "прочистить",
            "отремонтировать", "настроить", "отрегулировать",
            "прошить", "перепрошить", "обновить", "установить",
            "снять", "разобрать", "промыть", "продуть",
            "подтянуть", "затянуть", "открутить", "закрутить",
            "пропаять", "запаять", "изолировать", "подключить",
            "отключить", "сбросить", "адаптировать", "откалибровать",
            "замерить", "измерить", "прозвонить", "проверить цепь",
            "осмотреть", "продиагностировать",
        };

        // Разбиваем текст на предложения
        var sentences = System.Text.RegularExpressions.Regex.Split(
            text, @"(?<=[.!?…])\s+");

        foreach (var sentence in sentences)
        {
            var s = sentence.Trim();
            if (s.Length < 10 || s.Length > 200) continue;

            foreach (var verb in actionVerbs)
            {
                if (s.ToLowerInvariant().Contains(verb))
                {
                    // Нормализуем: первая буква заглавная, без точки в конце
                    var clean = s.TrimEnd('.', '!', '?', '…', ',').Trim();
                    if (clean.Length > 0)
                    {
                        clean = char.ToUpperInvariant(clean[0]) + clean[1..];
                        result.Add(clean);
                    }
                    break;
                }
            }
        }

        return result.Take(5).ToList(); // не более 5 решений
    }

    // ═══════════════════════════════════════════════════
    //  8. ПОИСК И ЗАГРУЗКА КАРТИНОК-СХЕМ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Сидирует базу скрытых функций, если она пуста.
    /// Также обновляет статистику активаций.
    /// </summary>
    private async Task<string?> SeedCodingDatabaseAsync()
    {
        try
        {
            var coding = new CodingService();
            var added = await coding.SeedAsync();

            if (added > 0)
            {
                var total = await coding.GetFeatureCountAsync();
                return $"добавлено {added} скрытых функций (всего {total})";
            }
            else
            {
                var total = await coding.GetFeatureCountAsync();
                return $"база актуальна: {total} функций";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Coding seed error: {ex.Message}");
            return $"ошибка сидирования: {ex.Message}";
        }
    }

    /// <summary>
    /// Для ожидающих запросов схем (PendingDiagramRequests) ищет в интернете
    /// изображения-схемы, скачивает их и сохраняет в локальную БД.
    /// Лимит: до 3 схем за один запуск.
    /// </summary>
    private async Task<string?> SearchAndDownloadSchemeImagesAsync()
    {
        const int maxImages = 3;

        var diagramDb = new DiagramDbService();

        // ── 1. Берём ожидающие запросы ──
        var pending = await diagramDb.GetPendingRequestsAsync();

        // Фильтруем: не больше maxRetries попыток (чтобы не долбить безнадёжные)
        var candidates = pending
            .Where(p => p.RetryCount <= 5)
            .Take(maxImages)
            .ToList();

        if (candidates.Count == 0)
            return pending.Count > 0
                ? $"{pending.Count} запросов (все исчерпали попытки)"
                : "нет ожидающих запросов";

        Debug.WriteLine($"[UpdateAgent] Pending diagram requests: {pending.Count}, candidates: {candidates.Count}");

        int downloaded = 0;
        int tried = 0;

        foreach (var req in candidates)
        {
            tried++;

            try
            {
                // ── 2. Ищем изображения через DuckDuckGo ──
                var imageUrl = await FindSchematicImageUrlAsync(
                    req.ErrorCode, req.CarBrand, req.CarModel);

                if (string.IsNullOrWhiteSpace(imageUrl))
                {
                    // Обновляем счётчик попыток
                    await diagramDb.SavePendingRequestAsync(
                        req.CarBrand, req.CarModel, req.ErrorCode,
                        req.SearchQuery);
                    Debug.WriteLine($"[UpdateAgent] No image found for {req.ErrorCode} {req.CarBrand} {req.CarModel}");
                    continue;
                }

                // ── 3. Скачиваем и сохраняем ──
                var localPath = await diagramDb.DownloadAndSaveImageDiagramAsync(
                    req.CarBrand, req.CarModel, req.ErrorCode,
                    imageUrl: imageUrl,
                    sourceUrl: imageUrl,
                    source: "internet");

                if (localPath != null)
                {
                    await diagramDb.MarkRequestAsFoundAsync(
                        req.CarBrand, req.CarModel, req.ErrorCode);
                    downloaded++;
                    Debug.WriteLine($"[UpdateAgent] Downloaded diagram: {req.ErrorCode} → {Path.GetFileName(localPath)}");
                }

                await Task.Delay(1000); // вежливая пауза
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateAgent] Diagram search failed for {req.ErrorCode}: {ex.Message}");
            }
        }

        return downloaded > 0
            ? $"скачано {downloaded} из {tried} проверенных"
            : $"проверено {tried} — изображений не найдено";
    }

    /// <summary>
    /// Ищет URL картинки-схемы для заданного кода ошибки и автомобиля.
    /// Алгоритм:
    /// 1. Поиск в DuckDuckGo Lite → получаем ссылки на страницы
    /// 2. Заходим на каждую страницу → ищем &lt;img&gt; теги
    /// 3. Фильтруем: не иконки, не реклама, не аватары
    /// 4. Возвращаем лучший URL
    /// </summary>
    private async Task<string?> FindSchematicImageUrlAsync(
        string errorCode, string carBrand, string carModel)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(20);

        // ── Шаг 1: поиск страниц со схемами ──
        var sources = new[]
        {
            // ── Русскоязычные (приоритет) ──
            (domain: "drive2.ru",   q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:drive2.ru {errorCode} схема расположения датчиков OBD2"
                : $"site:drive2.ru {errorCode} {carBrand} {carModel} схема"),
            (domain: "auto.ru",     q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:auto.ru {errorCode} схема датчики"
                : $"site:auto.ru {errorCode} {carBrand} схема"),
            (domain: "diagnost.ru", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:diagnost.ru {errorCode} схема расположение датчик"
                : $"site:diagnost.ru {errorCode} {carBrand} схема"),
            (domain: "kodobd.ru",   q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:kodobd.ru {errorCode} схема расположение"
                : $"site:kodobd.ru {errorCode} {carBrand} схема"),
            (domain: "diagnost7.ru", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:diagnost7.ru {errorCode} схема расположение"
                : $"site:diagnost7.ru {errorCode} {carBrand} схема"),
            (domain: "diagnost54.ru", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:diagnost54.ru {errorCode} схема"
                : $"site:diagnost54.ru {errorCode} {carBrand} схема"),
            (domain: "bmwpost.ru", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:bmwpost.ru {errorCode}"
                : $"site:bmwpost.ru {errorCode} {carBrand}"),
            // ── Международные ──
            (domain: "obd-en.avto.pro", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:obd-en.avto.pro {errorCode}"
                : $"site:obd-en.avto.pro {errorCode} {carBrand}"),
            (domain: "geekobd.com", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:geekobd.com {errorCode}"
                : $"site:geekobd.com {errorCode} {carBrand}"),
            (domain: "carmasters.org", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:carmasters.org {errorCode}"
                : $"site:carmasters.org {errorCode} {carBrand}"),
            (domain: "smartland.am", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:smartland.am {errorCode}"
                : $"site:smartland.am {errorCode} {carBrand}"),
            (domain: "otomotiv-forum.com", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:otomotiv-forum.com {errorCode}"
                : $"site:otomotiv-forum.com {errorCode} {carBrand}"),
            (domain: "engineguide", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"EngineGuide {errorCode} diagram schematic"
                : $"EngineGuide {errorCode} {carBrand} diagram"),
            // ── Специализированные (чип-тюнинг/ECU) ──
            (domain: "binunlock.com", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:binunlock.com {errorCode}"
                : $"site:binunlock.com {errorCode} {carBrand}"),
            (domain: "iprog.pro", q: string.IsNullOrWhiteSpace(carBrand)
                ? $"site:iprog.pro {errorCode}"
                : $"site:iprog.pro {errorCode} {carBrand}"),
        };

        HashSet<string> pageUrls = new();
        string html = "";

        foreach (var (domain, q) in sources)
        {
            try
            {
                var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(q)}";
                html = await client.GetStringAsync(url);
                pageUrls = ExtractResultLinks(html);
                if (pageUrls.Count > 0) break;
            }
            catch { }
        }

        // Если все спец-источники пусты — общий поиск
        if (pageUrls.Count == 0)
        {
            var fallbackQuery = string.IsNullOrWhiteSpace(carBrand)
                ? $"{errorCode} схема расположения датчиков OBD2"
                : $"{errorCode} {carBrand} {carModel} схема";
            var fbUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(fallbackQuery)}";
            try { html = await client.GetStringAsync(fbUrl); pageUrls = ExtractResultLinks(html); }
            catch { return null; }
        }

        if (pageUrls.Count == 0) return null;

        Debug.WriteLine($"[UpdateAgent] Found {pageUrls.Count} result pages for '{errorCode} {carBrand}'");

        // ── Шаг 2: заходим на страницы и ищем изображения ──
        var allImageUrls = new List<(string url, int score)>();

        foreach (var pageUrl in pageUrls.Take(3))
        {
            try
            {
                var pageHtml = await client.GetStringAsync(pageUrl);

                var imgMatches = System.Text.RegularExpressions.Regex.Matches(
                    pageHtml,
                    @"<img[^>]+src=[""']([^""']+)[""']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match img in imgMatches)
                {
                    var src = img.Groups[1].Value;

                    // Превращаем относительные URL в абсолютные
                    if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            src = new Uri(new Uri(pageUrl), src).AbsoluteUri;
                        }
                        catch { continue; }
                    }

                    var score = ScoreImageUrl(src, errorCode);
                    if (score > 0)
                        allImageUrls.Add((src, score));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateAgent] Failed to parse page: {pageUrl} — {ex.Message}");
            }
        }

        if (allImageUrls.Count == 0) return null;

        // ── Шаг 3: выбираем лучшую картинку ──
        var best = allImageUrls
            .OrderByDescending(x => x.score)
            .First();

        Debug.WriteLine($"[UpdateAgent] Best image for {errorCode}: score={best.score} url={best.url[..Math.Min(120, best.url.Length)]}");
        return best.url;
    }

    /// <summary>
    /// Извлекает ссылки на страницы из HTML DuckDuckGo Lite,
    /// исключая служебные и рекламные URL.
    /// </summary>
    private static HashSet<string> ExtractResultLinks(string html)
    {
        var pageUrls = new HashSet<string>();
        var linkMatches = System.Text.RegularExpressions.Regex.Matches(
            html, @"<a[^>]+href=[""'](https?://[^""']+)[""']",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in linkMatches)
        {
            var url = m.Groups[1].Value;
            if (url.Contains("duckduckgo.com") ||
                url.Contains("doubleclick") ||
                url.Contains("googleadservices") ||
                url.Contains("googlesyndication"))
                continue;
            pageUrls.Add(url);
        }
        return pageUrls;
    }

    /// <summary>
    /// Оценивает, насколько URL похож на изображение-схему.
    /// Возвращает 0 если не подходит, >0 если подходит (больше = лучше).
    /// </summary>
    private static int ScoreImageUrl(string url, string errorCode)
    {
        var lower = url.ToLowerInvariant();
        int score = 0;

        // ── Должен быть настоящим изображением ──
        if (!lower.EndsWith(".jpg") && !lower.EndsWith(".jpeg") &&
            !lower.EndsWith(".png") && !lower.EndsWith(".webp") &&
            !lower.EndsWith(".gif") && !lower.EndsWith(".bmp"))
            return 0;

        // ── Минус за явно не-схемное ──
        if (lower.Contains("icon") || lower.Contains("avatar") ||
            lower.Contains("logo") || lower.Contains("banner") ||
            lower.Contains("emoji") || lower.Contains("thumb") ||
            lower.Contains("button") || lower.Contains("badge") ||
            lower.Contains("favicon") || lower.Contains("pixel") ||
            lower.Contains("1x1") || lower.Contains("spacer") ||
            lower.Contains("tracking") || lower.Contains("pixel.gif"))
            return 0;

        // ── Плюс за схематические признаки ──
        if (lower.Contains("schema") || lower.Contains("scheme") ||
            lower.Contains("diagram") || lower.Contains("схем") ||
            lower.Contains("располож") || lower.Contains("raspol") ||
            lower.Contains("sxema") || lower.Contains("dvigatel") ||
            lower.Contains("engine") || lower.Contains("motor"))
            score += 20;

        if (lower.Contains("datchik") || lower.Contains("sensor") ||
            lower.Contains("датчик"))
            score += 15;

        if (lower.Contains("dtc") || lower.Contains("obd"))
            score += 10;

        // ── Плюс если в URL фигурирует код ошибки ──
        if (lower.Contains(errorCode.ToLowerInvariant()))
            score += 25;

        // ── Минус за слишком маленькие (по имени файла) ──
        if (lower.Contains("50x50") || lower.Contains("100x100") ||
            lower.Contains("_s.") || lower.Contains("_thumb") ||
            lower.Contains("-small") || lower.Contains("_small"))
            score -= 30;

        // ── Плюс за средние/крупные (по имени) ──
        if (lower.Contains("_l.") || lower.Contains("_large") ||
            lower.Contains("-large") || lower.Contains("_big") ||
            lower.Contains("_orig") || lower.Contains("_full") ||
            lower.Contains("800") || lower.Contains("1024") ||
            lower.Contains("1200") || lower.Contains("1600"))
            score += 10;

        // ── Русскоязычные (приоритет) ──
        if (lower.Contains("drive2") || lower.Contains("a.d-cd"))
            score += 10; // Drive2 — #1
        else if (lower.Contains("auto.ru") || lower.Contains("autoru"))
            score += 8;  // auto.ru — #2
        else if (lower.Contains("diagnost54.ru"))
            score += 7;  // diagnost54.ru
        else if (lower.Contains("diagnost7.ru"))
            score += 7;  // diagnost7.ru
        else if (lower.Contains("diagnost.ru"))
            score += 7;  // diagnost.ru
        else if (lower.Contains("kodobd.ru"))
            score += 7;  // kodobd.ru
        else if (lower.Contains("bmwpost.ru"))
            score += 7;  // bmwpost.ru
        // ── Международные ──
        else if (lower.Contains("obd-en.avto.pro"))
            score += 6;  // obd-en.avto.pro
        else if (lower.Contains("geekobd.com"))
            score += 6;  // geekobd.com
        else if (lower.Contains("carmasters.org"))
            score += 6;  // carmasters.org
        else if (lower.Contains("smartland.am"))
            score += 6;  // smartland.am
        else if (lower.Contains("otomotiv-forum.com"))
            score += 6;  // otomotiv-forum.com
        // ── Хостинги картинок ──
        else if (lower.Contains("radikal") || lower.Contains("fastpic") ||
            lower.Contains("imgur") || lower.Contains("postimg") ||
            lower.Contains("vfl.ru") || lower.Contains("i.ibb"))
            score += 5;
        // ── Специализированные (чип-тюнинг/ECU) ──
        else if (lower.Contains("binunlock.com"))
            score += 5;  // binunlock.com — чип-тюнинг
        else if (lower.Contains("iprog.pro"))
            score += 5;  // iprog.pro — программаторы

        return Math.Max(1, score); // минимум 1 если прошли фильтр
    }

    // ═══════════════════════════════════════════════════
    //  9. МОНИТОРИНГ КОНКУРЕНТОВ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Делегирует мониторинг конкурентов CompetitorMonitor.
    /// </summary>
    private async Task<string?> MonitorCompetitorsAsync()
    {
        try
        {
            return await CompetitorMonitor.Instance.RunFromUpdateAgentAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Competitor monitoring error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Делегирует мониторинг автопрома AutoIndustryMonitor.
    /// </summary>
    private async Task<string?> MonitorAutoIndustryAsync()
    {
        try
        {
            return await AutoIndustryMonitor.Instance.RunCheckAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Auto industry monitoring error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Делегирует мониторинг российского авторынка AutoMarketMonitor.
    /// </summary>
    private async Task<string?> MonitorAutoMarketAsync()
    {
        try
        {
            return await AutoMarketMonitor.Instance.RunCheckAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateAgent] Auto market monitoring error: {ex.Message}");
            return null;
        }
    }
}
