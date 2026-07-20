using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarDiagnosticApp.Models;
using PointF = Microsoft.Maui.Graphics.PointF;

namespace CarDiagnosticApp.Data
{
    /// <summary>
    /// JSON-модель для десериализации mapping_*.json (snake_case).
    /// </summary>
    public class MappingJson
    {
        [JsonPropertyName("brand")]
        public string Brand { get; set; } = "";

        [JsonPropertyName("engine_name")]
        public string EngineName { get; set; } = "";

        [JsonPropertyName("engine_type")]
        public string EngineType { get; set; } = "";

        [JsonPropertyName("layout")]
        public string Layout { get; set; } = "";

        [JsonPropertyName("difference_notes")]
        public string DifferenceNotes { get; set; } = "";

        [JsonPropertyName("is_fallback")]
        public bool IsFallback { get; set; }

        [JsonPropertyName("views")]
        public List<MappingView> Views { get; set; } = new();
    }

    public class MappingView
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("background")]
        public string Background { get; set; } = "";

        [JsonPropertyName("components")]
        public List<MappingComponent> Components { get; set; } = new();
    }

    public class MappingComponent
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "engine";

        [JsonPropertyName("color")]
        public string Color { get; set; } = "#78909C";

        // outline: [[x,y],...] или "circle(cx,cy,r)"
        [JsonPropertyName("outline")]
        public JsonElement Outline { get; set; }

        [JsonPropertyName("error_codes")]
        public List<string> ErrorCodes { get; set; } = new();
    }

    /// <summary>
    /// База 2D-схем двигателей. Загружает данные из mapping_*.json.
    /// </summary>
    public static class DiagramDatabase
    {
        private static readonly Dictionary<string, EngineDiagram> _cache = new();
        private static bool _initialized;
        private static readonly object _loadLock = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        /// <summary>
        /// Получить схему для марки авто.
        /// </summary>
        public static EngineDiagram? GetDiagram(string brand, string? model = null)
        {
            EnsureLoaded();

            var key = NormalizeBrand(brand);
            if (_cache.TryGetValue(key, out var diagram))
                return diagram;

            // Fallback: generic
            return _cache.GetValueOrDefault("*");
        }

        /// <summary>
        /// Получить конкретный вид схемы.
        /// </summary>
        public static DiagramView? GetView(EngineDiagram diagram, string viewId)
            => diagram.Views.FirstOrDefault(v => v.ViewId == viewId)
            ?? diagram.Views.FirstOrDefault();

        /// <summary>
        /// Перезагрузить все mapping-файлы из директории Data.
        /// </summary>
        public static void Reload()
        {
            _cache.Clear();
            _initialized = false;
            EnsureLoaded();
        }

        /// <summary>
        /// Возвращает все диаграммы (для миграции в SQLite).
        /// </summary>
        public static Dictionary<string, EngineDiagram> GetAllDiagrams()
        {
            EnsureLoaded();
            return new Dictionary<string, EngineDiagram>(_cache);
        }

        // ═══════════════════════════════════════════════════
        //  Загрузка
        // ═══════════════════════════════════════════════════

        /// <summary>
        /// Инициализировать загрузку диаграмм на фоновом потоке.
        /// Вызывается при старте приложения. Без этого EnsureLoaded вернёт fallback.
        /// </summary>
        public static async Task InitializeAsync()
        {
            if (_initialized) return;
            await Task.Run(LoadFromDisk);
        }

        private static void LoadFromDisk()
        {
            lock (_loadLock)
            {
                if (_initialized && _cache.Count > 1) return;

                try
                {
                    foreach (var dataDir in GetCandidateDataDirs())
                    {
                        if (!Directory.Exists(dataDir)) continue;

                        var files = Directory.GetFiles(dataDir, "mapping_*.json");
                        if (files.Length == 0) continue;

                        foreach (var file in files)
                            TryLoadMappingFile(file);

                        if (_cache.Count > 0)
                            break;
                    }

                    // MauiAsset только если файлы на диске не найдены.
                    // OpenAppPackageFileAsync с фонового потока на WinUI → 0xc000027b (STOWED_EXCEPTION).
                    if (_cache.Count == 0)
                        TryLoadFromMauiAssets();

                    if (_cache.Count == 0)
                        LoadFallback();
                }
                catch
                {
                    if (_cache.Count == 0)
                        LoadFallback();
                }
                finally
                {
                    _initialized = true;
                }
            }
        }

        private static IEnumerable<string> GetCandidateDataDirs()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var appCtx = AppContext.BaseDirectory;

            yield return Path.Combine(baseDir, "Data");
            yield return Path.Combine(appCtx, "Data");
            yield return Path.Combine(baseDir, "mappings");
            yield return Path.Combine(appCtx, "mappings");
            // publish/zip: mapping_*.json иногда лежат рядом с exe
            yield return baseDir;
            yield return appCtx;

            // Dev fallback: исходники проекта
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "source", "repos", "CarDiagnosticApp", "Data");
        }

        private static void TryLoadMappingFile(string file)
        {
            try
            {
                var json = File.ReadAllText(file, Encoding.UTF8);
                LoadMappingJson(json);
            }
            catch { /* skip malformed files */ }
        }

        private static void TryLoadFromMauiAssets()
        {
            try
            {
                // Не вызывать FileSystem с фонового потока WinUI — краш 0xc000027b.
                // Если MainThread недоступен / мы не на UI — пропускаем Maui assets (есть disk + fallback).
                try
                {
                    if (!MainThread.IsMainThread)
                        return;
                }
                catch
                {
                    return;
                }

                // Синхронный путь для EnsureLoaded; asset names without Resources/Raw prefix
                string[] assetNames =
                {
                    "mappings/mapping_vaz.json",
                    "mappings/mapping_kamaz.json",
                    "mappings/mapping_gaz.json",
                    "mappings/mapping_uaz.json",
                    "mappings/mapping_generic.json",
                    "mapping_vaz.json",
                    "mapping_kamaz.json",
                    "mapping_gaz.json",
                    "mapping_uaz.json",
                    "mapping_generic.json",
                };

                foreach (var name in assetNames)
                {
                    try
                    {
                        using var stream = FileSystem.OpenAppPackageFileAsync(name)
                            .ConfigureAwait(false).GetAwaiter().GetResult();
                        if (stream == null) continue;
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        LoadMappingJson(reader.ReadToEnd());
                    }
                    catch { /* asset missing */ }
                }
            }
            catch { /* FileSystem unavailable (unit tests) */ }
        }

        private static void LoadMappingJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;
            var mapping = JsonSerializer.Deserialize<MappingJson>(json, JsonOpts);
            if (mapping == null) return;

            var diagram = ConvertToDiagram(mapping);
            var key = NormalizeBrand(mapping.IsFallback || mapping.Brand == "*" ? "*" : mapping.Brand);
            _cache[key] = diagram;
        }

        private static void EnsureLoaded()
        {
            if (_initialized) return;
            LoadFromDisk();
        }

        private static void LoadFallback()
        {
            // Жёстко закодированный fallback (inline4 generic)
            var diagram = new EngineDiagram
            {
                EngineName = "Рядный 4-цил.",
                EngineType = "inline4",
                CarBrand = "",
                Views = new()
                {
                    new()
                    {
                        ViewName = "Вид сверху", ViewId = "top",
                        BackgroundLabel = "Моторный отсек",
                        Components = new()
                        {
                            Comp("engine_block","Блок цилиндров","engine","#78909C",R(.25f,.35f,.50f,.30f),P("P0300","P0301","P0302","P0303","P0304")),
                            Comp("cylinder_head","ГБЦ","engine","#90A4AE",R(.25f,.25f,.50f,.10f),P("P0340")),
                            Comp("throttle","Дроссель","intake","#81C784",R(.04f,.12f,.08f,.10f),P("P0120","P0121","P0122")),
                            Comp("maf","ДМРВ","sensor","#A5D6A7",R(.02f,.05f,.08f,.09f),P("P0100","P0101","P0102")),
                            Comp("fuel_rail","Рампа","fuel","#FFCDD2",R(.26f,.20f,.48f,.05f),P("P0170","P0171","P0172")),
                            Comp("coils","Катушки","ignition","#FFF9C4",R(.26f,.16f,.48f,.04f),P("P0351","P0352","P0353","P0354")),
                            Comp("radiator","Радиатор","cooling","#B3E5FC",R(.04f,.40f,.06f,.22f),P("P0115","P0116")),
                            Comp("alternator","Генератор","electrical","#FFE082",R(.84f,.06f,.06f,.06f),P("P0560","P0562")),
                            Comp("ecu","ЭБУ","electrical","#CE93D8",R(.08f,.62f,.08f,.08f),P("P0600","P0601")),
                            Comp("crank","ДПКВ","sensor","#E1BEE7",C(.28f,.46f,.03f),P("P0320","P0335")),
                            Comp("cam","ДПРВ","sensor","#CE93D8",C(.72f,.20f,.03f),P("P0340")),
                            Comp("o2_up","ДК1","sensor","#AED581",C(.68f,.22f,.025f),P("P0130","P0131","P0132")),
                            Comp("o2_down","ДК2","sensor","#C5E1A5",C(.74f,.48f,.025f),P("P0136","P0140","P0420")),
                            Comp("cat","Катализатор","exhaust","#BCAAA4",R(.78f,.45f,.06f,.10f),P("P0420")),
                            Comp("battery","АКБ","electrical","#FFF59D",R(.90f,.60f,.06f,.08f),P("P0560","P0562","P0563")),
                        }
                    }
                }
            };
            _cache["*"] = diagram;
        }

        // ═══════════════════════════════════════════════════
        //  Конвертация JSON → EngineDiagram
        // ═══════════════════════════════════════════════════

        private static EngineDiagram ConvertToDiagram(MappingJson m)
        {
            var brand = NormalizeBrand(m.IsFallback || m.Brand == "*" ? "*" : m.Brand);
            var diagram = new EngineDiagram
            {
                EngineName = string.IsNullOrWhiteSpace(m.EngineName) ? brand : m.EngineName,
                EngineType = m.EngineType,
                CarBrand = brand == "*" ? "" : brand,
                Description = m.DifferenceNotes ?? "",
                Views = new(),
                Checklist = new(),
            };

            foreach (var mv in m.Views)
            {
                var view = new DiagramView
                {
                    ViewName = mv.Name,
                    ViewId = mv.Id,
                    BackgroundLabel = mv.Background,
                    Components = new()
                };

                foreach (var mc in mv.Components)
                {
                    var codes = mc.ErrorCodes ?? new List<string>();
                    var comp = new DiagramComponent
                    {
                        Id = mc.Id,
                        Name = mc.Name,
                        Category = mc.Category,
                        DefaultColor = mc.Color,
                        ErrorCodes = codes.Select(c => (c ?? "").Trim().ToUpperInvariant())
                            .Where(c => c.Length > 0)
                            .Distinct()
                            .ToList(),
                        Outline = ParseOutline(mc.Outline)
                    };
                    view.Components.Add(comp);
                }

                diagram.Views.Add(view);
            }

            // Чеклист-рекомендации по узлам (для UI схем)
            if (!string.IsNullOrWhiteSpace(m.DifferenceNotes))
                diagram.Checklist.Add(m.DifferenceNotes);

            foreach (var view in diagram.Views)
            {
                foreach (var comp in view.Components.Where(c => c.ErrorCodes.Count > 0).Take(8))
                {
                    diagram.Checklist.Add(
                        $"• {comp.Name}: коды {string.Join(", ", comp.ErrorCodes.Take(4))}");
                }
            }

            return diagram;
        }

        /// <summary>
        /// Парсит outline: [[x,y],...] или "circle(cx,cy,r)".
        /// </summary>
        private static List<PointF> ParseOutline(JsonElement el)
        {
            // Строковый формат: "circle(cx,cy,r)"
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString() ?? "";
                if (s.StartsWith("circle(") && s.EndsWith(")"))
                {
                    var inner = s[7..^1];
                    var parts = inner.Split(',');
                    if (parts.Length == 3 &&
                        float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var cx) &&
                        float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var cy) &&
                        float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var r))
                    {
                        return C(cx, cy, r);
                    }
                }
                return new();
            }

            // Массив: [[x,y],[x,y],...]
            if (el.ValueKind == JsonValueKind.Array)
            {
                var pts = new List<PointF>();
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Array)
                    {
                        var coords = new List<float>();
                        foreach (var c in item.EnumerateArray())
                        {
                            if (c.ValueKind == JsonValueKind.Number && c.TryGetSingle(out var v))
                                coords.Add(v);
                            else if (c.ValueKind == JsonValueKind.String &&
                                     float.TryParse(c.GetString(), System.Globalization.NumberStyles.Float,
                                         System.Globalization.CultureInfo.InvariantCulture, out var vs))
                                coords.Add(vs);
                        }
                        if (coords.Count >= 2)
                            pts.Add(new PointF(coords[0], coords[1]));
                    }
                }
                return pts;
            }

            return new();
        }

        // ═══════════════════════════════════════════════════
        //  Helpers (для fallback)
        // ═══════════════════════════════════════════════════

        static List<PointF> R(float x, float y, float w, float h) => new() { new(x,y), new(x+w,y), new(x+w,y+h), new(x,y+h) };
        static List<PointF> C(float cx, float cy, float r, int segs = 12)
        {
            var pts = new List<PointF>();
            for (int i = 0; i < segs; i++) { var a = 2*Math.PI*i/segs; pts.Add(new((float)(cx+r*Math.Cos(a)), (float)(cy+r*Math.Sin(a)))); }
            return pts;
        }
        static DiagramComponent Comp(string id, string name, string cat, string color, List<PointF> outline, List<string> codes)
            => new() { Id=id, Name=name, Category=cat, DefaultColor=color, Outline=outline, ErrorCodes=codes };
        static List<string> P(params string[] codes) => codes.ToList();

        /// <summary>Нормализация марки для ключей кэша и сравнения.</summary>
        public static string NormalizeBrand(string brand)
        {
            var raw = (brand ?? "").Trim();
            if (raw.Length == 0) return "*";

            // Латиница без учёта регистра
            var upper = raw.ToUpperInvariant();
            if (upper is "LADA" or "VAZ" or "AVTOVAZ" or "AUTOVAZ") return "ВАЗ";
            if (upper is "GAZ" or "GAZELLE" or "SOBOL") return "ГАЗ";
            if (upper is "UAZ") return "УАЗ";
            if (upper is "KAMAZ" or "KAMA3") return "КАМАЗ";

            // Кириллица (ToUpperInvariant для кириллицы ок)
            var cyr = raw.ToUpper();
            if (cyr is "ВАЗ" or "ЛАДА" or "АВТОВАЗ") return "ВАЗ";
            if (cyr is "ГАЗ" or "ГАЗЕЛЬ") return "ГАЗ";
            if (cyr is "УАЗ") return "УАЗ";
            if (cyr is "КАМАЗ" or "КАМAЗ") return "КАМАЗ";

            // Частичное совпадение
            if (cyr.Contains("ВАЗ") || cyr.Contains("ЛАДА") || upper.Contains("LADA")) return "ВАЗ";
            if (cyr.Contains("КАМАЗ") || upper.Contains("KAMAZ")) return "КАМАЗ";
            if (cyr.Contains("ГАЗ") || upper.Contains("GAZ")) return "ГАЗ";
            if (cyr.Contains("УАЗ") || upper.Contains("UAZ")) return "УАЗ";

            return cyr;
        }

        /// <summary>
        /// Совпадают ли две марки (LADA ≡ ВАЗ, KAMAZ ≡ КАМАЗ).
        /// Пустая марка / "*" НЕ считается совпадением со всем — иначе смешиваются бренды.
        /// </summary>
        public static bool BrandsMatch(string? a, string? b)
        {
            var na = NormalizeBrand(a ?? "");
            var nb = NormalizeBrand(b ?? "");
            // Универсальный / пустой ключ — не матчим с конкретной маркой
            if (na is "*" or "" || nb is "*" or "")
                return false;
            return string.Equals(na, nb, StringComparison.Ordinal);
        }

        /// <summary>Алиасы одной марки (для поиска в кеше LADA/ВАЗ).</summary>
        public static IEnumerable<string> BrandAliases(string? brand)
        {
            var n = NormalizeBrand(brand ?? "");
            if (n is "*" or "")
                yield break;

            yield return n;
            switch (n)
            {
                case "ВАЗ":
                    yield return "LADA";
                    yield return "Lada";
                    yield return "ВАЗ";
                    yield return "АвтоВАЗ";
                    break;
                case "КАМАЗ":
                    yield return "KAMAZ";
                    yield return "КамАЗ";
                    yield return "КАМАЗ";
                    break;
                case "ГАЗ":
                    yield return "GAZ";
                    yield return "ГАЗ";
                    break;
                case "УАЗ":
                    yield return "UAZ";
                    yield return "УАЗ";
                    break;
                default:
                    yield return brand!.Trim();
                    break;
            }
        }
    }
}
