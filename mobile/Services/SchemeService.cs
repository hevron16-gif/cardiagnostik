using CarDiagnosticApp.Models;
using Newtonsoft.Json.Linq;
using System.Text;
using PointF = Microsoft.Maui.Graphics.PointF;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Модуль схем узлов — загрузка с сервера, кеширование, построение 2D-диаграмм.
/// Использует ApiService для HTTP, DiagramDbService для локального хранения.
/// </summary>
public class SchemeService
{
    private readonly ApiService _api;
    private readonly DiagramDbService _diagramDb;
    private readonly LicenseService _license;

    public SchemeService(ApiService api, DiagramDbService diagramDb, LicenseService license)
    {
        _api = api;
        _diagramDb = diagramDb;
        _license = license;
    }

    // ══════════════ Получение схемы по коду ошибки ══════════════

    /// <summary>
    /// Получить EngineDiagram для отображения. Если нет в кеше — загружает с сервера.
    /// БЕСПЛАТНАЯ версия: возвращает заглушку с предложением апгрейда.
    /// PAID (Pro/Enterprise): возвращает полноценную схему.
    /// </summary>
    public async Task<EngineDiagram?> GetDiagramAsync(
        string errorCode, string carBrand, string carModel)
    {
        // FREE: заглушка (пропускается при LicenseService.TestingUnlockAll / IsPaid)
        if (!_license.IsPaid)
        {
            return CreateUpgradeStub(errorCode, carBrand, carModel);
        }

        // ═══ PAID / тестовый unlock: полная схема ═══
        // 1. Локальный mapping (ВАЗ/КАМАЗ/…) — быстрее и без чужих марок
        try
        {
            var local = CarDiagnosticApp.Data.DiagramDatabase.GetDiagram(carBrand, carModel);
            if (local != null && local.Views.Count > 0)
                return local;
        }
        catch { }

        // 2. Проверяем локальный SQLite-кеш
        var cached = await _diagramDb.GetDiagramAsync(carBrand, carModel, errorCode);
        if (cached != null)
            return cached;

        // fallback: поиск по коду ошибки ТОЛЬКО своей марки
        cached = await _diagramDb.GetDiagramByCodeForBrandAsync(errorCode, carBrand);
        if (cached != null)
            return cached;

        // 2. Запрашиваем с сервера
        var raw = await FetchRawSchemaAsync(errorCode);
        if (raw == null)
            return null;

        // 3. Парсим и строим EngineDiagram
        var diagram = BuildDiagram(raw, errorCode, carBrand, carModel);
        if (diagram == null)
            return null;

        // 4. Если есть image_url — скачиваем и кешируем изображение
        var imageUrl = raw["data"]?.Value<string>("image_url");
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            diagram.ImagePath = await DownloadAndCacheImageAsync(imageUrl, errorCode);
        }

        // 5. Сохраняем в локальный кеш
        await _diagramDb.SaveDiagramAsync(carBrand, carModel, errorCode, diagram, "server");

        return diagram;
    }

    /// <summary>
    /// Заглушка для бесплатной версии — сообщение о необходимости апгрейда.
    /// </summary>
    public EngineDiagram CreateUpgradeStub(string errorCode, string carBrand, string carModel)
    {
        return new EngineDiagram
        {
            Id = "upgrade-stub",
            ErrorCode = errorCode,
            CarBrand = carBrand,
            CarModel = carModel,
            Title = "🔒 Схема узлов — Premium",
            Description = "Схемы узлов доступны в версии Pro (499 ₽/мес).",
            Views = new List<DiagramView>
            {
                new DiagramView
                {
                    ViewId = "upgrade",
                    ViewName = "Апгрейд",
                    BackgroundLabel = "Оформите Pro для доступа к схемам",
                    Components = new List<DiagramComponent>
                    {
                        new DiagramComponent
                        {
                            Id = "upgrade_btn",
                            Name = "Перейти на Pro →",
                            Category = "Апгрейд",
                            DefaultColor = "#FF6D00",
                            HighlightLevel = 3,
                            Outline = new List<PointF>
                            {
                                new(0.25f, 0.35f), new(0.75f, 0.35f),
                                new(0.75f, 0.65f), new(0.25f, 0.65f),
                            },
                        },
                    },
                },
            },
            Checklist = new List<string>
            {
                "✅ Офлайн-база кодов: Free",
                "✅ Чтение ошибок ELM327: Free",
                "✅ История (последние 10): Free",
                "",
                "🔒 Схемы узлов (2D): Pro",
                "🔒 AI-диагностика DeepSeek: Pro",
                "🔒 Живые данные + графики: Pro",
                "🔒 Самообучение ChromaDB: Pro",
                "🔒 Облачная синхронизация: Pro",
            },
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Быстрый запрос: есть ли схема для этого кода?
    /// Free: всегда false (схемы недоступны).
    /// </summary>
    public async Task<bool> IsSchemaAvailableAsync(string errorCode, string carBrand = "", string carModel = "")
    {
        if (!_license.IsPaid)
            return false;

        // Локальные mapping всегда доступны в paid/test
        try
        {
            var local = CarDiagnosticApp.Data.DiagramDatabase.GetDiagram(carBrand, carModel);
            if (local != null && local.Views.Count > 0)
                return true;
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(carBrand))
        {
            var cached = await _diagramDb.GetDiagramAsync(carBrand, carModel, errorCode);
            if (cached != null) return true;
        }

        var byCode = await _diagramDb.GetDiagramByCodeForBrandAsync(errorCode, carBrand);
        if (byCode != null) return true;

        var raw = await FetchRawSchemaAsync(errorCode);
        return raw != null && raw.Value<bool?>("available") == true;
    }

    /// <summary>
    /// Сообщение для free-пользователя при попытке открыть схему.
    /// </summary>
    public string GetUpgradeMessage(string errorCode)
    {
        return $"🔒 СХЕМЫ — PRO\n\nСхема для кода {errorCode} доступна в версии Pro.\n\n"
               + "В бесплатной версии вы уже можете:\n"
               + "✅ Читать ошибки через ELM327\n"
               + "✅ Расшифровывать коды (офлайн)\n"
               + "✅ Просматривать историю (10 записей)\n\n"
               + "Оформите Pro за 499 ₽/мес и получите:\n"
               + "🔒 Интерактивные 2D-схемы узлов\n"
               + "🤖 AI-анализ (DeepSeek)\n"
               + "🧠 Самообучение (ChromaDB)\n"
               + "📈 Живые графики\n"
               + "☁️ Облачная синхронизация";
    }

    /// <summary>
    /// Скачивает изображение схемы и сохраняет локально.
    /// Возвращает путь к файлу в FilesDir.
    /// </summary>
    public async Task<string?> DownloadSchemaImageAsync(string errorCode, string imageUrl)
    {
        return await DownloadAndCacheImageAsync(imageUrl, errorCode);
    }

    // ══════════════ Приватные методы ══════════════

    private async Task<JObject?> FetchRawSchemaAsync(string errorCode)
    {
        try
        {
            // user_id=test — на тестовом сервере; paywall всё равно может ответить available:false
            var url = $"/schemas/{Uri.EscapeDataString(errorCode)}?user_id=test";
            var json = await _api.GetRawAsync(url);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var jo = JObject.Parse(json);
            // Если сервер требует Pro — не считаем ошибкой, клиент покажет локальную схему
            if (jo.Value<bool?>("available") == false)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SchemeService] server schema blocked: {jo.Value<string>("message")}");
                return null;
            }
            return jo;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SchemeService] FetchRaw: {ex.Message}");
            return null;
        }
    }

    private static EngineDiagram? BuildDiagram(
        JObject raw, string errorCode, string carBrand, string carModel)
    {
        if (raw.Value<bool?>("available") != true)
            return null;

        var data = raw["data"];
        if (data == null)
            return null;

        var title = data.Value<string>("title") ?? $"Схема — {errorCode}";
        var description = data.Value<string>("description") ?? "";
        var imageUrl = data.Value<string>("image_url");

        var nodes = data["nodes"] as JArray;
        var checkpoints = data["checkpoints"] as JArray;

        // Строим компоненты
        var components = new List<DiagramComponent>();
        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                var id = node.Value<int>("id");
                var label = node.Value<string>("label") ?? $"Узел {id}";
                var x = node.Value<float>("x");
                var y = node.Value<float>("y");
                var links = node["links"] as JArray;

                // Генерируем прямоугольный полигон вокруг позиции узла
                var outline = GenerateNodeOutline(x, y, label.Length);

                components.Add(new DiagramComponent
                {
                    Id = $"node_{id}",
                    Name = label,
                    Category = DetermineCategory(label, errorCode),
                    DefaultColor = DetermineColor(label, errorCode),
                    HighlightLevel = IsPrimaryNode(label, errorCode) ? 3 : 0,
                    Outline = outline,
                });
            }
        }

        // Строим чек-поинты
        var checklist = new List<string>();
        if (checkpoints != null)
        {
            foreach (var cp in checkpoints)
                checklist.Add(cp.Value<string>() ?? "");
        }

        return new EngineDiagram
        {
            Id = Guid.NewGuid().ToString(),
            ErrorCode = errorCode,
            CarBrand = carBrand,
            CarModel = carModel,
            Title = title,
            Description = description,
            ImageUrl = imageUrl,
            ImagePath = null, // будет заполнено позже
            Views = new List<DiagramView>
            {
                new DiagramView
                {
                    ViewId = "main",
                    ViewName = "Основная схема",
                    BackgroundLabel = title,
                    Components = components,
                },
            },
            Checklist = checklist,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Генерирует полигон-прямоугольник вокруг позиции (x, y).
    /// Нормализует в относительные координаты 0..1.
    /// </summary>
    private static List<PointF> GenerateNodeOutline(float x, float y, int labelLen)
    {
        // Нормализуем x: 100..700 → 0..1, y: 50..150 → 0..1
        float nx = (x - 50f) / 700f;
        float ny = (y - 20f) / 200f;
        float w = Math.Max(0.06f, labelLen * 0.004f);  // ширина от длины текста
        float h = 0.06f;

        // Clamp to 0..1
        nx = Math.Clamp(nx, 0.02f, 0.98f);
        ny = Math.Clamp(ny, 0.02f, 0.98f);

        float halfW = w / 2;
        float halfH = h / 2;

        return new List<PointF>
        {
            new(nx - halfW, ny - halfH),
            new(nx + halfW, ny - halfH),
            new(nx + halfW, ny + halfH),
            new(nx - halfW, ny + halfH),
        };
    }

    /// <summary>
    /// Скачивает изображение в FilesDir и возвращает локальный путь.
    /// </summary>
    private async Task<string?> DownloadAndCacheImageAsync(string imageUrl, string errorCode)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var bytes = await client.GetByteArrayAsync(imageUrl);

            var dir = Path.Combine(FileSystem.AppDataDirectory, "schemes");
            Directory.CreateDirectory(dir);

            var ext = Path.GetExtension(imageUrl)?.Split('?')[0];
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".png";

            var fileName = $"{errorCode}_{DateTime.UtcNow:yyyyMMddHHmmss}{ext}";
            var path = Path.Combine(dir, fileName);

            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ══════════════ Эвристики категорий и цветов ══════════════

    private static string DetermineCategory(string label, string errorCode)
    {
        var lower = label.ToLowerInvariant();
        if (lower.Contains("топлив") || lower.Contains("форсунк") || lower.Contains("насос") ||
            lower.Contains("рампа") || lower.Contains("бензо") || lower.Contains("fuel"))
            return "Топливная система";

        if (lower.Contains("воздух") || lower.Contains("впуск") || lower.Contains("дроссель") ||
            lower.Contains("mаf") || lower.Contains("маf") || lower.Contains("фильтр") ||
            lower.Contains("турб") || lower.Contains("интеркулер"))
            return "Система впуска";

        if (lower.Contains("зажиган") || lower.Contains("свеч") || lower.Contains("катушк") ||
            lower.Contains("искр") || lower.Contains("пробой"))
            return "Система зажигания";

        if (lower.Contains("выпуск") || lower.Contains("катализ") || lower.Contains("лямбда") ||
            lower.Contains("глушитель") || lower.Contains("егр") || lower.Contains("egr") ||
            lower.Contains("выхлоп"))
            return "Система выпуска";

        if (lower.Contains("эбу") || lower.Contains("ecu") || lower.Contains("провод") ||
            lower.Contains("датчик") || lower.Contains("сенсор") || lower.Contains("can"))
            return "Электроника";

        if (lower.Contains("тормоз") || lower.Contains("abs") || lower.Contains("esp"))
            return "Тормозная система";

        return "Общее";
    }

    private static string DetermineColor(string label, string errorCode)
    {
        var cat = DetermineCategory(label, errorCode);
        return cat switch
        {
            "Топливная система" => "#FFA726",     // оранжевый
            "Система впуска" => "#42A5F5",        // синий
            "Система зажигания" => "#EF5350",     // красный
            "Система выпуска" => "#66BB6A",       // зелёный
            "Электроника" => "#AB47BC",           // фиолетовый
            "Тормозная система" => "#EC407A",     // розовый
            _ => "#78909C",                       // серый
        };
    }

    private static bool IsPrimaryNode(string label, string errorCode)
    {
        // Первичный узел — тот, что непосредственно связан с кодом ошибки
        var lower = label.ToLowerInvariant();
        var code = errorCode.ToUpperInvariant();

        return code switch
        {
            "P0171" => lower.Contains("впуск") || lower.Contains("вакуум"),
            "P0172" => lower.Contains("форсунк") || lower.Contains("давлен"),
            "P0300" or "P0301" or "P0302" or "P0303" or "P0304"
                => lower.Contains("свеч") || lower.Contains("катушк"),
            "P0420" or "P0430"
                => lower.Contains("катализ"),
            "P0100" or "P0101" or "P0102" or "P0103"
                => lower.Contains("mаf") || lower.Contains("маf") || lower.Contains("расход"),
            "P0115" or "P0117" or "P0118"
                => lower.Contains("температур") && lower.Contains("охлажд"),
            "P0130" or "P0131" or "P0132" or "P0133" or "P0135"
                => lower.Contains("лямбда") || lower.Contains("кислород"),
            "P0400" or "P0401" or "P0402"
                => lower.Contains("егр") || lower.Contains("egr"),
            "P0440" or "P0442" or "P0455"
                => lower.Contains("адсорб") || lower.Contains("evap") || lower.Contains("утечк"),
            _ => false,
        };
    }
}
