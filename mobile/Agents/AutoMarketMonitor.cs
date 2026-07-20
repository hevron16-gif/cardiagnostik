using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CarDiagnosticApp.Agents;

/// <summary>
/// Мониторинг российского авторынка.
/// Отслеживает: новые марки, новые модели, обновления существующих,
/// китайские бренды выходящие на рынок РФ, смену поколений.
/// Запускается из UpdateAgent (раз в 14 дней) и вручную из админ-панели.
/// </summary>
public class AutoMarketMonitor
{
    private static readonly Lazy<AutoMarketMonitor> _instance = new(() => new AutoMarketMonitor());
    public static AutoMarketMonitor Instance => _instance.Value;

    private readonly RussianAutoService _svc = new();
    private readonly HttpClient _http;
    private bool _isRunning;
    private DateTime? _lastCheckAt;

    public event Action<string>? OnAlert;

    public DateTime? LastCheckAt => _lastCheckAt;
    public bool IsRunning => _isRunning;

    private AutoMarketMonitor()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:130.0) Gecko/20100101 Firefox/130.0");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>Запуск при старте приложения.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20));
                await _svc.SeedDefaultsAsync();
                await _svc.SeedEnginesAsync();
                await RunCheckAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoMarketMonitor] Start error: {ex.Message}");
            }
        });
        Debug.WriteLine("[AutoMarketMonitor] Started.");
    }

    // ──────────────────────────────────────────────
    // Поисковые запросы по категориям
    // ──────────────────────────────────────────────

    private static readonly (string Label, string Query)[] SearchQueries =
    [
        ("Новые российские марки", "новая российская марка автомобилей 2026"),
        ("Новые модели LADA/ВАЗ", "новая модель Lada АвтоВАЗ 2026"),
        ("Новые модели УАЗ", "УАЗ новая модель автомобиль 2026"),
        ("Новые модели ГАЗ", "ГАЗ новая модель автомобиль 2026"),
        ("Новые модели Москвич", "Москвич новый автомобиль модель 2026"),
        ("Новые китайские бренды РФ", "новые китайские бренды авто Россия 2026"),
        ("Электромобили Россия", "новый российский электромобиль марка модель 2026"),
        ("Новинки автопрома РФ", "российский автопром новинки новые модели 2026 site:zr.ru"),
        ("АвтоВАЗ новинки", "АвтоВАЗ новая модель анонс 2026 site:drive2.ru"),
        ("Российские авто премьеры", "премьера новый российский автомобиль 2026 site:autoreview.ru"),
    ];

    private static readonly string[] RussianAutomotiveSites =
    [
        "zr.ru",          // За рулём
        "autoreview.ru",  // Авторевю
        "auto.ru",
        "motor.ru",
        "drom.ru",
        "kolesa.ru",
        "drive2.ru",
        "rg.ru",
        "autonews.ru",
        "auto.mail.ru",
    ];

    // ──────────────────────────────────────────────
    // Основной цикл
    // ──────────────────────────────────────────────

    public async Task<string> RunCheckAsync()
    {
        if (_isRunning)
            return "[AutoMarketMonitor] Уже выполняется, пропускаю.";

        _isRunning = true;
        var newItems = 0;
        var errors = 0;
        var alerts = new List<string>();

        try
        {
            Debug.WriteLine("[AutoMarketMonitor] Starting check cycle…");

            // 1. Поиск по запросам
            foreach (var (label, query) in SearchQueries)
            {
                try
                {
                    var found = await SearchForModelsAsync(query, label);
                    if (found.Count > 0)
                    {
                        await _svc.InsertAllAsync(found);
                        newItems += found.Count;
                        Debug.WriteLine($"[AutoMarketMonitor] [{label}] +{found.Count}");
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    Debug.WriteLine($"[AutoMarketMonitor] Search [{label}] error: {ex.Message}");
                }
            }

            // 2. Поиск по конкретным сайтам
            try
            {
                var siteItems = await SearchSitesAsync();
                if (siteItems.Count > 0)
                {
                    await _svc.InsertAllAsync(siteItems);
                    newItems += siteItems.Count;
                    Debug.WriteLine($"[AutoMarketMonitor] [sites] +{siteItems.Count}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                Debug.WriteLine($"[AutoMarketMonitor] Sites error: {ex.Message}");
            }

            // 2.5. Поиск обновлений существующих моделей
            try
            {
                var updateItems = await SearchForModelUpdatesAsync();
                if (updateItems.Count > 0)
                {
                    await _svc.InsertAllUpdatesAsync(updateItems);
                    newItems += updateItems.Count;
                    Debug.WriteLine($"[AutoMarketMonitor] [updates] +{updateItems.Count}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                Debug.WriteLine($"[AutoMarketMonitor] Updates error: {ex.Message}");
            }

            // 2.6. Поиск новых двигателей и систем
            try
            {
                var engineItems = await SearchForEnginesAsync();
                if (engineItems.Count > 0)
                {
                    await _svc.InsertAllEnginesAsync(engineItems);
                    newItems += engineItems.Count;
                    Debug.WriteLine($"[AutoMarketMonitor] [engines] +{engineItems.Count}");
                }
            }
            catch (Exception ex)
            {
                errors++;
                Debug.WriteLine($"[AutoMarketMonitor] Engines error: {ex.Message}");
            }

            // 3. Алерты о новых марках (только если марка вообще новая, не просто модель)
            var fresh = await _svc.GetNewAsync(10);
            foreach (var m in fresh.Where(m => !m.IsProcessed && m.Status == "announced").Take(3))
            {
                alerts.Add($"🆕 Новая российская марка/модель: {m.Brand} {m.ModelName} ({m.YearStart})");
            }

            if (alerts.Count > 0)
                OnAlert?.Invoke(string.Join("\n", alerts));

            _lastCheckAt = DateTime.UtcNow;

            var result = newItems == 0
                ? $"✅ Мониторинг российского авторынка: новых моделей не найдено. Всего в базе: {await _svc.CountAsync()}."
                : $"🚙 Мониторинг российского авторынка: +{newItems} моделей (ошибок: {errors}). Всего: {await _svc.CountAsync()}.";

            Debug.WriteLine($"[AutoMarketMonitor] {result}");
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoMarketMonitor] Fatal: {ex}");
            return $"[AutoMarketMonitor] Ошибка: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
        }
    }

    // ──────────────────────────────────────────────
    // Поиск и парсинг
    // ──────────────────────────────────────────────

    private async Task<List<RussianAutoModel>> SearchForModelsAsync(string query, string label)
    {
        var results = new List<RussianAutoModel>();
        var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";

        try
        {
            var html = await _http.GetStringAsync(searchUrl);
            var matches = ParseDdgResults(html);

            foreach (var (title, url, snippet) in matches.Take(8))
            {
                var (brand, modelName) = ExtractBrandModel(title);

                // Пропускаем если марка/модель не извлечены
                if (string.IsNullOrEmpty(brand) || string.IsNullOrEmpty(modelName))
                    continue;

                // Дедупликация
                if (await _svc.ExistsByModelAsync(brand, modelName))
                    continue;

                var bodyType = DetectBodyType(title + " " + snippet);
                var engineType = DetectEngineType(title + " " + snippet);
                var status = DetectStatus(title, snippet);

                results.Add(new RussianAutoModel
                {
                    Brand = brand,
                    ModelName = modelName,
                    Generation = DetectGeneration(title, snippet),
                    YearStart = ExtractYear(title + " " + snippet),
                    BodyType = bodyType,
                    EngineTypes = engineType,
                    OBDProtocol = "OBD2/EOBD", // по умолчанию для современных
                    Source = ExtractDomain(url),
                    SourceUrl = url,
                    IsNew = status is "announced" or "in_production",
                    Status = status,
                    Factory = DetectFactory(brand),
                    DetectedAt = DateTime.UtcNow,
                    Notes = $"Найдено по запросу: {label}",
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoMarketMonitor] DDG search failed for '{query}': {ex.Message}");
        }

        return results;
    }

    private async Task<List<RussianAutoModel>> SearchSitesAsync()
    {
        var results = new List<RussianAutoModel>();

        foreach (var site in RussianAutomotiveSites.Take(5))
        {
            try
            {
                var query = $"site:{site} новая модель российский автомобиль 2026";
                var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                var html = await _http.GetStringAsync(searchUrl);
                var matches = ParseDdgResults(html);

                foreach (var (title, url, snippet) in matches.Take(5))
                {
                    var (brand, modelName) = ExtractBrandModel(title);
                    if (string.IsNullOrEmpty(brand) || string.IsNullOrEmpty(modelName))
                        continue;
                    if (await _svc.ExistsByModelAsync(brand, modelName))
                        continue;

                    results.Add(new RussianAutoModel
                    {
                        Brand = brand,
                        ModelName = modelName,
                        Generation = DetectGeneration(title, snippet),
                        YearStart = ExtractYear(title + " " + snippet),
                        BodyType = DetectBodyType(title + " " + snippet),
                        EngineTypes = DetectEngineType(title + " " + snippet),
                        OBDProtocol = "OBD2/EOBD",
                        Source = site,
                        SourceUrl = url,
                        IsNew = true,
                        Status = "announced",
                        Factory = DetectFactory(brand),
                        DetectedAt = DateTime.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoMarketMonitor] Site search failed for {site}: {ex.Message}");
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

        var linkPattern = new Regex(
            @"<a\s+[^>]*href\s*=\s*""(?<url>[^""]+)""[^>]*>\s*(?<title>.+?)\s*</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var snippetPattern = new Regex(
            @"<span\s+class=""(?:snippet|result-snippet)"">(?<snippet>.+?)</span>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var links = linkPattern.Matches(html);
        var snippets = snippetPattern.Matches(html);

        var cleanLinks = links.Cast<Match>()
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
    // Извлечение марки и модели
    // ──────────────────────────────────────────────

    /// <summary>Извлекает пару (Марка, Модель) из заголовка.</summary>
    private static (string Brand, string ModelName) ExtractBrandModel(string title)
    {
        if (string.IsNullOrEmpty(title)) return ("", "");

        var t = title;
        var knownBrands = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["LADA"] = ["LADA", "Lada", "Лада", "ВАЗ", "АвтоВАЗ", "Vesta", "Granta", "Niva", "Largus", "Aura", "XRAY", "Xray"],
            ["УАЗ"] = ["УАЗ", "UAZ", "Patriot", "Hunter", "Pickup", "Профи", "Буханка", "СГР"],
            ["ГАЗ"] = ["ГАЗ", "GAZ", "Газель", "Соболь", "Валдай", "Волга", "NN"],
            ["Москвич"] = ["Москвич", "Moskvich"],
            ["Evolute"] = ["Evolute", "Эволют", "i-Pro", "i-Joy", "i-Sky", "i-Jet"],
            ["Xcite"] = ["Xcite", "X-Cross", "Икс-Кросс"],
            ["AmberAuto"] = ["AmberAuto", "Amber", "Амбер", "Автотор"],
            ["Aurus"] = ["Aurus", "Аурус", "Senat", "Komendant"],
            ["КАМАЗ"] = ["КАМАЗ", "KAMAZ"],
            ["AVTOVAZ"] = ["LADA"],
            ["Haval"] = ["Haval", "Хавейл", "Jolion", "F7", "Dargo", "H9"],
            ["Chery"] = ["Chery", "Чери", "Tiggo", "Arrizo"],
            ["Geely"] = ["Geely", "Джили", "Atlas", "Coolray", "Monjaro", "Emgrand"],
            ["Changan"] = ["Changan", "Чанган", "CS35", "CS55", "CS75", "UNI"],
            ["Omoda"] = ["Omoda", "Омода", "C5", "S5"],
            ["Exeed"] = ["Exeed", "Эксид", "TXL", "VX", "LX"],
            ["Tank"] = ["Tank", "Тэнк", "300", "500", "700"],
            ["Jetour"] = ["Jetour", "Джетур", "Dashing", "X70", "X90"],
            ["Lixiang"] = ["Lixiang", "Li Auto", "Лисян", "L7", "L8", "L9"],
            ["Zeekr"] = ["Zeekr", "Зикр", "001", "007", "009"],
            ["Voyah"] = ["Voyah", "Воях", "Free", "Dream", "Passion"],
            ["BYD"] = ["BYD", "БИД", "Han", "Tang", "Song", "Seal", "Dolphin"],
            ["BAIC"] = ["BAIC", "БАИК", "X35", "X55", "U5"],
            ["Dongfeng"] = ["Dongfeng", "Донгфенг"],
            ["FAW"] = ["FAW", "ФАВ", "Bestune"],
            ["GAC"] = ["GAC", "ГАК", "GS5", "GS8"],
            ["SWM"] = ["SWM", "СВМ"],
            ["Kaiyi"] = ["Kaiyi", "Кайи", "E5", "X3"],
            ["Sollers"] = ["Sollers", "Соллерс", "Argo"],
        };

        foreach (var (brand, keywords) in knownBrands)
        {
            foreach (var kw in keywords)
            {
                if (t.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    // Извлекаем модель — второе слово после марки
                    var model = ExtractModelName(t, kw, keywords);

                    // Если модель совпадает с маркой, пробуем найти другое слово
                    if (model.Equals(kw, StringComparison.OrdinalIgnoreCase))
                        model = FindPossibleModel(t, keywords);

                    return (brand, model);
                }
            }
        }

        // Fallback: пробуем извлечь просто по словам
        return FallbackExtract(title);
    }

    private static string ExtractModelName(string title, string keyword, string[] allKeywords)
    {
        // Ищем модель как слово после ключевого слова марки
        var words = title.Split([' ', '«', '»', ',', '.', ':', '-', '—', '('], StringSplitOptions.RemoveEmptyEntries);
        var kwIdx = -1;

        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                kwIdx = i;
                break;
            }
        }

        if (kwIdx >= 0 && kwIdx + 1 < words.Length)
        {
            var candidate = words[kwIdx + 1].Trim('«', '»', '"');
            // Проверяем, не является ли это просто годом или числом
            if (candidate.Length >= 2 && !int.TryParse(candidate, out _))
                return candidate;
        }

        // Ищем слово-модель (обычно латиница с цифрами или заглавное слово)
        var modelMatch = Regex.Match(title, @"\b([A-ZА-Я][a-zа-я]+\s*(?:\d{2,3}|NG|FL|Plus|Cross|Sport|NN|Pro|Max)?)\b");
        if (modelMatch.Success)
        {
            var m = modelMatch.Groups[1].Value;
            // Не должно быть в списке ключевых слов марки
            if (!allKeywords.Any(k => m.Equals(k, StringComparison.OrdinalIgnoreCase)))
                return m;
        }

        return "";
    }

    private static string FindPossibleModel(string title, string[] allKeywords)
    {
        var words = title.Split([' ', '«', '»', ',', '.', ':', '-', '—', '('], StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
        {
            var clean = w.Trim('«', '»', '"');
            if (clean.Length >= 3
                && !allKeywords.Any(k => clean.Contains(k, StringComparison.OrdinalIgnoreCase))
                && !int.TryParse(clean, out _))
                return clean;
        }
        return "";
    }

    private static (string, string) FallbackExtract(string title)
    {
        // Простой fallback: первые два значимых слова
        var words = title.Split([' ', '«', '»', ',', '.', ':', '-', '—'], StringSplitOptions.RemoveEmptyEntries);
        var significant = words
            .Select(w => w.Trim('«', '»', '"'))
            .Where(w => w.Length >= 3 && !int.TryParse(w, out _))
            .Take(2)
            .ToList();

        if (significant.Count == 2)
            return (significant[0], significant[1]);
        if (significant.Count == 1)
            return (significant[0], "");

        return ("", "");
    }

    // ──────────────────────────────────────────────
    // Определение атрибутов
    // ──────────────────────────────────────────────

    private static string DetectGeneration(string title, string snippet)
    {
        var t = (title + " " + snippet).ToLowerInvariant();

        if (t.Contains("iii") || t.Contains("третье поколение") || t.Contains("3 поколение"))
            return "III";
        if (t.Contains("ii") || t.Contains("второе поколение") || t.Contains("2 поколение"))
            return "II";
        if (t.Contains("новое поколение") || t.Contains("next generation") || t.Contains("ng"))
            return "новое поколение";
        if (t.Contains("рестайлинг") || t.Contains("restyling") || t.Contains("фейслифт") || t.Contains("facelift") || t.Contains("fl"))
            return "рестайлинг";

        return "I";
    }

    private static int? ExtractYear(string text)
    {
        // Ищем год 2024-2030
        var match = Regex.Match(text, @"\b(20[2-9]\d)\b");
        if (match.Success)
            return int.Parse(match.Groups[1].Value);

        return null;
    }

    private static string DetectBodyType(string text)
    {
        var t = text.ToLowerInvariant();

        if (t.Contains("кроссовер") || t.Contains("crossover") || t.Contains("suv")) return "кроссовер";
        if (t.Contains("внедорожник") || t.Contains("offroad")) return "внедорожник";
        if (t.Contains("седан") || t.Contains("sedan")) return "седан";
        if (t.Contains("хэтчбек") || t.Contains("hatchback")) return "хэтчбек";
        if (t.Contains("универсал") || t.Contains("wagon") || t.Contains("estate")) return "универсал";
        if (t.Contains("лифтбек") || t.Contains("liftback")) return "лифтбек";
        if (t.Contains("пикап") || t.Contains("pickup")) return "пикап";
        if (t.Contains("фургон") || t.Contains("van")) return "фургон";
        if (t.Contains("микроавтобус") || t.Contains("minibus")) return "микроавтобус";
        if (t.Contains("купе") || t.Contains("coupe")) return "купе";
        if (t.Contains("кабриолет") || t.Contains("cabrio")) return "кабриолет";

        return "";
    }

    private static string DetectEngineType(string text)
    {
        var t = text.ToLowerInvariant();
        var types = new List<string>();

        if (t.Contains("бензин") || t.Contains("petrol") || t.Contains("gasoline"))
        {
            var match = Regex.Match(text, @"(\d[,\.]\d)\s*(?:л|L)", RegexOptions.IgnoreCase);
            types.Add(match.Success ? $"бензин {match.Groups[1].Value}" : "бензин");
        }
        if (t.Contains("дизель") || t.Contains("diesel"))
        {
            var match = Regex.Match(text, @"(\d[,\.]\d)\s*(?:л|L)", RegexOptions.IgnoreCase);
            types.Add(match.Success ? $"дизель {match.Groups[1].Value}" : "дизель");
        }
        if (t.Contains("электро") || t.Contains("electric") || t.Contains("ev") || t.Contains("электрокар"))
            types.Add("электро");
        if (t.Contains("гибрид") || t.Contains("hybrid") || t.Contains("phev"))
            types.Add("гибрид");
        if (t.Contains("газ") || t.Contains("cng") || t.Contains("lpg"))
            types.Add("газ");

        return string.Join(", ", types);
    }

    private static string DetectStatus(string title, string snippet)
    {
        var t = (title + " " + snippet).ToLowerInvariant();

        if (t.Contains("анонс") || t.Contains("announced") || t.Contains("презентац") ||
            t.Contains("показали") || t.Contains("представлен") || t.Contains("дебют"))
            return "announced";

        if (t.Contains("старт продаж") || t.Contains("в продаже") || t.Contains("поступил") ||
            t.Contains("начали выпуск") || t.Contains("сошёл с конвейер") || t.Contains("старт производств"))
            return "in_production";

        if (t.Contains("снят с производств") || t.Contains("discontinued") || t.Contains("прекращён"))
            return "discontinued";

        if (t.Contains("слух") || t.Contains("rumor") || t.Contains("инсайд") || t.Contains("возможно"))
            return "rumored";

        return "announced";
    }

    private static string DetectFactory(string brand) => brand switch
    {
        "LADA" => "АвтоВАЗ",
        "УАЗ" => "УАЗ",
        "ГАЗ" => "ГАЗ",
        "Москвич" => "Москвич",
        "Evolute" => "Моторинвест",
        "Xcite" => "Автозавод СПб",
        "AmberAuto" => "Автотор",
        "Aurus" => "НАМИ",
        "КАМАЗ" => "КАМАЗ",
        "Sollers" => "Соллерс",
        _ => ""
    };

    // ──────────────────────────────────────────────
    // Вспомогательные
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
        if (url.StartsWith("//")) return "https:" + url;
        if (!url.StartsWith("http")) return "";

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

    // ──────────────────────────────────────────────
    // Поиск новых двигателей и систем
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ищет новости о новых двигателях, трансмиссиях, ЭБУ и системах российских авто.
    /// </summary>
    private async Task<List<RussianAutoEngine>> SearchForEnginesAsync()
    {
        var results = new List<RussianAutoEngine>();

        var engineQueries = new (string Label, string RecordType)[]
        {
            ("новый двигатель АвтоВАЗ LADA 2026", "engine"),
            ("новый дизельный двигатель УАЗ ЗМЗ 2026", "engine"),
            ("новый двигатель ГАЗ 2026", "engine"),
            ("российский автомобильный двигатель новый 2026", "engine"),
            ("новая трансмиссия вариатор АКПП LADA 2026", "transmission"),
            ("новый ЭБУ контроллер двигателя АвтоВАЗ Итэлма 2026", "ecu"),
            ("LADA Vesta новая мультимедиа электроника 2026", "ecu"),
            ("российский электродвигатель для авто 2026", "electric"),
            ("гибридная силовая установка российский авто 2026", "hybrid"),
            ("импортозамещение ЭБУ двигатель российский 2026", "ecu"),
            ("новый протокол диагностики OBD российский авто", "ecu"),
            ("ADAS система помощи водителю LADA УАЗ 2026", "ecu"),
        };

        foreach (var (query, recordType) in engineQueries)
        {
            if (results.Count >= 20) break;

            try
            {
                var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
                var html = await _http.GetStringAsync(searchUrl);
                var matches = ParseDdgResults(html);

                foreach (var (title, url, snippet) in matches.Take(3))
                {
                    var (brand, engineName) = ExtractEngineInfo(title + " " + snippet);

                    if (string.IsNullOrEmpty(brand))
                        brand = InferBrandFromText(title + " " + snippet);

                    if (string.IsNullOrEmpty(brand) && string.IsNullOrEmpty(engineName))
                        continue;

                    // Дедупликация
                    var engineCode = ExtractEngineCode(title + " " + snippet);
                    if (await _svc.ExistsEngineAsync(engineCode, brand, engineName))
                        continue;

                    var engine = new RussianAutoEngine
                    {
                        Brand = brand,
                        EngineCode = engineCode,
                        EngineName = Truncate(engineName, 100),
                        FuelType = DetectEngineFuelType(title + " " + snippet),
                        Displacement = ExtractDisplacement(title + " " + snippet),
                        PowerHP = ExtractEnginePower(title + " " + snippet),
                        FuelSystem = DetectFuelSystem(title + " " + snippet),
                        Turbo = DetectTurbo(title + " " + snippet),
                        EmissionClass = DetectEmissionClass(title + " " + snippet),
                        Transmission = DetectNewTransmission(title + " " + snippet),
                        TransmissionVendor = DetectTransmissionVendor(title + " " + snippet),
                        ECUType = DetectNewECU(title + " " + snippet),
                        ECUVendor = DetectECUVendor(title + " " + snippet),
                        OBDProtocol = DetectNewOBDProtocol(title + " " + snippet),
                        RecordType = recordType,
                        IsNew = true,
                        Status = DetectEngineStatus(title, snippet),
                        Factory = DetectEngineFactory(brand),
                        Source = ExtractDomain(url),
                        SourceUrl = url,
                        Notes = $"Найдено по: {query}",
                        DetectedAt = DateTime.UtcNow,
                    };

                    results.Add(engine);
                }

                await Task.Delay(TimeSpan.FromSeconds(1.5));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoMarketMonitor] Engine search '{query}': {ex.Message}");
            }
        }

        return results;
    }

    private static string ExtractEngineCode(string text)
    {
        // ВАЗ-21179, ЗМЗ-40906, УМЗ-4216, HFC4GB2.4E, G4FG
        var m = Regex.Match(text, @"\b([А-Я]+-?\d{4,6}(?:\.\d+)?|[A-Z]+[\dA-Z]+(?:\.\d+)?[A-Z]?)\b");
        return m.Success ? m.Groups[1].Value : "";
    }

    private static (string Brand, string EngineName) ExtractEngineInfo(string text)
    {
        var brandMap = new (string keyword, string brand)[]
        {
            ("АвтоВАЗ", "LADA"), ("LADA", "LADA"), ("Lada", "LADA"),
            ("УАЗ", "УАЗ"), ("UAZ", "УАЗ"),
            ("ГАЗ", "ГАЗ"), ("GAZ", "ГАЗ"),
            ("ЗМЗ", "УАЗ"), ("УМЗ", "ГАЗ"),
            ("Москвич", "Москвич"), ("Moskvich", "Москвич"),
            ("Evolute", "Evolute"), ("Эволют", "Evolute"),
            ("Xcite", "Xcite"),
            ("AmberAuto", "AmberAuto"),
            ("Aurus", "Aurus"), ("Аурус", "Aurus"),
        };

        string brand = "";
        string engineName = "";

        foreach (var (kw, br) in brandMap)
        {
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                brand = br;
                break;
            }
        }

        // Ищем название двигателя: литраж + технология
        var dispMatch = Regex.Match(text, @"(\d[,\.]\d)\s*(?:л|L|литр)");
        if (dispMatch.Success)
        {
            var disp = dispMatch.Groups[1].Value;
            // Что после литража?
            var afterIdx = dispMatch.Index + dispMatch.Length;
            var after = text.Length > afterIdx + 30 ? text[afterIdx..(afterIdx + 30)] : text[afterIdx..];
            var label = "";

            if (Regex.IsMatch(after, @"турбо|turbo", RegexOptions.IgnoreCase))
                label = "Турбо";
            else if (Regex.IsMatch(after, @"атмосфер|atmo", RegexOptions.IgnoreCase))
                label = "Атмосферный";

            engineName = $"{disp} {label}".Trim();
        }

        // Fallback: ищем что-то похожее на модель двигателя
        if (string.IsNullOrEmpty(engineName))
        {
            var codeMatch = Regex.Match(text, @"\b([А-Я]+\s*\d{4,6}(?:\.\d+)?)\b");
            if (codeMatch.Success)
                engineName = codeMatch.Groups[1].Value;
        }

        return (brand, engineName);
    }

    private static string InferBrandFromText(string text)
    {
        if (text.Contains("АвтоВАЗ", StringComparison.OrdinalIgnoreCase) || text.Contains("Лада", StringComparison.OrdinalIgnoreCase))
            return "LADA";
        if (text.Contains("ВАЗ", StringComparison.OrdinalIgnoreCase) && !text.Contains("УАЗ", StringComparison.OrdinalIgnoreCase))
            return "LADA";
        if (text.Contains("УАЗ", StringComparison.OrdinalIgnoreCase))
            return "УАЗ";
        if (text.Contains("ГАЗ", StringComparison.OrdinalIgnoreCase) || text.Contains("Газель", StringComparison.OrdinalIgnoreCase))
            return "ГАЗ";
        if (text.Contains("Москвич", StringComparison.OrdinalIgnoreCase))
            return "Москвич";
        if (text.Contains("Evolute", StringComparison.OrdinalIgnoreCase) || text.Contains("Эволют", StringComparison.OrdinalIgnoreCase))
            return "Evolute";
        return "";
    }

    private static double? ExtractDisplacement(string text)
    {
        var m = Regex.Match(text, @"(\d[,\.]\d)\s*(?:л|L|литр)");
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    private static int? ExtractEnginePower(string text)
    {
        // "150 л.с.", "150 лс", "150 hp", "150 л. с."
        var m = Regex.Match(text, @"(\d{2,4})\s*(?:л\.?\s*с\.?|hp)", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var hp))
            return hp;
        return null;
    }

    private static string DetectEngineFuelType(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("электро") || t.Contains("electric") || t.Contains("ev")) return "электро";
        if (t.Contains("гибрид") || t.Contains("hybrid") || t.Contains("phev")) return "гибрид";
        if (t.Contains("дизель") || t.Contains("diesel") || t.Contains("тди")) return "дизель";
        if (t.Contains("газ") || t.Contains("cng") || t.Contains("метан")) return "газ";
        if (t.Contains("бензин") || t.Contains("petrol") || t.Contains("gasoline")) return "бензин";
        return "бензин";
    }

    private static string DetectFuelSystem(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("прямой впрыск") || t.Contains("direct inject") || t.Contains("gdi") || t.Contains("tsi"))
            return "прямой впрыск";
        if (t.Contains("распределённый") || t.Contains("mpi") || t.Contains("port inject"))
            return "распределённый впрыск";
        if (t.Contains("common rail"))
            return "Common Rail";
        return "";
    }

    private static string DetectTurbo(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("битурбо") || t.Contains("twin turbo")) return "битурбо";
        if (t.Contains("турбо") || t.Contains("turbo") || t.Contains("наддув")) return "турбо";
        if (t.Contains("компрессор") || t.Contains("supercharg")) return "компрессор";
        if (t.Contains("атмосфер") || t.Contains("atmo")) return "атмосферный";
        return "";
    }

    private static string DetectEmissionClass(string text)
    {
        var m = Regex.Match(text, @"(?:Евро|Euro)[-\s]?(\d+[a-z]?)", RegexOptions.IgnoreCase);
        if (m.Success) return $"Евро-{m.Groups[1].Value}";
        if (text.Contains("China-VI", StringComparison.OrdinalIgnoreCase)) return "China-VI";
        return "";
    }

    private static string DetectNewTransmission(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("вариатор") || t.Contains("cvt")) return "CVT";
        if (t.Contains("робот") || t.Contains("amt") || t.Contains("ркпп") || t.Contains("dct")) return "РКПП";
        if (t.Contains("автомат") || t.Contains("акпп") || t.Contains("гидротрансформатор")) return "АКПП";
        if (t.Contains("механика") || t.Contains("мкпп") || t.Contains("manual")) return "МКПП";
        return "";
    }

    private static string DetectTransmissionVendor(string text)
    {
        if (text.Contains("Jatco", StringComparison.OrdinalIgnoreCase)) return "Jatco";
        if (text.Contains("Aisin", StringComparison.OrdinalIgnoreCase)) return "Aisin";
        if (text.Contains("Punch", StringComparison.OrdinalIgnoreCase)) return "Punch";
        if (text.Contains("Dymos", StringComparison.OrdinalIgnoreCase)) return "Dymos";
        if (text.Contains("АвтоВАЗ", StringComparison.OrdinalIgnoreCase)) return "АвтоВАЗ";
        if (text.Contains("ГАЗ", StringComparison.OrdinalIgnoreCase)) return "ГАЗ";
        return "";
    }

    private static string DetectNewECU(string text)
    {
        var m = Regex.Match(text, @"(?:ЭБУ|ECU|контроллер)\s*(.{3,30})", RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim();

        // Поиск по известным типам
        foreach (var pattern in new[] { @"Bosch\s+(?:ME|MED|EDC)\d[\d.]*", @"Микас\s+\d+\.\d+",
                   @"Delphi\s+MT\d+", @"Siemens\s+\w+\d+", @"Январь\s+\d+\.\d+" })
        {
            m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
        }
        return "";
    }

    private static string DetectECUVendor(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("bosch")) return "Bosch";
        if (t.Contains("итэлма") || t.Contains("itelma")) return "Итэлма";
        if (t.Contains("delphi")) return "Delphi";
        if (t.Contains("siemens") || t.Contains("continental")) return "Siemens/Continental";
        if (t.Contains("автоваз")) return "АвтоВАЗ";
        return "";
    }

    private static string DetectNewOBDProtocol(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("uds")) return "OBD2/UDS";
        if (t.Contains("doip")) return "OBD2/DoIP";
        if (t.Contains("can") || t.Contains("шина")) return "OBD2/CAN";
        if (t.Contains("k-line") || t.Contains("kwp")) return "K-Line/KWP2000";
        if (t.Contains("obd2") || t.Contains("obd-2") || t.Contains("eo")) return "OBD2/EOBD";
        return "";
    }

    private static string DetectEngineStatus(string title, string snippet)
    {
        var t = (title + " " + snippet).ToLowerInvariant();
        if (t.Contains("сертифицирован") || t.Contains("одобрен") || t.Contains("certified")) return "certified";
        if (t.Contains("испытани") || t.Contains("тест") || t.Contains("testing")) return "testing";
        if (t.Contains("начали") || t.Contains("старт") || t.Contains("конвейер") || t.Contains("выпуск")) return "in_production";
        return "announced";
    }

    private static string DetectEngineFactory(string brand) => brand switch
    {
        "LADA" => "АвтоВАЗ",
        "УАЗ" => "ЗМЗ",
        "ГАЗ" => "УМЗ/ГАЗ",
        "Evolute" => "Моторинвест",
        "Aurus" => "НАМИ",
        _ => ""
    };

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
                $"rusautos_report_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);
            Debug.WriteLine($"[AutoMarketMonitor] Report saved to {path}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoMarketMonitor] Report error: {ex.Message}");
        }
    }

    // ──────────────────────────────────────────────
    // Поиск обновлений существующих моделей
    // ──────────────────────────────────────────────

    /// <summary>
    /// Ищет обновления для моделей, уже известных системе.
    /// Проходит по каждой модели из seed-данных и ищет рестайлинги, новые поколения и т.д.
    /// </summary>
    private async Task<List<RussianAutoModelUpdate>> SearchForModelUpdatesAsync()
    {
        var results = new List<RussianAutoModelUpdate>();

        // Приоритетные модели (флагманы, чаще обновляются)
        var priorityModels = new (string Brand, string ModelName)[]
        {
            ("LADA", "Vesta NG"),
            ("LADA", "Granta FL"),
            ("LADA", "Niva Legend"),
            ("LADA", "Niva Travel"),
            ("LADA", "Largus"),
            ("LADA", "Aura"),
            ("УАЗ", "Patriot FL"),
            ("УАЗ", "Hunter"),
            ("ГАЗ", "Газель NN"),
            ("ГАЗ", "Соболь NN"),
            ("Москвич", "Москвич 3"),
            ("Москвич", "Москвич 6"),
            ("Москвич", "Москвич 8"),
            ("Evolute", "i-Pro"),
            ("Evolute", "i-Joy"),
            ("Xcite", "X-Cross 7"),
        };

        // Типы запросов для обновлений
        var updateQueries = new (string Prefix, string UpdateType)[]
        {
            ("рестайлинг обновление", "restyling"),
            ("новое поколение", "new_generation"),
            ("новый двигатель мотор", "new_engine"),
            ("новая комплектация версия", "new_trim"),
            ("технические изменения обновление", "tech_update"),
            ("снят с производства прекращён", "discontinued"),
        };

        foreach (var (brand, model) in priorityModels)
        {
            foreach (var (prefix, updateType) in updateQueries)
            {
                if (results.Count >= 30) // лимит на один прогон
                    break;

                try
                {
                    var query = $"{brand} {model} {prefix} 2026";
                    var searchUrl = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";

                    var html = await _http.GetStringAsync(searchUrl);
                    var matches = ParseDdgResults(html);

                    foreach (var (title, url, snippet) in matches.Take(3))
                    {
                        // Проверяем, действительно ли контент про обновление
                        if (!MatchesUpdateType(title + " " + snippet, updateType))
                            continue;

                        var description = Truncate(snippet, 250);
                        if (string.IsNullOrWhiteSpace(description))
                            description = title;

                        // Дедупликация
                        var descPrefix = description.Length > 30 ? description[..30] : description;
                        if (await _svc.ExistsUpdateAsync(brand, model, updateType, descPrefix))
                            continue;

                        var year = ExtractYear(title + " " + snippet);

                        var update = new RussianAutoModelUpdate
                        {
                            Brand = brand,
                            ModelName = model,
                            UpdateType = updateType,
                            Description = Truncate(description, 300),
                            Year = year,
                            Source = ExtractDomain(url),
                            SourceUrl = url,
                            AffectsDiagnostics = updateType is "new_generation" or "tech_update" or "new_engine",
                            DetectedAt = DateTime.UtcNow,
                        };

                        results.Add(update);
                    }

                    // Пауза чтобы не забанили DDG
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AutoMarketMonitor] Update search '{brand} {model} {prefix}': {ex.Message}");
                }
            }
        }

        return results;
    }

    /// <summary>Проверяет, соответствует ли контент типу обновления.</summary>
    private static bool MatchesUpdateType(string text, string updateType)
    {
        var t = text.ToLowerInvariant();

        return updateType switch
        {
            "restyling" => t.Contains("рестайлинг") || t.Contains("restyling") ||
                           t.Contains("фейслифт") || t.Contains("facelift") ||
                           t.Contains("обновлённ") || t.Contains("рестайл"),

            "new_generation" => t.Contains("поколение") || t.Contains("generation") ||
                                t.Contains("смена поколен") || t.Contains("новый кузов"),

            "new_engine" => t.Contains("двигател") || t.Contains("engine") ||
                           t.Contains("мотор") || t.Contains("турбо") ||
                           t.Contains("гибрид") || t.Contains("электр"),

            "new_trim" => t.Contains("комплектаци") || t.Contains("верси") ||
                         t.Contains("оснащение") || t.Contains("trim") ||
                         t.Contains("люкс") || t.Contains("премиум"),

            "tech_update" => t.Contains("техническ") || t.Contains("технолог") ||
                            t.Contains("электроник") || t.Contains("мультимеди") ||
                            t.Contains("платформ") || t.Contains("шасси") ||
                            t.Contains("подвеск") || t.Contains("трансмисси"),

            "discontinued" => t.Contains("снят с производств") || t.Contains("discontinued") ||
                             t.Contains("прекращ") || t.Contains("конвейер") ||
                             t.Contains("последн") || t.Contains("проща"),

            _ => false,
        };
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text;
        return text[..maxLen] + "…";
    }

    public async Task<string> ForceRunAsync()
    {
        if (_isRunning) return "⏳ Мониторинг уже выполняется…";
        return await RunCheckAsync();
    }
}
