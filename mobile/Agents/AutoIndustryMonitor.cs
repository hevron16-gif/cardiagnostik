using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CarDiagnosticApp.Agents;

/// <summary>
/// Фоновый агент мониторинга автопрома.
/// Отслеживает: новые модели, отзывные кампании, изменения стандартов OBD2,
/// новые протоколы, ЭБУ, коды ошибок, изменения в законодательстве.
/// Запускается раз в 24 часа через UpdateAgent.
/// </summary>
public class AutoIndustryMonitor
{
    private static readonly Lazy<AutoIndustryMonitor> _instance = new(() => new AutoIndustryMonitor());
    public static AutoIndustryMonitor Instance => _instance.Value;

    private readonly AutoIndustryService _svc = new();
    private readonly HttpClient _http;
    private DateTime? _lastCheckAt;
    private bool _isRunning;

    /// <summary>Событие для UI-алертов.</summary>
    public event Action<string>? OnAlert;

    private AutoIndustryMonitor()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public DateTime? LastCheckAt => _lastCheckAt;
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Запускает немедленную проверку при старте приложения.
    /// Основной цикл мониторинга вызывается из UpdateAgent (раз в 14 дней).
    /// </summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Небольшая задержка чтобы не нагружать при старте
                await Task.Delay(TimeSpan.FromSeconds(15));
                await RunCheckAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoIndustryMonitor] Start error: {ex.Message}");
            }
        });
        Debug.WriteLine("[AutoIndustryMonitor] Started.");
    }

    // ── Категории и поисковые запросы ──

    private static readonly (string Category, string Query, string Relevance)[] SearchQueries =
    [
        ("new_model", "новые модели автомобилей 2026 Россия технические характеристики", "medium"),
        ("recall", "отзывная кампания автомобилей Росстандарт 2026", "high"),
        ("standard", "новый стандарт OBD2 EOBD диагностика изменения", "critical"),
        ("protocol", "новый диагностический протокол автомобилей CAN UDS DoIP", "high"),
        ("ecu", "новый блок управления ЭБУ двигатель автомобиль", "medium"),
        ("error_codes", "новые коды ошибок OBD2 DTC производитель", "high"),
        ("regulation", "изменения техрегламент диагностика автомобилей таможенный союз", "high"),
    ];

    // ── Домены-источники с весами ──

    private static readonly (string Domain, int Weight)[] TrustedSources =
    [
        ("drive2.ru", 10),
        ("zr.ru", 9),          // За рулём
        ("auto.ru", 8),
        ("autoreview.ru", 8),  // Авторевю
        ("motor.ru", 7),
        ("drom.ru", 7),
        ("kolesa.ru", 6),
        ("rg.ru", 6),          // Российская газета (публикует отзывы)
        ("diagnost.ru", 5),
        ("kodobd.ru", 5),
    ];

    // ──────────────────────────────────────────────
    // Основной цикл проверки
    // ──────────────────────────────────────────────

    /// <summary>
    /// Запускает полный цикл мониторинга. Вызывается из UpdateAgent раз в 24 часа.
    /// Возвращает строку-статус для логов.
    /// </summary>
    public async Task<string> RunCheckAsync()
    {
        if (_isRunning)
            return "[AutoIndustryMonitor] Уже выполняется, пропускаю.";

        _isRunning = true;
        var newItems = 0;
        var errors = 0;
        var alerts = new List<string>();

        try
        {
            Debug.WriteLine($"[AutoIndustryMonitor] Starting check cycle…");
            var before = await _svc.CountAsync();

            // 1. Поиск новостей по каждой категории
            foreach (var (category, query, relevance) in SearchQueries)
            {
                try
                {
                    var found = await SearchAndParseAsync(query, category, relevance);
                    if (found.Count > 0)
                    {
                        await _svc.InsertAllAsync(found);
                        newItems += found.Count;
                        Debug.WriteLine($"[AutoIndustryMonitor] [{category}] +{found.Count}");
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    Debug.WriteLine($"[AutoIndustryMonitor] Search error [{category}]: {ex.Message}");
                }
            }

            // 2. Ищем по конкретным авто-сайтам (drive2, zr, drom)
            try
            {
                var siteItems = await SearchTrustedSitesAsync();
                if (siteItems.Count > 0)
                {
                    await _svc.InsertAllAsync(siteItems);
                    newItems += siteItems.Count;
                    Debug.WriteLine($"[AutoIndustryMonitor] [sites] +{siteItems.Count}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                Debug.WriteLine($"[AutoIndustryMonitor] Sites error: {ex.Message}");
            }

            // 3. Анализ критических находок (алерты)
            var critical = await _svc.GetHighRelevanceAsync(10);
            foreach (var c in critical.Where(c => !c.IsProcessed).Take(3))
            {
                var icon = c.Relevance == "critical" ? "🔴" : "🟡";
                alerts.Add($"{icon} Автопром: [{c.Category}] {c.Title}");
            }

            // 4. Сохраняем отчёт
            await SaveReportAsync();

            _lastCheckAt = DateTime.UtcNow;

            // 5. Алерты
            if (alerts.Count > 0)
            {
                var msg = string.Join("\n", alerts);
                OnAlert?.Invoke(msg);
            }

            var result = newItems == 0
                ? $"✅ Мониторинг автопрома: новых событий нет. Всего в базе: {await _svc.CountAsync()}."
                : $"📰 Мониторинг автопрома: +{newItems} событий (ошибок поиска: {errors}). Всего: {await _svc.CountAsync()}.";

            Debug.WriteLine($"[AutoIndustryMonitor] {result}");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoIndustryMonitor] Fatal error: {ex}");
            return $"[AutoIndustryMonitor] Ошибка: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
        }
    }

    // ──────────────────────────────────────────────
    // Поиск и парсинг
    // ──────────────────────────────────────────────

    /// <summary>
    /// Поиск через DuckDuckGo Lite по запросу, парсинг результатов.
    /// </summary>
    private async Task<List<AutoIndustryNews>> SearchAndParseAsync(
        string query, string category, string baseRelevance)
    {
        var results = new List<AutoIndustryNews>();
        var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";

        try
        {
            var html = await _http.GetStringAsync(searchUrl);
            var matches = ParseDdgResults(html);

            foreach (var (title, url, snippet) in matches.Take(8))
            {
                if (await _svc.ExistsByUrlAsync(url))
                    continue;

                var source = ExtractDomain(url);
                var relevance = DetermineRelevance(title, snippet, baseRelevance, source);

                results.Add(new AutoIndustryNews
                {
                    Title = title,
                    Category = category,
                    Source = source,
                    SourceUrl = url,
                    Summary = Truncate(snippet, 300),
                    Relevance = relevance,
                    DetectedAt = DateTime.UtcNow,
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoIndustryMonitor] DDG search failed for '{query}': {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Поиск по доверенным авто-сайтам.
    /// </summary>
    private async Task<List<AutoIndustryNews>> SearchTrustedSitesAsync()
    {
        var results = new List<AutoIndustryNews>();

        foreach (var (domain, weight) in TrustedSources.Take(5))
        {
            try
            {
                var query = $"site:{domain} диагностика автомобилей новые модели отзыв";
                var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                var html = await _http.GetStringAsync(searchUrl);
                var matches = ParseDdgResults(html);

                foreach (var (title, url, snippet) in matches.Take(5))
                {
                    if (await _svc.ExistsByUrlAsync(url))
                        continue;

                    var category = CategorizeByTitle(title);
                    var relevance = DetermineRelevance(title, snippet, "medium", domain);

                    // Добавляем вес источника как бонус к relevancy
                    if (weight >= 9 && relevance == "medium")
                        relevance = "high";

                    results.Add(new AutoIndustryNews
                    {
                        Title = title,
                        Category = category,
                        Source = domain,
                        SourceUrl = url,
                        Summary = Truncate(snippet, 300),
                        Relevance = relevance,
                        DetectedAt = DateTime.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoIndustryMonitor] Site search failed for {domain}: {ex.Message}");
            }
        }

        return results;
    }

    // ──────────────────────────────────────────────
    // Парсинг DuckDuckGo Lite
    // ──────────────────────────────────────────────

    private static List<(string Title, string Url, string Snippet)> ParseDdgResults(string html)
    {
        var results = new List<(string, string, string)>();

        // DDG Lite формат: <a href="URL">Title</a> ... <span class="snippet">Snippet</span>
        var linkPattern = new Regex(
            @"<a\s+[^>]*href\s*=\s*""(?<url>[^""]+)""[^>]*>\s*(?<title>.+?)\s*</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var snippetPattern = new Regex(
            @"<span\s+class=""(?:snippet|result-snippet)"">(?<snippet>.+?)</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var links = linkPattern.Matches(html);
        var snippets = snippetPattern.Matches(html);

        var cleanLinks = links
            .Cast<Match>()
            .Select(m => (
                Title: StripHtml(m.Groups["title"].Value),
                Url: CleanUrl(m.Groups["url"].Value)))
            .Where(l => !string.IsNullOrEmpty(l.Url)
                && !l.Url.Contains("duckduckgo.com")
                && !l.Title.Contains("DuckDuckGo", StringComparison.OrdinalIgnoreCase))
            .Take(15)
            .ToList();

        for (int i = 0; i < cleanLinks.Count; i++)
        {
            var snippet = i < snippets.Count
                ? StripHtml(snippets[i].Groups["snippet"].Value)
                : "";
            results.Add((cleanLinks[i].Title, cleanLinks[i].Url, snippet));
        }

        return results;
    }

    // ──────────────────────────────────────────────
    // Вспомогательные методы
    // ──────────────────────────────────────────────

    private static string StripHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var clean = Regex.Replace(input, @"<[^>]+>", " ");
        return Regex.Replace(clean, @"\s+", " ").Trim();
    }

    private static string CleanUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";

        // Относительные URL на DDG
        if (url.StartsWith("//"))
            return "https:" + url;

        if (!url.StartsWith("http"))
            return "";

        // Отрезаем DDG редирект
        var uddgMatch = Regex.Match(url, @"uddg=(?<real>https?%3A%2F%2F[^&]+)");
        if (uddgMatch.Success)
            return Uri.UnescapeDataString(uddgMatch.Groups["real"].Value);

        return url;
    }

    private static string ExtractDomain(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.Host.Replace("www.", "");
        }
        catch { }
        return url;
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;
        return text[..maxLen] + "…";
    }

    /// <summary>
    /// Определяет категорию по заголовку (на основе ключевых слов).
    /// </summary>
    private static string CategorizeByTitle(string title)
    {
        var t = title.ToLowerInvariant();

        if (t.Contains("отзыв") || t.Contains("отзывн") || t.Contains("recall") || t.Contains("сервисная кампания"))
            return "recall";

        if (t.Contains("стандарт") || t.Contains("obd") || t.Contains("eobd") || t.Contains("протокол диагности"))
            return "standard";

        if (t.Contains("протокол") && (t.Contains("can") || t.Contains("uds") || t.Contains("doip")))
            return "protocol";

        if (t.Contains("эбу") || t.Contains("ecu") || t.Contains("блок управлен"))
            return "ecu";

        if (t.Contains("код ошибк") || t.Contains("dtc") || t.Contains("p0") || t.Contains("p1") || t.Contains("p2"))
            return "error_codes";

        if (t.Contains("модель") || t.Contains("новый") && (t.Contains("авто") || t.Contains("lada") || t.Contains("газ") || t.Contains("уаз")))
            return "new_model";

        if (t.Contains("техрегламент") || t.Contains("закон") || t.Contains("гост") || t.Contains("регулир"))
            return "regulation";

        return "other";
    }

    /// <summary>
    /// Определяет важность на основе заголовка и сниппета.
    /// </summary>
    private static string DetermineRelevance(string title, string snippet, string baseRelevance, string source)
    {
        var t = (title + " " + snippet).ToLowerInvariant();

        // Критические: новый стандарт, обязательные изменения
        if (t.Contains("новый стандарт") || t.Contains("изменение протокол") ||
            t.Contains("обязательн") && (t.Contains("диагност") || t.Contains("obd")))
            return "critical";

        // Важные: recall, новые коды, протоколы
        if (t.Contains("recall") || t.Contains("отзывн") ||
            t.Contains("код ошибк") || t.Contains("новый протокол"))
            return "high";

        // Средние: новые модели, ЭБУ
        if (t.Contains("новый модел") || t.Contains("поколение") ||
            t.Contains("эбу") || t.Contains("ecu"))
            return "medium";

        // Если источник с высоким весом, повышаем
        if (source is "zr.ru" or "autoreview.ru" or "drive2.ru")
            return baseRelevance == "medium" ? "medium" : baseRelevance;

        return baseRelevance;
    }

    // ──────────────────────────────────────────────
    // Отчёт
    // ──────────────────────────────────────────────

    public async Task SaveReportAsync()
    {
        try
        {
            var report = await _svc.GenerateReportAsync();
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"autoprom_report_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);
            Debug.WriteLine($"[AutoIndustryMonitor] Report saved to {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoIndustryMonitor] Report error: {ex.Message}");
        }
    }

    /// <summary>
    /// Ручной запуск (из админ-панели).
    /// </summary>
    public async Task<string> ForceRunAsync()
    {
        if (_isRunning)
            return "⏳ Мониторинг уже выполняется…";

        return await RunCheckAsync();
    }
}
