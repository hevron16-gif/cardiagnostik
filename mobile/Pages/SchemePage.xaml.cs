using CarDiagnosticApp.Data;
using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using CarDiagnosticApp.Views;
using Microsoft.Maui.Layouts;
using PointF = Microsoft.Maui.Graphics.PointF;

namespace CarDiagnosticApp.Pages;

public partial class SchemePage : ContentPage
{
    private readonly string _errorCode;
    private readonly string _carBrand;
    private readonly string _carModel;
    private readonly string? _aiAnalysisText;
    private readonly ApiService? _api;

    private EngineDiagram? _diagram;
    private DiagramView? _currentView;
    private readonly DiagramDrawable _drawable = new();

    private string? _imageDiagramPath;
    private float _startScale, _lastScale;
    private float _startPanX, _startPanY;
    private IDispatcherTimer? _pulseTimer;
    private bool _isSearching;
    private bool _loadStarted;

    /// <summary>Данные схемы из библиотеки сервера (/schemas/{code}).</summary>
    private Newtonsoft.Json.Linq.JObject? _serverSchemaData;
    private string? _serverSchemaTitle;
    private string? _serverSchemaDescription;
    private List<string> _serverCheckpoints = new();
    private List<(int Id, string Label, float X, float Y, List<int> Links)> _serverNodes = new();

    public static string? PendingAiAnalysis { get; set; }

    public SchemePage(string errorCode, string carBrand, string carModel)
    {
        InitializeComponent();

        _errorCode = errorCode ?? "";
        _carBrand = carBrand ?? "";
        _carModel = carModel ?? "";
        _aiAnalysisText = PendingAiAnalysis;
        PendingAiAnalysis = null;

        try
        {
            _api = IPlatformApplication.Current?.Services?.GetService<ApiService>();
        }
        catch { _api = null; }

        try
        {
            LabelErrorCode.Text = _errorCode;
            LabelCarInfo.Text = $"{_carBrand} {_carModel}".Trim();
            // GraphicsView удалён: на Windows WinUI 0xc000027b при Draw/Invalidate
            ComponentListScroll.IsVisible = false;
            if (!string.IsNullOrWhiteSpace(_aiAnalysisText))
                BtnAiHighlight.IsVisible = true;
        }
        catch { }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loadStarted) return;
        _loadStarted = true;

        try
        {
            // Дать странице отрисоваться — иначе GraphicsView/WinUI падает при первом paint
            await Task.Delay(100);
            await LoadDiagramAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SchemePage] Load: {ex}");
            WriteSchemeLog($"Load fail: {ex.GetType().Name}: {ex.Message}");
            try
            {
                LabelPageTitle.Text = "Ошибка загрузки схемы";
                DiagramPlaceholder.IsVisible = true;
                PlaceholderTitle.Text = "Не удалось открыть схему";
                PlaceholderSubtitle.Text = "Попробуйте кнопку «Поиск схем» или вернитесь назад";
                ComponentListScroll.IsVisible = false;
            }
            catch { }
        }
        // Автопоиск в сети ОТКЛЮЧЁН — только по кнопке (не вешает UI)
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPulse();
    }

    private static void WriteSchemeLog(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CarDiagnosticApp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Scheme] {msg}\n");
        }
        catch { }
    }

    // ═══════════════════════════════════════════════════════
    //  Пульсация неисправного компонента
    // ═══════════════════════════════════════════════════════

    private void StartPulse()
    {
        // Пульсация GraphicsView отключена полностью (список узлов вместо canvas).
        return;
    }

    private void StopPulse()
    {
        try
        {
            _pulseTimer?.Stop();
        }
        catch { }
        _pulseTimer = null;
        _drawable.PulseOffset = 0;
    }

    // ═══════════════════════════════════════════════════════
    //  Загрузка диаграммы
    // ═══════════════════════════════════════════════════════

    private async Task LoadDiagramAsync()
    {
        WriteSchemeLog($"Load start code={_errorCode} brand={_carBrand} model={_carModel}");

        // 1) Локальный mapping JSON — главный путь (без SQLite/сети).
        try { DiagramDatabase.GetDiagram(_carBrand, _carModel); } catch { }

        var localFirst = DiagramDatabase.GetDiagram(_carBrand, _carModel);
        if (localFirst != null && localFirst.Views.Count > 0 &&
            (string.IsNullOrWhiteSpace(localFirst.CarBrand)
             || string.IsNullOrWhiteSpace(_carBrand)
             || DiagramDatabase.BrandsMatch(localFirst.CarBrand, _carBrand)
             || localFirst.CarBrand is "*" or ""))
        {
            if (!string.IsNullOrWhiteSpace(_carBrand) &&
                (localFirst.CarBrand is "*" or "") &&
                DiagramDatabase.GetDiagram(_carBrand) is { Views.Count: > 0 } branded)
            {
                _diagram = branded;
            }
            else
            {
                _diagram = localFirst;
            }
        }

        // 2) Локальная библиотека PNG (Data/schemes/P0xxx.png) — приоритет, в окне приложения
        try
        {
            var localImg = FindLocalLibraryImage(_errorCode);
            if (localImg != null)
            {
                _imageDiagramPath = localImg;
                WriteSchemeLog($"local library image: {localImg}");
            }
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"local library: {ex.Message}");
        }

        // 3) Библиотека сервера — JSON-узлы, чеклист (дополнение)
        try
        {
            await FetchServerLibrarySchemaAsync(_errorCode);
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"server library: {ex.Message}");
        }

        // 4) SQLite — только если локальный mapping пуст
        if (_diagram == null || _diagram.Views.Count == 0)
        {
            try
            {
                var diagramDb = new DiagramDbService();
                var byBrandCode = await diagramDb.GetDiagramAsync(_carBrand, _carModel, _errorCode);
                if (byBrandCode != null && byBrandCode.Views.Count > 0)
                    _diagram = byBrandCode;
            }
            catch (Exception ex)
            {
                WriteSchemeLog($"sqlite: {ex.Message}");
            }
        }

        if (_diagram == null || _diagram.Views.Count == 0)
            _diagram = DiagramDatabase.GetDiagram("");

        // Если локально пусто, но сервер отдал nodes — строим EngineDiagram из библиотеки
        if ((_diagram == null || _diagram.Views.Count == 0) && _serverNodes.Count > 0)
        {
            try
            {
                var serverDiagram = await LoadServerSchemaAsync(_errorCode, _carBrand, _carModel);
                if (serverDiagram != null && serverDiagram.Views.Count > 0)
                    _diagram = serverDiagram;
            }
            catch { }
        }

        if ((_diagram == null || _diagram.Views.Count == 0) && _serverNodes.Count == 0)
        {
            LabelPageTitle.Text = "Схема не найдена";
            ComponentListScroll.IsVisible = false;
            ImageScrollView.IsVisible = false;
            DiagramPlaceholder.IsVisible = true;
            SearchResultsScroll.IsVisible = false;
            PlaceholderTitle.Text = "Схема не найдена";
            PlaceholderSubtitle.Text = "Нет локальной схемы и нет записи в библиотеке сервера. Нажмите «Библиотека».";
            WriteSchemeLog("no diagram found");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_diagram.CarBrand) &&
            !string.IsNullOrWhiteSpace(_carBrand) &&
            !DiagramDatabase.BrandsMatch(_diagram.CarBrand, _carBrand) &&
            _diagram.CarBrand is not ("*" or ""))
        {
            var local = DiagramDatabase.GetDiagram(_carBrand, _carModel);
            if (local != null)
                _diagram = local;
        }

        var titleBase = !string.IsNullOrWhiteSpace(_serverSchemaTitle)
            ? _serverSchemaTitle!
            : (_diagram != null ? $"Узлы: {_diagram.EngineName}" : _errorCode);
        LabelPageTitle.Text = titleBase;
        LabelCarInfo.Text = $"{_carBrand} {_carModel}".Trim();
        if (_diagram != null &&
            !string.IsNullOrWhiteSpace(_diagram.CarBrand) &&
            _diagram.CarBrand is not ("*" or "") &&
            !string.IsNullOrWhiteSpace(_diagram.EngineName))
        {
            LabelCarInfo.Text = $"{_diagram.CarBrand} · {_diagram.EngineName}";
        }

        DiagramPlaceholder.IsVisible = false;
        SearchResultsScroll.IsVisible = false;
        ImageScrollView.IsVisible = false;
        BtnSearchSchemes.IsEnabled = true;

        try { if (_diagram != null) BuildViewTabs(); } catch (Exception ex) { WriteSchemeLog($"tabs: {ex.Message}"); }

        var view = _diagram != null
            ? (DiagramDatabase.GetView(_diagram, "top") ?? _diagram.Views.FirstOrDefault())
            : null;
        if (view == null && _serverNodes.Count == 0)
        {
            DiagramPlaceholder.IsVisible = true;
            WriteSchemeLog("no views");
            return;
        }

        _currentView = view;
        if (view != null)
            _drawable.View = view;
        _drawable.Scale = 1f;
        _drawable.OffsetX = 0;
        _drawable.OffsetY = 0;

        var code = (_errorCode ?? "").Trim().ToUpperInvariant();
        var highlights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_diagram != null)
        {
            foreach (var v in _diagram.Views)
            {
                foreach (var comp in v.Components)
                {
                    if (comp.ErrorCodes != null &&
                        comp.ErrorCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
                    {
                        highlights[comp.Id] = 3;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_aiAnalysisText))
        {
            try { ParseAiTextAndHighlight(highlights); } catch { }
        }

        if (view != null)
        {
            foreach (var comp in view.Components)
                comp.HighlightLevel = highlights.GetValueOrDefault(comp.Id, 0);
        }
        _drawable.HighlightLevels = highlights;

        try { if (view != null) ShowSchemeRecommendations(highlights); } catch { }
        try { if (view != null) UpdateInfoBar(highlights); } catch { }

        await ApplyLibraryImageAsync(_errorCode);
        ShowComponentListUI(view, highlights);
        StopPulse();
    }

    /// <summary>Ставит PNG location-схему в панель сверху (обязательно видно).</summary>
    private async Task ApplyLibraryImageAsync(string errorCode)
    {
        try
        {
            var bytes = await LoadLibraryImageBytesAsync(errorCode);
            if (bytes == null || bytes.Length < 1000)
            {
                LibraryImagePanel.IsVisible = false;
                WriteSchemeLog($"ApplyLibraryImage: no bytes for {errorCode}");
                return;
            }

            // FromStream надёжнее FromFile на Windows/WinUI (белый Image при FromFile)
            var tmpDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CarDiagnosticApp", "schemes_cache");
            Directory.CreateDirectory(tmpDir);
            var tmp = Path.Combine(tmpDir, $"{errorCode}_{bytes.Length}_{DateTime.Now:HHmmssfff}.png");
            await File.WriteAllBytesAsync(tmp, bytes);

            var copy = bytes;
            LibrarySchemeImage.Source = null;
            await Task.Delay(16); // дать UI сбросить предыдущий Source
            LibrarySchemeImage.Source = ImageSource.FromStream(() => new MemoryStream(copy));
            LibraryImageCaption.Text = $"LOCATION · {errorCode}.png · {bytes.Length / 1024} КБ · {DateTime.Now:HH:mm:ss}";
            LibraryImagePanel.IsVisible = true;
            LibraryImagePanel.HeightRequest = 360;
            LibrarySchemeImage.HeightRequest = 340;
            LibrarySchemeImage.MinimumHeightRequest = 280;
            _imageDiagramPath = tmp;
            WriteSchemeLog($"ApplyLibraryImage OK stream bytes={bytes.Length} tmp={tmp}");
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"ApplyLibraryImage fail: {ex.Message}");
            try { LibraryImagePanel.IsVisible = false; } catch { }
        }
    }

    /// <summary>Ищет PNG в локальной библиотеке Data/schemes/{code}.png рядом с exe.</summary>
    private static string? FindLocalLibraryImage(string errorCode)
    {
        var code = (errorCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) return null;

        var names = new[]
        {
            $"{code}.png",
            $"{code}_location.png",
            $"{code}_1.png",
        };

        var bases = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "schemes"),
            Path.Combine(AppContext.BaseDirectory, "schemes"),
            Path.Combine(AppContext.BaseDirectory, "Data"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CarDiagnosticApp", "schemes"),
        };

        foreach (var dir in bases)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var n in names)
            {
                var p = Path.Combine(dir, n);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>Загрузка PNG из MauiAsset packages/schemes или с диска. Всегда в байты (без кэша Image).</summary>
    private async Task<byte[]?> LoadLibraryImageBytesAsync(string errorCode)
    {
        var code = (errorCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) return null;

        // 1) MauiAsset
        foreach (var name in new[] { $"schemes/{code}.png", $"schemes/{code}_location.png", $"{code}.png" })
        {
            try
            {
                await using var s = await FileSystem.OpenAppPackageFileAsync(name);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms);
                if (ms.Length > 1000)
                {
                    WriteSchemeLog($"asset image {name} bytes={ms.Length}");
                    return ms.ToArray();
                }
            }
            catch { /* asset missing */ }
        }

        // 2) Файл рядом с exe
        var path = FindLocalLibraryImage(code);
        if (path != null && File.Exists(path))
        {
            var b = await File.ReadAllBytesAsync(path);
            WriteSchemeLog($"disk image {path} bytes={b.Length}");
            return b;
        }
        return null;
    }

    /// <summary>
    /// Схема в окне приложения: PNG-библиотека + сервер (узлы) + локальная карта марки.
    /// Без GraphicsView / браузера / интернет-поиска картинок.
    /// </summary>
    private void ShowComponentListUI(DiagramView? view, Dictionary<string, int> highlights)
    {
        try
        {
            ComponentList.Children.Clear();
            var hl = highlights ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // ═══ Локальная PNG-библиотека (location diagram) ═══
            var libPath = _imageDiagramPath;
            if (string.IsNullOrEmpty(libPath) || !File.Exists(libPath))
                libPath = FindLocalLibraryImage(_errorCode);

            // PNG location — главная «картинка» (панель сверху). Блоковую AbsoluteLayout-схему НЕ рисуем.
            // (раньше синие блоки сервера выглядели как «старая схема из блоков»)
            // Image заполняется в ApplyLibraryImageAsync перед вызовом ShowComponentListUI.

            if (_serverNodes.Count > 0 || !string.IsNullOrWhiteSpace(_serverSchemaTitle))
            {
                ComponentList.Children.Add(MakeSectionHeader("📚 Библиотека сервера (текст)",
                    string.IsNullOrWhiteSpace(_serverSchemaTitle) ? _errorCode : _serverSchemaTitle!));

                if (!string.IsNullOrWhiteSpace(_serverSchemaDescription))
                {
                    ComponentList.Children.Add(new Border
                    {
                        BackgroundColor = Color.FromArgb("#1A237E"),
                        Stroke = Color.FromArgb("#3949AB"),
                        StrokeThickness = 1,
                        Padding = new Thickness(12, 10),
                        Margin = new Thickness(0, 0, 0, 8),
                        Content = new Label
                        {
                            Text = _serverSchemaDescription,
                            FontSize = 13,
                            TextColor = Color.FromArgb("#E8EAF6"),
                            LineBreakMode = LineBreakMode.WordWrap,
                        }
                    });
                }

                if (_serverNodes.Count > 0)
                {
                    ComponentList.Children.Add(new Label
                    {
                        Text = "Узлы (текст, без блоковой схемы):",
                        FontFamily = "InterSemiBold",
                        FontSize = 13,
                        TextColor = Color.FromArgb("#90CAF9"),
                        Margin = new Thickness(0, 4, 0, 4),
                    });
                    foreach (var n in _serverNodes)
                    {
                        var linkTxt = n.Links.Count > 0
                            ? " → " + string.Join(", ", n.Links.Select(id =>
                                _serverNodes.FirstOrDefault(x => x.Id == id).Label ?? $"#{id}"))
                            : "";
                        ComponentList.Children.Add(new Label
                        {
                            Text = $"• {n.Label}{linkTxt}",
                            FontSize = 13,
                            TextColor = Colors.White,
                            Margin = new Thickness(4, 2, 0, 2),
                        });
                    }
                }

                if (_serverCheckpoints.Count > 0)
                {
                    ComponentList.Children.Add(new Label
                    {
                        Text = "Чек-лист:",
                        FontFamily = "InterSemiBold",
                        FontSize = 13,
                        TextColor = Color.FromArgb("#81C784"),
                        Margin = new Thickness(0, 10, 0, 4),
                    });
                    int i = 1;
                    foreach (var cp in _serverCheckpoints)
                    {
                        ComponentList.Children.Add(new Label
                        {
                            Text = $"{i}. {cp}",
                            FontSize = 12,
                            TextColor = Color.FromArgb("#C8E6C9"),
                            Margin = new Thickness(8, 2, 0, 2),
                        });
                        i++;
                    }
                }
            }

            // ═══ Локальная карта марки (подсветка по DTC) ═══
            if (view != null && view.Components.Count > 0)
            {
                var faultCount = hl.Count(kv => kv.Value >= 3);
                ComponentList.Children.Add(MakeSectionHeader(
                    "🔧 Карта узлов двигателя (локально)",
                    $"{view.ViewName} · {view.Components.Count} узлов" +
                    (faultCount > 0 ? $" · 🔴 {faultCount} по {_errorCode}" : "")));

                ComponentList.Children.Add(new Label
                {
                    Text = "🔴 неисправность · 🟠 проверить · 🔵 связан · ⚪ норма",
                    FontSize = 11,
                    TextColor = Color.FromArgb("#757575"),
                    Margin = new Thickness(0, 0, 0, 8),
                });

                foreach (var comp in view.Components
                    .OrderByDescending(c => hl.GetValueOrDefault(c.Id, c.HighlightLevel))
                    .ThenBy(c => c.Name))
                {
                    int level = hl.GetValueOrDefault(comp.Id, comp.HighlightLevel);
                    string badge = level >= 3 ? "🔴" : level == 2 ? "🟠" : level == 1 ? "🔵" : "⚪";
                    string bg = level >= 3 ? "#3D1515" : level == 2 ? "#3D2A10" : level == 1 ? "#0D2137" : "#1E1E1E";
                    string border = level >= 3 ? "#FF1744" : level == 2 ? "#FF9100" : level == 1 ? "#1976D2" : "#333333";
                    var codes = comp.ErrorCodes is { Count: > 0 }
                        ? string.Join(", ", comp.ErrorCodes.Take(8)) : "—";

                    ComponentList.Children.Add(new Border
                    {
                        BackgroundColor = Color.FromArgb(bg),
                        Stroke = Color.FromArgb(border),
                        StrokeThickness = level > 0 ? 2 : 1,
                        Padding = new Thickness(12, 10),
                        Content = new VerticalStackLayout
                        {
                            Spacing = 2,
                            Children =
                            {
                                new Label { Text = $"{badge} {comp.Name}", FontFamily = "InterSemiBold", FontSize = 14, TextColor = Colors.White },
                                new Label { Text = $"Категория: {comp.Category}", FontSize = 11, TextColor = Color.FromArgb("#9E9E9E") },
                                new Label { Text = $"Коды: {codes}", FontSize = 11, TextColor = Color.FromArgb("#B0BEC5") },
                            }
                        }
                    });
                }
            }

            try { ImageScrollView.IsVisible = false; } catch { }
            try { SearchResultsScroll.IsVisible = false; } catch { }
            DiagramPlaceholder.IsVisible = false;
            ComponentListScroll.IsVisible = true;
            WriteSchemeLog($"UI OK serverNodes={_serverNodes.Count} local={view?.Components.Count ?? 0} imgPanel={LibraryImagePanel.IsVisible}");
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"list fail: {ex.Message}");
            try
            {
                ComponentListScroll.IsVisible = false;
                LibraryImagePanel.IsVisible = false;
                DiagramPlaceholder.IsVisible = true;
                PlaceholderTitle.Text = "Ошибка отображения схемы";
                PlaceholderSubtitle.Text = ex.Message;
            }
            catch { }
        }
    }

    private static View MakeSectionHeader(string title, string subtitle)
    {
        return new VerticalStackLayout
        {
            Spacing = 2,
            Margin = new Thickness(0, 4, 0, 8),
            Children =
            {
                new Label { Text = title, FontFamily = "InterSemiBold", FontSize = 15, TextColor = Color.FromArgb("#E0E0E0") },
                new Label { Text = subtitle, FontSize = 12, TextColor = Color.FromArgb("#90A4AE") },
            }
        };
    }

    /// <summary>Схема узлов из библиотеки: MAUI AbsoluteLayout (в окне приложения).</summary>
    private View BuildServerNodeDiagram()
    {
        const double canvasW = 360;
        const double canvasH = 200;
        double maxX = Math.Max(1, _serverNodes.Max(n => (double)n.X));
        double maxY = Math.Max(1, _serverNodes.Max(n => (double)n.Y));
        // нормализация в отступы
        double pad = 16;

        var abs = new AbsoluteLayout
        {
            BackgroundColor = Color.FromArgb("#0D1117"),
            HeightRequest = canvasH,
            MinimumHeightRequest = canvasH,
        };

        var frame = new Border
        {
            BackgroundColor = Color.FromArgb("#0D1117"),
            Stroke = Color.FromArgb("#37474F"),
            StrokeThickness = 1,
            Padding = 4,
            Content = abs,
            HeightRequest = canvasH + 8,
            Margin = new Thickness(0, 0, 0, 8),
        };

        // Фон
        AbsoluteLayout.SetLayoutBounds(abs, new Rect(0, 0, 1, 1));

        foreach (var n in _serverNodes)
        {
            double nx = pad + (n.X / (maxX + 80)) * (canvasW - 2 * pad - 100);
            double ny = pad + (n.Y / (maxY + 40)) * (canvasH - 2 * pad - 36);
            nx = Math.Clamp(nx, 4, canvasW - 110);
            ny = Math.Clamp(ny, 4, canvasH - 40);

            var node = new Border
            {
                BackgroundColor = Color.FromArgb("#1565C0"),
                Stroke = Color.FromArgb("#64B5F6"),
                StrokeThickness = 1,
                Padding = new Thickness(8, 6),
                Content = new Label
                {
                    Text = n.Label.Length > 18 ? n.Label[..16] + "…" : n.Label,
                    FontSize = 11,
                    TextColor = Colors.White,
                    FontFamily = "InterSemiBold",
                    LineBreakMode = LineBreakMode.TailTruncation,
                    MaxLines = 2,
                },
            };
            AbsoluteLayout.SetLayoutBounds(node, new Rect(nx, ny, 105, 36));
            AbsoluteLayout.SetLayoutFlags(node, AbsoluteLayoutFlags.None);
            abs.Children.Add(node);
        }

        // Ширина контейнера
        abs.WidthRequest = canvasW;
        return frame;
    }

    /// <summary>Загрузка схемы из библиотеки сервера GET /schemas/{code} — только in-app JSON.</summary>
    private async Task FetchServerLibrarySchemaAsync(string errorCode)
    {
        _serverSchemaData = null;
        _serverSchemaTitle = null;
        _serverSchemaDescription = null;
        _serverCheckpoints = new();
        _serverNodes = new();

        if (_api == null || string.IsNullOrWhiteSpace(errorCode))
        {
            WriteSchemeLog("server library: no api/code");
            return;
        }

        var json = await _api.GetSchemaJsonAsync(errorCode, "test");
        if (string.IsNullOrWhiteSpace(json))
        {
            WriteSchemeLog("server library: empty response");
            return;
        }

        var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
        if (jo.Value<bool?>("available") != true)
        {
            WriteSchemeLog($"server library unavailable: {jo.Value<string>("message")}");
            return;
        }

        var data = jo["data"] as Newtonsoft.Json.Linq.JObject;
        if (data == null) return;

        _serverSchemaData = data;
        _serverSchemaTitle = data.Value<string>("title") ?? errorCode;
        _serverSchemaDescription = data.Value<string>("description") ?? "";

        if (data["checkpoints"] is Newtonsoft.Json.Linq.JArray cps)
        {
            foreach (var cp in cps)
            {
                var t = cp.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? cp.ToString()
                    : (cp["text"]?.ToString() ?? cp.ToString());
                if (!string.IsNullOrWhiteSpace(t))
                    _serverCheckpoints.Add(t!);
            }
        }

        if (data["nodes"] is Newtonsoft.Json.Linq.JArray nodes)
        {
            foreach (var node in nodes)
            {
                var id = node.Value<int?>("id") ?? _serverNodes.Count + 1;
                var label = node.Value<string>("label") ?? $"Узел {id}";
                var x = node.Value<float?>("x") ?? 50;
                var y = node.Value<float?>("y") ?? 50;
                var links = new List<int>();
                if (node["links"] is Newtonsoft.Json.Linq.JArray la)
                {
                    foreach (var l in la)
                    {
                        if (l.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                            links.Add((int)l);
                        else if (int.TryParse(l.ToString(), out var lid))
                            links.Add(lid);
                    }
                }
                _serverNodes.Add((id, label, x, y, links));
            }
        }

        WriteSchemeLog($"server library OK title={_serverSchemaTitle} nodes={_serverNodes.Count} cp={_serverCheckpoints.Count}");
    }

    private static bool HasAnyErrorCodes(EngineDiagram diagram)
        => diagram.Views.Any(v => v.Components.Any(c => c.ErrorCodes.Count > 0));

    /// <summary>
    /// Загрузка схемы с сервера: JSON nodes → EngineDiagram, опционально SVG-картинка.
    /// </summary>
    private async Task<EngineDiagram?> LoadServerSchemaAsync(string errorCode, string brand, string model)
    {
        if (_api == null || string.IsNullOrWhiteSpace(errorCode)) return null;

        var json = await _api.GetSchemaJsonAsync(errorCode, "test");
        if (string.IsNullOrWhiteSpace(json)) return null;

        var jo = Newtonsoft.Json.Linq.JObject.Parse(json);
        if (jo.Value<bool?>("available") != true)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SchemePage] server unavailable: {jo.Value<string>("message")}");
            return null;
        }

        // Используем SchemeService.Build via reflection-free copy: create service if DI allows
        try
        {
            var license = IPlatformApplication.Current?.Services?.GetService<LicenseService>()
                          ?? new LicenseService(_api);
            var svc = new SchemeService(_api, new DiagramDbService(), license);
            // GetDiagramAsync also tries local first — call fetch path by using service after force
            var diagram = await svc.GetDiagramAsync(errorCode, brand, model);
            if (diagram != null && diagram.Views.Count > 0 && diagram.Id != "upgrade-stub")
                return diagram;
        }
        catch { }

        // Ручной разбор nodes, если service вернул stub/null
        var data = jo["data"];
        if (data == null) return null;
        var nodes = data["nodes"] as Newtonsoft.Json.Linq.JArray;
        if (nodes == null || nodes.Count == 0) return null;

        var components = new List<DiagramComponent>();
        foreach (var node in nodes)
        {
            var id = node.Value<int?>("id") ?? components.Count + 1;
            var label = node.Value<string>("label") ?? $"Узел {id}";
            var x = node.Value<float?>("x") ?? 100;
            var y = node.Value<float?>("y") ?? 100;
            float nx = Math.Clamp((x - 50f) / 700f, 0.02f, 0.98f);
            float ny = Math.Clamp((y - 20f) / 200f, 0.02f, 0.98f);
            float halfW = 0.08f, halfH = 0.05f;
            components.Add(new DiagramComponent
            {
                Id = $"node_{id}",
                Name = label,
                Category = "engine",
                DefaultColor = "#90CAF9",
                HighlightLevel = 0,
                ErrorCodes = new List<string> { errorCode.ToUpperInvariant() },
                Outline = new List<PointF>
                {
                    new(nx - halfW, ny - halfH),
                    new(nx + halfW, ny - halfH),
                    new(nx + halfW, ny + halfH),
                    new(nx - halfW, ny + halfH),
                },
            });
        }

        var checklist = new List<string>();
        if (data["checkpoints"] is Newtonsoft.Json.Linq.JArray cps)
        {
            foreach (var cp in cps)
                checklist.Add(cp.Type == Newtonsoft.Json.Linq.JTokenType.String
                    ? (cp.ToString() ?? "")
                    : (cp["text"]?.ToString() ?? cp.ToString() ?? ""));
        }

        // Попробовать подтянуть SVG как картинку
        string? imagePath = null;
        try
        {
            var svgUrl = $"{_api.BaseUrl}/schemas/{Uri.EscapeDataString(errorCode)}/image?user_id=test";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var bytes = await http.GetByteArrayAsync(svgUrl);
            if (bytes.Length > 200)
            {
                var dir = Path.Combine(FileSystem.AppDataDirectory, "schemes");
                Directory.CreateDirectory(dir);
                imagePath = Path.Combine(dir, $"{errorCode}_server.svg");
                await File.WriteAllBytesAsync(imagePath, bytes);
            }
        }
        catch { }

        return new EngineDiagram
        {
            Id = $"server-{errorCode}",
            ErrorCode = errorCode,
            CarBrand = brand,
            CarModel = model,
            EngineName = data.Value<string>("title") ?? errorCode,
            Title = data.Value<string>("title") ?? $"Схема {errorCode}",
            Description = data.Value<string>("description") ?? "",
            ImagePath = imagePath,
            Views = new List<DiagramView>
            {
                new()
                {
                    ViewId = "server",
                    ViewName = "Серверная схема",
                    BackgroundLabel = data.Value<string>("title") ?? errorCode,
                    Components = components,
                }
            },
            Checklist = checklist,
            CreatedAt = DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Показывает рекомендации по ремонту на основе подсвеченных узлов схемы.
    /// </summary>
    private void ShowSchemeRecommendations(Dictionary<string, int> highlights)
    {
        if (_currentView == null && _diagram?.Views.Count > 0)
            _currentView = _diagram.Views[0];
        if (_currentView == null) return;

        var fault = _currentView.Components
            .Where(c => highlights.GetValueOrDefault(c.Id, 0) >= 3)
            .ToList();
        var related = _currentView.Components
            .Where(c => highlights.GetValueOrDefault(c.Id, 0) is 1 or 2)
            .ToList();

        if (fault.Count == 0 && related.Count == 0)
        {
            // Даже без подсветки — общие рекомендации по марке из Description/Checklist
            if (_diagram?.Checklist is { Count: > 0 })
            {
                InfoBar.IsVisible = true;
                LabelHighlightedComp.Text = "Рекомендации по схеме";
                LabelHighlightedCodes.Text = string.Join(" · ", _diagram.Checklist.Take(3));
            }
            return;
        }

        InfoBar.IsVisible = true;
        var names = fault.Select(c => c.Name).Concat(related.Select(c => c.Name)).Distinct().Take(4);
        LabelHighlightedComp.Text = string.Join(", ", names);

        var tips = new List<string>();
        foreach (var comp in fault.Take(3))
        {
            tips.Add($"Проверить: {comp.Name}");
            if (comp.ErrorCodes.Count > 0)
                tips.Add($"коды {string.Join("/", comp.ErrorCodes.Take(3))}");
        }

        // Бренд-специфичные подсказки
        var brandKey = DiagramDatabase.NormalizeBrand(_carBrand);
        if (brandKey == "ВАЗ")
            tips.Add("ВАЗ: проверьте разъёмы датчиков и массу ЭБУ (частая причина ложных кодов)");
        else if (brandKey == "КАМАЗ")
            tips.Add("КАМАЗ: проверьте ТНВД, топливный фильтр-отстойник и давление наддува");
        else if (brandKey == "ГАЗ")
            tips.Add("ГАЗ: типичны проблемы MAF/MAP и топливной рампы");
        else if (brandKey == "УАЗ")
            tips.Add("УАЗ: проверьте ДМРВ, ДПКВ и качество топлива");

        if (!string.IsNullOrWhiteSpace(_diagram?.Description))
            tips.Add(_diagram.Description);

        LabelHighlightedCodes.Text = string.Join(". ", tips.Take(4));
    }

    /// <summary>
    /// Парсит текст AI-анализа и находит упоминания компонентов.
    /// </summary>
    private void ParseAiTextAndHighlight(Dictionary<string, int> highlights)
    {
        if (_diagram == null) return;

        var text = _aiAnalysisText!.ToLowerInvariant();

        // Словарь ключевых слов → ID компонентов
        var componentKeywords = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["дпкв"] = new[] { "crankshaft_sensor", "crank" },
            ["датчик коленвала"] = new[] { "crankshaft_sensor", "crank" },
            ["дпрв"] = new[] { "camshaft_sensor", "cam" },
            ["датчик распредвала"] = new[] { "camshaft_sensor", "cam" },
            ["дтож"] = new[] { "coolant_temp_sensor", "cts" },
            ["датчик температуры"] = new[] { "coolant_temp_sensor", "cts" },
            ["дмрв"] = new[] { "air_filter_maf", "maf" },
            ["датчик массового расхода"] = new[] { "air_filter_maf", "maf" },
            ["дроссель"] = new[] { "throttle_body" },
            ["дпдз"] = new[] { "tps" },
            ["датчик положения дросселя"] = new[] { "tps" },
            ["катушка"] = new[] { "ignition_coils", "coil_pack" },
            ["зажигани"] = new[] { "ignition_coils", "coil_pack", "distributor" },
            ["свеч"] = new[] { "spark_plugs", "plugs" },
            ["форсунк"] = new[] { "fuel_rail", "injectors" },
            ["лямбда"] = new[] { "o2_upstream", "o2_downstream" },
            ["кислородн"] = new[] { "o2_upstream", "o2_downstream" },
            ["дк1"] = new[] { "o2_upstream" },
            ["дк2"] = new[] { "o2_downstream" },
            ["катализатор"] = new[] { "catalyst", "cat" },
            ["топливный насос"] = new[] { "fuel_pump", "pump_in_tank" },
            ["бензонасос"] = new[] { "fuel_pump", "pump_in_tank" },
            ["рдт"] = new[] { "fuel_pressure_reg", "fpr" },
            ["регулятор давления"] = new[] { "fuel_pressure_reg", "fpr" },
            ["радиатор"] = new[] { "radiator" },
            ["термостат"] = new[] { "thermostat" },
            ["помп"] = new[] { "water_pump", "pump" },
            ["генератор"] = new[] { "alternator" },
            ["стартер"] = new[] { "starter" },
            ["эбу"] = new[] { "ecu" },
            ["детонаци"] = new[] { "knock_sensor", "knock" },
            ["датчик скорости"] = new[] { "vss" },
            ["датчик давления масла"] = new[] { "oil_press", "oil_pump" },
            ["масляный насос"] = new[] { "oil_pump" },
            ["клапан egr"] = new[] { "egr_valve" },
            ["егр"] = new[] { "egr_valve" },
            ["адсорбер"] = new[] { "evap_canister" },
            ["evap"] = new[] { "evap_canister" },
            ["клапан продувки"] = new[] { "evap_canister", "purge_valve" },
            ["впускной коллектор"] = new[] { "intake_manifold" },
            ["выпускной коллектор"] = new[] { "exhaust_manifold" },
            ["рхх"] = new[] { "iac_valve" },
            ["холостого хода"] = new[] { "iac_valve" },
            ["турбин"] = new[] { "turbo" },
            ["тнвд"] = new[] { "injection_pump", "fuel_pump" },
            ["топливный фильтр"] = new[] { "fuel_filter" },
            ["акб"] = new[] { "battery" },
            ["аккумулятор"] = new[] { "battery" },
        };

        foreach (var (keyword, ids) in componentKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var id in ids)
                {
                    // Не перезаписываем level 3 (точное совпадение)
                    if (!highlights.ContainsKey(id))
                        highlights[id] = 2; // уровень 2: AI предлагает проверить
                }
            }
        }
    }

    /// <summary>
    /// Применяет словарь подсветки ко всем видам и вызывает перерисовку.
    /// </summary>
    private void ApplyHighlights(Dictionary<string, int> highlights)
    {
        if (_diagram == null) return;

        foreach (var view in _diagram.Views)
        {
            foreach (var comp in view.Components)
            {
                comp.HighlightLevel = highlights.GetValueOrDefault(comp.Id, 0);
            }
        }

        _drawable.HighlightLevels = highlights;
        UpdateInfoBar(highlights);
        if (_currentView != null)
            ShowComponentListUI(_currentView, highlights);
    }

    /// <summary>
    /// Обновляет инфо-бар с перечнем выделенных компонентов.
    /// </summary>
    private void UpdateInfoBar(Dictionary<string, int> highlights)
    {
        if (highlights.Count == 0 || _currentView == null)
        {
            InfoBar.IsVisible = false;
            return;
        }

        InfoBar.IsVisible = true;

        var faultComps = new List<string>();
        var warnComps = new List<string>();
        var relatedComps = new List<string>();

        foreach (var (id, level) in highlights)
        {
            var comp = _currentView.Components.FirstOrDefault(c => c.Id == id);
            if (comp == null) continue;

            switch (level)
            {
                case 3: faultComps.Add($"🔴 {comp.Name}"); break;
                case 2: warnComps.Add($"🟠 {comp.Name}"); break;
                case 1: relatedComps.Add($"🔵 {comp.Name}"); break;
            }
        }

        var parts = new List<string>();
        parts.AddRange(faultComps);
        parts.AddRange(warnComps.Take(2));

        LabelHighlightedComp.Text = string.Join("  ", parts.Take(3));
        if (parts.Count > 3)
            LabelHighlightedComp.Text += $" +{parts.Count - 3}";

        var descParts = new List<string>();
        if (faultComps.Count > 0) descParts.Add($"{faultComps.Count} неисправен");
        if (warnComps.Count > 0) descParts.Add($"{warnComps.Count} проверить");
        LabelHighlightedCodes.Text = string.Join(", ", descParts);
    }

    /// <summary>
    /// Публичный метод для внешней установки подсветки.
    /// </summary>
    public void SetHighlights(Dictionary<string, int> highlights)
    {
        ApplyHighlights(highlights);
        if (highlights.Values.Any(v => v >= 3))
            StartPulse();
    }

    // ═══════════════════════════════════════════════════════
    //  Табы переключения видов
    // ═══════════════════════════════════════════════════════

    private void BuildViewTabs()
    {
        ViewTabs.Children.Clear();
        foreach (var view in _diagram!.Views)
        {
            var tab = new Button
            {
                Text = GetViewIcon(view.ViewId) + " " + view.ViewName,
                FontSize = 11,
                FontFamily = "InterSemiBold",
                Padding = new Thickness(10, 6),
                CornerRadius = 16,
                BackgroundColor = Color.FromArgb(view.ViewId == "top" ? "#1565C0" : "#E0E0E0"),
                TextColor = Color.FromArgb(view.ViewId == "top" ? "#FFFFFF" : "#424242"),
                CommandParameter = view.ViewId
            };
            tab.Clicked += OnViewTabClicked;
            ViewTabs.Children.Add(tab);
        }
    }

    private static string GetViewIcon(string viewId) => viewId switch
    {
        "top" => "🏗️", "fuel" => "⛽", "ignition" => "⚡",
        "cooling" => "🌡️", "exhaust" => "💨", "evap" => "🫧",
        "sensors" => "📡", _ => "📐"
    };

    private void OnViewTabClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is string viewId)
        {
            SwitchView(viewId);
            foreach (var child in ViewTabs.Children)
            {
                if (child is Button tab)
                {
                    var active = (tab.CommandParameter as string) == viewId;
                    tab.BackgroundColor = Color.FromArgb(active ? "#1565C0" : "#E0E0E0");
                    tab.TextColor = Color.FromArgb(active ? "#FFFFFF" : "#424242");
                }
            }
        }
    }

    private void SwitchView(string viewId)
    {
        if (_diagram == null) return;

        var view = DiagramDatabase.GetView(_diagram, viewId);
        if (view == null) return;

        _currentView = view;
        _drawable.View = view;

        // Переносим HighlightLevels на компоненты текущего вида
        foreach (var comp in view.Components)
        {
            comp.HighlightLevel = _drawable.HighlightLevels.GetValueOrDefault(comp.Id, 0);
        }

        ResetZoom();
        ShowComponentListUI(view, _drawable.HighlightLevels);
    }

    // ═══════════════════════════════════════════════════════
    //  AI-подсветка (кнопка 🧠 AI)
    // ═══════════════════════════════════════════════════════

    private async void OnAiHighlightClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_aiAnalysisText))
        {
            await DisplayAlert("AI-анализ", "Нет данных AI-анализа. Запустите диагностику на сервере.", "OK");
            return;
        }

        // Повторно парсим и применяем
        var highlights = _drawable.HighlightLevels
            .Where(kv => kv.Value >= 3)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        ParseAiTextAndHighlight(highlights);

        foreach (var (id, level) in highlights)
        {
            if (_drawable.HighlightLevels.TryGetValue(id, out var existing) && existing >= 3)
                continue;
            _drawable.HighlightLevels[id] = level;
        }

        ApplyHighlights(_drawable.HighlightLevels);
    }

    // ═══════════════════════════════════════════════════════
    //  Жесты
    // ═══════════════════════════════════════════════════════

    private void OnPinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
                _startScale = _drawable.Scale;
                _lastScale = (float)e.Scale;
                break;
            case GestureStatus.Running:
                _drawable.Scale = Math.Clamp(_startScale * (float)e.Scale, 0.3f, 5f);
                /* canvas removed */;
                break;
        }
    }

    private void OnPanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _startPanX = _drawable.OffsetX;
                _startPanY = _drawable.OffsetY;
                break;
            case GestureStatus.Running:
                _drawable.OffsetX = _startPanX + (float)e.TotalX;
                _drawable.OffsetY = _startPanY + (float)e.TotalY;
                /* canvas removed */;
                break;
        }
    }

    private void OnDiagramTapped(object? sender, TappedEventArgs e)
    {
        if (_currentView == null) return;

        var pos = e.GetPosition(ComponentListScroll);
        if (pos == null) return;

        var w = ComponentListScroll.Width;
        var h = ComponentListScroll.Height;
        if (w <= 0 || h <= 0) return;

        float margin = 24;
        float dx = margin, dy = margin + 28;
        float dw = (float)w - 2 * margin;
        float dh = (float)h - 2 * margin - 28;

        float wf = (float)w, hf = (float)h;
        float tx = ((float)pos.Value.X - _drawable.OffsetX - wf * 0.5f) / _drawable.Scale + wf * 0.5f;
        float ty = ((float)pos.Value.Y - _drawable.OffsetY - hf * 0.5f) / _drawable.Scale + hf * 0.5f;

        float nx = (tx - dx) / dw;
        float ny = (ty - dy) / dh;

        DiagramComponent? hit = null;
        foreach (var comp in _currentView.Components)
        {
            if (comp.Outline.Count < 3) continue;
            if (PointInPolygon(nx, ny, comp.Outline)) { hit = comp; break; }
        }

        if (hit != null)
            ShowComponentInfo(hit);
        else
            InfoBar.IsVisible = false;
    }

    private void ShowComponentInfo(DiagramComponent comp)
    {
        InfoBar.IsVisible = true;

        string badge = comp.HighlightLevel switch
        {
            3 => "🔴 ",
            2 => "🟠 ",
            1 => "🔵 ",
            _ => ""
        };
        LabelHighlightedComp.Text = $"{badge}{comp.Name}";

        var codes = string.Join(", ", comp.ErrorCodes.Take(6));
        if (comp.ErrorCodes.Count > 6) codes += $" +{comp.ErrorCodes.Count - 6}";

        string severity = comp.HighlightLevel switch
        {
            3 => " · Неисправность",
            2 => " · Проверить",
            1 => " · Связан",
            _ => ""
        };
        LabelHighlightedCodes.Text = $"Коды: {codes}{severity}";
    }

    private static bool PointInPolygon(float px, float py, List<PointF> poly)
    {
        bool inside = false;
        int j = poly.Count - 1;
        for (int i = 0; i < poly.Count; j = i++)
        {
            if ((poly[i].Y > py) != (poly[j].Y > py) &&
                px < (poly[j].X - poly[i].X) * (py - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }

    // ═══════════════════════════════════════════════════════
    //  Кнопки зума
    // ═══════════════════════════════════════════════════════

    private void OnZoomInClicked(object? sender, EventArgs e)
    {
        _drawable.Scale = Math.Min(_drawable.Scale * 1.3f, 5f);
        /* canvas removed */;
    }

    private void OnZoomOutClicked(object? sender, EventArgs e)
    {
        _drawable.Scale = Math.Max(_drawable.Scale / 1.3f, 0.3f);
        /* canvas removed */;
    }

    private void OnResetZoomClicked(object? sender, EventArgs e)
    {
        ResetZoom();
        /* canvas removed */;
    }

    private void ResetZoom()
    {
        _drawable.Scale = 1;
        _drawable.OffsetX = 0;
        _drawable.OffsetY = 0;
    }

    // ═══════════════════════════════════════════════════════
    //  ЭТАП 3: Поиск схем в интернете
    // ═══════════════════════════════════════════════════════

    private async void OnSearchSchemesClicked(object? sender, EventArgs e)
    {
        // Кнопка «Библиотека» — перезагрузка схемы из /schemas на сервере (в окне приложения)
        if (_isSearching) return;
        _isSearching = true;
        BtnSearchSchemes.IsEnabled = false;
        try
        {
            LabelPageTitle.Text = "Загрузка из библиотеки…";
            await FetchServerLibrarySchemaAsync(_errorCode);

            if (_serverNodes.Count == 0 && string.IsNullOrWhiteSpace(_serverSchemaTitle))
            {
                await DisplayAlert("Библиотека",
                    $"На сервере нет JSON для {_errorCode}. Локальная LOCATION-схема (PNG) всё равно показывается сверху.",
                    "OK");
            }

            await ApplyLibraryImageAsync(_errorCode);
            var view = _currentView ?? _diagram?.Views.FirstOrDefault();
            ShowComponentListUI(view, _drawable.HighlightLevels);
            LabelPageTitle.Text = !string.IsNullOrWhiteSpace(_serverSchemaTitle)
                ? _serverSchemaTitle!
                : $"Схема: {_errorCode}";
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"library btn: {ex.Message}");
            await DisplayAlert("Библиотека", $"Ошибка: {ex.Message}", "OK");
        }
        finally
        {
            _isSearching = false;
            BtnSearchSchemes.IsEnabled = true;
        }
    }

    private void OnCloseSearchClicked(object? sender, EventArgs e)
    {
        RestoreLocalSchemeView();
    }

    /// <summary>Вернуть отображение схемы в приложении.</summary>
    private void RestoreLocalSchemeView()
    {
        try
        {
            SearchResultsScroll.IsVisible = false;
            ImageScrollView.IsVisible = false;
            DiagramPlaceholder.IsVisible = false;

            var view = _currentView ?? _diagram?.Views.FirstOrDefault();
            if (view != null || _serverNodes.Count > 0)
            {
                _currentView = view;
                ShowComponentListUI(view, _drawable.HighlightLevels);
            }
            else
            {
                ComponentListScroll.IsVisible = false;
                DiagramPlaceholder.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"RestoreLocal: {ex.Message}");
        }
    }

    private View CreateResultCard(SchemeSearchItem item)
    {
        var domain = "🔗";
        try
        {
            var uri = new Uri(item.url);
            domain = $"🌐 {uri.Host.Replace("www.", "")}";
        }
        catch { }

        // Значок источника
        var sourceIcon = item.source switch
        {
            "google_cse" => "🔍 Google",
            "yandex" => "🖼️ Яндекс.Картинки",
            "direct" => "📎",
            _ => "🔍 DDG",
        };

        var card = new Border
        {
            BackgroundColor = Color.FromArgb("#252525"),
            Stroke = Color.FromArgb("#333333"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(12, 10),
        };

        var outerStack = new VerticalStackLayout { Spacing = 6 };

        // Строка с миниатюрой (если есть) и текстом
        var contentRow = new HorizontalStackLayout { Spacing = 10 };

        // Миниатюра — только для Google CSE изображений
        if (!string.IsNullOrWhiteSpace(item.thumbnail) && item.thumbnail.StartsWith("http"))
        {
            try
            {
                var thumbnail = new Image
                {
                    Source = ImageSource.FromUri(new Uri(item.thumbnail)),
                    WidthRequest = 60,
                    HeightRequest = 60,
                    Aspect = Aspect.AspectFill,
                };
                var thumbnailBorder = new Border
                {
                    WidthRequest = 60,
                    HeightRequest = 60,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
                    Stroke = Color.FromArgb("#444444"),
                    Content = thumbnail,
                };
                contentRow.Children.Add(thumbnailBorder);
            }
            catch { /* fallback: без миниатюры */ }
        }
        // Иконка для Яндекс.Картинок
        else if (item.source == "yandex")
        {
            contentRow.Children.Add(new Label
            {
                Text = "🖼️",
                FontSize = 28,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                WidthRequest = 60,
            });
        }

        var textStack = new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center };

        // Заголовок
        textStack.Children.Add(new Label
        {
            Text = item.title,
            FontFamily = "InterSemiBold",
            FontSize = 13,
            TextColor = Color.FromArgb("#64B5F6"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 2,
        });

        // Сниппет
        if (!string.IsNullOrWhiteSpace(item.snippet))
        {
            textStack.Children.Add(new Label
            {
                Text = item.snippet,
                FontFamily = "InterRegular",
                FontSize = 11,
                TextColor = Color.FromArgb("#9E9E9E"),
                LineBreakMode = LineBreakMode.TailTruncation,
                MaxLines = 2,
            });
        }

        contentRow.Children.Add(textStack);
        outerStack.Children.Add(contentRow);

        // Нижняя строка: источник + домен
        var footerRow = new HorizontalStackLayout { Spacing = 8 };
        footerRow.Children.Add(new Label
        {
            Text = sourceIcon,
            FontFamily = "InterRegular",
            FontSize = 9,
            TextColor = Color.FromArgb("#616161"),
        });
        footerRow.Children.Add(new Label
        {
            Text = domain,
            FontFamily = "InterRegular",
            FontSize = 9,
            TextColor = Color.FromArgb("#757575"),
        });
        outerStack.Children.Add(footerRow);

        card.Content = outerStack;

        // Клик: картинки (direct/google_cse/…) — скачиваем и показываем в приложении
        var tap = new TapGestureRecognizer();
        var capturedItem = item;
        tap.Tapped += async (_, _) =>
        {
            var img = capturedItem.full_image_url;
            if (string.IsNullOrWhiteSpace(img)) img = capturedItem.image_url;
            if (string.IsNullOrWhiteSpace(img)) img = capturedItem.url;

            var isImage = !string.IsNullOrWhiteSpace(img) &&
                (capturedItem.source is "direct" or "google_cse" or "yandex" ||
                 img.Contains("/image", StringComparison.OrdinalIgnoreCase) ||
                 img.Contains(".png", StringComparison.OrdinalIgnoreCase) ||
                 img.Contains(".jpg", StringComparison.OrdinalIgnoreCase) ||
                 img.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                 img.Contains(".webp", StringComparison.OrdinalIgnoreCase) ||
                 img.Contains(".svg", StringComparison.OrdinalIgnoreCase));

            if (isImage)
            {
                await DownloadAndSaveImageAsync(capturedItem);
            }
            else
            {
                var linkUrl = !string.IsNullOrWhiteSpace(capturedItem.page_url)
                    ? capturedItem.page_url
                    : capturedItem.url;
                try
                {
                    await Browser.Default.OpenAsync(linkUrl, BrowserLaunchMode.SystemPreferred);
                }
                catch
                {
                    await DisplayAlert("Ошибка", "Не удалось открыть ссылку в браузере", "OK");
                }
            }
        };
        card.GestureRecognizers.Add(tap);

        return card;
    }

    // ═══════════════════════════════════════════════════
    //  ЭТАП 3.4: Скачивание и сохранение картинок-схем
    // ═══════════════════════════════════════════════════

    private async Task DownloadAndSaveImageAsync(SchemeSearchItem item)
    {
        if (_isSearching) return;

        try
        {
            SearchLoadingIndicator.IsVisible = true;
            BtnSearchSchemes.IsEnabled = false;
            _isSearching = true;

            var imageUrl = item.full_image_url;
            if (string.IsNullOrWhiteSpace(imageUrl))
                imageUrl = item.image_url;
            if (string.IsNullOrWhiteSpace(imageUrl))
                imageUrl = item.url;

            // SVG / server image: на Windows MAUI Image → белый экран. Открываем в браузере.
            if (imageUrl.Contains("/image", StringComparison.OrdinalIgnoreCase) ||
                imageUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                imageUrl.Contains(".svg", StringComparison.OrdinalIgnoreCase))
            {
                SearchLoadingIndicator.IsVisible = false;
                try
                {
                    await Browser.Default.OpenAsync(imageUrl, BrowserLaunchMode.SystemPreferred);
                    SearchResultsCount.Text = "SVG открыт в браузере (в приложении не рисуется).";
                }
                catch (Exception ex)
                {
                    await DisplayAlert("SVG", $"Не удалось открыть: {ex.Message}", "OK");
                }
                return;
            }

            var diagramDb = new DiagramDbService();
            var localPath = await diagramDb.DownloadAndSaveImageDiagramAsync(
                _carBrand, _carModel, _errorCode,
                imageUrl,
                sourceUrl: item.page_url ?? item.url,
                source: "internet");

            SearchLoadingIndicator.IsVisible = false;

            if (localPath != null && File.Exists(localPath) &&
                !localPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                await diagramDb.MarkRequestAsFoundAsync(_carBrand, _carModel, _errorCode);
                ShowImageDiagram(localPath);
            }
            else
            {
                try
                {
                    await Browser.Default.OpenAsync(imageUrl, BrowserLaunchMode.SystemPreferred);
                    SearchResultsCount.Text = "Картинка открыта в браузере.";
                }
                catch
                {
                    await DisplayAlert("Ошибка", "Не удалось скачать схему. Попробуйте другую.", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            SearchLoadingIndicator.IsVisible = false;
            await DisplayAlert("Ошибка", $"Не удалось скачать схему: {ex.Message}", "OK");
            RestoreLocalSchemeView();
        }
        finally
        {
            _isSearching = false;
            BtnSearchSchemes.IsEnabled = true;
        }
    }

    private void ShowImageDiagram(string imagePath)
    {
        // SVG → не показывать в Image (белый экран на Windows)
        if (string.IsNullOrWhiteSpace(imagePath) ||
            imagePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            RestoreLocalSchemeView();
            return;
        }

        try
        {
            ComponentListScroll.IsVisible = false;
            DiagramPlaceholder.IsVisible = false;
            SearchResultsScroll.IsVisible = false;

            ImageScrollView.IsVisible = true;
            ImageScrollView.BackgroundColor = Color.FromArgb("#121212");
            DownloadedSchemeImage.Source = ImageSource.FromFile(imagePath);

            LabelPageTitle.Text = "Схема (картинка)";
            LabelCarInfo.Text = $"{_carBrand} {_carModel}".Trim();
            BtnSearchSchemes.Text = "🌐 Найти онлайн";
            BtnSearchSchemes.IsEnabled = true;

            _imageDiagramPath = imagePath;
        }
        catch (Exception ex)
        {
            WriteSchemeLog($"ShowImage: {ex.Message}");
            RestoreLocalSchemeView();
        }
    }
}
