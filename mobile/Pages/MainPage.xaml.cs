using CarDiagnosticApp.Services;
using CarDiagnosticApp.Models;
using CarDiagnosticApp.Agents;
using System.Collections.ObjectModel;

namespace CarDiagnosticApp.Pages;

public partial class MainPage : ContentPage
{
    private readonly ApiService _api;
    private BluetoothService? _bt;
    private readonly ErrorHistoryService _errorHistory;
    private readonly OfflineCacheService _offlineCache;
    private readonly SyncService _sync;
    private readonly LocalDatabase _db;
    private readonly OfflineDatabase _offlineDb;
    private TestModeService _testService;
    private bool _isTestMode = false;
    private List<CarBrand> _carBrands;
    private string? _currentVin;
    private VinDecodeResult? _detectedVehicle;
    private List<ObdError> _allDetectedErrors = new();
    /// <summary>Блокирует OnBrandSelected при программном автовыборе (Android crash).</summary>
    private bool _suppressPickerEvents;

    private readonly ObservableCollection<ObdError> _currentErrors = new();
    private readonly ObservableCollection<ObdError> _pendingErrors = new();
    private readonly ObservableCollection<ObdError> _permanentErrors = new();

    public MainPage()
    {
        InitializeComponent();
        _api = IPlatformApplication.Current!.Services.GetRequiredService<ApiService>();
        _errorHistory = new ErrorHistoryService();
        _db = new LocalDatabase();
        _offlineDb = new OfflineDatabase();
        _offlineCache = new OfflineCacheService(_offlineDb);
        _sync = new SyncService(_api, _db, _offlineDb);
        _testService = new TestModeService();
        _carBrands = new List<CarBrand>();

        CurrentErrorsList.ItemsSource = _currentErrors;
        PendingErrorsList.ItemsSource = _pendingErrors;
        PermanentErrorsList.ItemsSource = _permanentErrors;

        ScanButton.Clicked += OnScanClicked;
        DiagnoseButton.Clicked += OnDiagnoseClicked;
        TestModeButton.Clicked += OnTestModeClicked;

        // Версия + tier
        try { VersionLabel.Text = $"v{AppInfo.Current.VersionString} ({AppSettings.UserTier})"; } catch { }

        // Блокировка Pro-функций для Free
        if (!AppSettings.IsAiAvailable)
        {
            DiagnoseButton.Text = "🤖 Диагностика ИИ\n(Pro)";
            DiagnoseButton.BackgroundColor = Color.FromArgb("#9E9E9E");
        }

        _ = LoadCarBrandsAsync();
        _ = _errorHistory.InitAsync();
        _ = _offlineDb.InitAsync();
        _ = BackgroundSyncAsync();

        // Проверяем интернет при запуске (уже проверено в App, но обновим индикатор)
        _ = RefreshConnectivityAsync();

        // Подписываемся на изменения сети через наш сервис
        App.Connectivity.ConnectivityChanged += OnAppConnectivityChanged;

        // Периодическая проверка связи — восстанавливаем онлайн после сбоев
        _ = StartPeriodicConnectivityCheckAsync();
    }

    /// <summary>
    /// Каждые 30 секунд проверяем, не восстановилась ли связь.
    /// Не запускаем, если уже онлайн.
    /// </summary>
    private async Task StartPeriodicConnectivityCheckAsync()
    {
        await Task.Delay(10000); // Первый запуск через 10 сек
        while (true)
        {
            try
            {
                if (!App.Connectivity.IsOnline)
                {
                    await App.Connectivity.CheckNowAsync();
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        UpdateConnectivityIndicator(App.Connectivity.IsOnline);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] Periodic check error: {ex.Message}");
            }
            await Task.Delay(30000); // Каждые 30 секунд
        }
    }

    private async Task LoadCarBrandsAsync()
    {
        try
        {
            List<CarBrand>? brands = null;
            try { brands = await _api.GetCarBrands(); } catch { /* offline */ }

            // Кеш марок
            if (brands == null || brands.Count == 0)
            {
                try
                {
                    var cache = new CarBrandCacheService();
                    brands = await cache.LoadBrandsAsync();
                }
                catch { }
            }

            // Офлайн-каталог (для автоопределения VIN без сервера)
            if (brands == null || brands.Count == 0)
                brands = VinDecoderService.GetOfflineBrandCatalog();
            else
                brands = MergeBrandCatalogs(brands, VinDecoderService.GetOfflineBrandCatalog());

            _carBrands = brands;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    pickerBrand.ItemsSource = _carBrands.Select(b => b.brand).ToList();
                    pickerBrand.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainPage] LoadCarBrands UI fail: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] LoadCarBrands: {ex.Message}");
            _carBrands = VinDecoderService.GetOfflineBrandCatalog();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    pickerBrand.ItemsSource = _carBrands.Select(b => b.brand).ToList();
                    pickerBrand.IsEnabled = true;
                    StatusLabel.Text = "Марки: офлайн-каталог";
                }
                catch { }
            });
        }
    }

    /// <summary>Объединяет API-список с офлайн-каталогом (добавляет недостающие марки/модели).</summary>
    private static List<CarBrand> MergeBrandCatalogs(List<CarBrand> primary, List<CarBrand> offline)
    {
        var result = primary.Select(b => new CarBrand
        {
            brand = b.brand,
            models = b.models?.ToList() ?? new List<string>()
        }).ToList();

        foreach (var ob in offline)
        {
            var existing = result.FirstOrDefault(b =>
                string.Equals(b.brand, ob.brand, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                result.Add(new CarBrand { brand = ob.brand, models = ob.models.ToList() });
            }
            else
            {
                foreach (var m in ob.models)
                {
                    if (!existing.models.Any(x => string.Equals(x, m, StringComparison.OrdinalIgnoreCase)))
                        existing.models.Add(m);
                }
            }
        }
        return result;
    }

    private void OnBrandSelected(object? sender, EventArgs e)
    {
        if (_suppressPickerEvents) return;

        try
        {
            var selectedBrand = pickerBrand.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(selectedBrand)) return;

            // Case-insensitive: API может вернуть "Lada", а VIN-декодер — "LADA"
            var brand = _carBrands.FirstOrDefault(b =>
                string.Equals(b.brand, selectedBrand, StringComparison.OrdinalIgnoreCase));
            if (brand == null) return;

            ApplyModelsToPicker(brand.models, preserveSelection: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] OnBrandSelected: {ex.Message}");
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"OnBrandSelected: {ex}");
#endif
        }
    }

    /// <summary>
    /// Безопасная установка списка моделей в Picker (Android не любит SelectedIndex = -1).
    /// </summary>
    private void ApplyModelsToPicker(IEnumerable<string>? models, bool preserveSelection, string? preferModel = null)
    {
        var list = (models ?? Enumerable.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = preserveSelection ? pickerModel.SelectedItem?.ToString() : null;
        if (!string.IsNullOrWhiteSpace(preferModel))
            previous = preferModel;

        // На Android: не трогаем SelectedIndex=-1; задаём SelectedItem из того же списка
        try
        {
            pickerModel.SelectedItem = null;
            pickerModel.ItemsSource = list;
            pickerModel.IsEnabled = list.Count > 0;
            pickerModel.Title = list.Count > 0 ? "Выберите модель" : "Нет моделей";

            if (list.Count == 0 || string.IsNullOrWhiteSpace(previous))
                return;

            var match = list.FirstOrDefault(m =>
                string.Equals(m, previous, StringComparison.OrdinalIgnoreCase));
            if (match == null)
                match = VinDecoderService.MatchModelInList(previous, list);

            if (match != null)
            {
                // Элемент должен быть reference из list (ItemsSource)
                var item = list.FirstOrDefault(m =>
                    string.Equals(m, match, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                    pickerModel.SelectedItem = item;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] ApplyModelsToPicker: {ex.Message}");
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"ApplyModelsToPicker: {ex}");
#endif
        }
    }

    /// <summary>
    /// Безопасный выбор марки по имени (элемент из ItemsSource + индекс).
    /// </summary>
    private bool SelectBrandSafe(string? brandName)
    {
        if (string.IsNullOrWhiteSpace(brandName) || _carBrands.Count == 0)
            return false;

        var brandEntry = _carBrands.FirstOrDefault(b =>
            string.Equals(b.brand, brandName, StringComparison.OrdinalIgnoreCase));
        brandEntry ??= _carBrands.FirstOrDefault(b =>
            VinDecoderService.MatchBrandInList(brandName, new[] { b.brand }) != null);

        if (brandEntry == null)
            return false;

        var names = _carBrands.Select(b => b.brand).ToList();
        // Всегда подставляем свежий list как ItemsSource — reference для SelectedItem
        pickerBrand.ItemsSource = names;
        pickerBrand.IsEnabled = true;

        var item = names.FirstOrDefault(n =>
            string.Equals(n, brandEntry.brand, StringComparison.OrdinalIgnoreCase));
        if (item == null) return false;

        _suppressPickerEvents = true;
        try
        {
            pickerBrand.SelectedItem = item;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainPage] SelectBrandSafe: {ex.Message}");
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"SelectBrandSafe: {ex}");
#endif
            return false;
        }
        finally
        {
            _suppressPickerEvents = false;
        }

        return true;
    }

    /// <summary>
    /// Расшифровывает VIN и автоматически выбирает марку/модель/год в UI.
    /// </summary>
    private async Task ApplyVehicleFromVinAsync(string? vin, string? calId = null)
    {
        if (string.IsNullOrWhiteSpace(vin))
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                VehicleInfoCard.IsVisible = false;
                VehicleInfoLabel.Text = "";
            });
            return;
        }

        // Если список марок ещё пуст — подгружаем
        if (_carBrands.Count == 0)
            await LoadCarBrandsAsync();

        var decoded = VinDecoderService.Decode(vin);
        _detectedVehicle = decoded;

        // Подсказка из CALID (если модель не определена)
        if (string.IsNullOrEmpty(decoded.Model) && !string.IsNullOrWhiteSpace(calId))
        {
            var calHint = GuessModelFromCalId(calId, decoded.Brand);
            if (!string.IsNullOrEmpty(calHint))
            {
                decoded = new VinDecodeResult
                {
                    Vin = decoded.Vin,
                    Brand = decoded.Brand,
                    Model = calHint,
                    Year = decoded.Year,
                    Manufacturer = decoded.Manufacturer,
                    Wmi = decoded.Wmi,
                    Plant = decoded.Plant,
                    Confidence = Math.Max(decoded.Confidence, 0.6),
                    Summary = VinDecoderService.Decode(vin).Summary + $" · ECU: {calId}",
                    IsValid = decoded.IsValid,
                };
                _detectedVehicle = decoded;
            }
        }

        // История по VIN: если уже диагностировали — берём марку/модель оттуда
        if ((string.IsNullOrEmpty(decoded.Brand) || string.IsNullOrEmpty(decoded.Model))
            && !string.IsNullOrEmpty(vin))
        {
            try
            {
                var cars = await _errorHistory.GetCarsAsync();
                var known = cars.FirstOrDefault(c =>
                    string.Equals(c.vin, vin, StringComparison.OrdinalIgnoreCase));
                if (known.vin != null)
                {
                    decoded = new VinDecodeResult
                    {
                        Vin = decoded.Vin,
                        Brand = string.IsNullOrEmpty(decoded.Brand) ? known.brand : decoded.Brand,
                        Model = string.IsNullOrEmpty(decoded.Model) ? known.model : decoded.Model,
                        Year = decoded.Year,
                        Manufacturer = decoded.Manufacturer,
                        Wmi = decoded.Wmi,
                        Plant = decoded.Plant,
                        Confidence = Math.Max(decoded.Confidence, 0.9),
                        Summary = $"{known.brand} {known.model}".Trim() +
                                  (decoded.Year is > 0 ? $" {decoded.Year}" : "") +
                                  " (из истории)",
                        IsValid = true,
                    };
                    _detectedVehicle = decoded;
                }
            }
            catch { }
        }

        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            try
            {
                // VIN label
                var yearPart = decoded.Year is > 0 ? $" · {decoded.Year} г." : "";
                VinLabel.Text = $"VIN: {decoded.Vin}{yearPart}";
                VinLabel.IsVisible = true;

                if (!decoded.IsValid || (string.IsNullOrEmpty(decoded.Brand) && string.IsNullOrEmpty(decoded.Model)))
                {
                    VehicleInfoCard.IsVisible = true;
                    VehicleInfoLabel.Text = string.IsNullOrEmpty(decoded.Vin)
                        ? "VIN не прочитан — выберите марку вручную"
                        : $"VIN: {decoded.Vin} — марка не распознана, выберите вручную";
                    return;
                }

                // Автовыбор марки
                var brandNames = _carBrands.Select(b => b.brand).ToList();
                var matchedBrand = VinDecoderService.MatchBrandInList(decoded.Brand, brandNames);

                // Если марки нет в списке — добавляем
                if (matchedBrand == null && !string.IsNullOrEmpty(decoded.Brand))
                {
                    var models = new List<string>();
                    if (!string.IsNullOrEmpty(decoded.Model))
                        models.Add(decoded.Model);
                    _carBrands.Add(new CarBrand { brand = decoded.Brand, models = models });
                    matchedBrand = decoded.Brand;
                    pickerBrand.ItemsSource = _carBrands.Select(b => b.brand).ToList();
                    pickerBrand.IsEnabled = true;
                }

                if (!string.IsNullOrEmpty(matchedBrand))
                {
                    var brandEntry = _carBrands.FirstOrDefault(b =>
                        string.Equals(b.brand, matchedBrand, StringComparison.OrdinalIgnoreCase));

                    if (brandEntry != null)
                    {
                        brandEntry.models ??= new List<string>();
                        if (!string.IsNullOrEmpty(decoded.Model) &&
                            !brandEntry.models.Any(m =>
                                string.Equals(m, decoded.Model, StringComparison.OrdinalIgnoreCase)))
                        {
                            brandEntry.models.Insert(0, decoded.Model);
                        }

                        // Сначала марка (с suppress), затем модели — без SelectedIndex=-1
                        SelectBrandSafe(brandEntry.brand);
                        ApplyModelsToPicker(brandEntry.models, preserveSelection: false, preferModel: decoded.Model);
                    }
                }

                VehicleInfoCard.IsVisible = true;
                var conf = decoded.Confidence >= 0.8 ? "высокая" :
                           decoded.Confidence >= 0.5 ? "средняя" : "низкая";
                VehicleInfoLabel.Text =
                    $"{decoded.Brand} {decoded.Model}".Trim() +
                    (decoded.Year is > 0 ? $", {decoded.Year} г." : "") +
                    $"\nУверенность: {conf}" +
                    (string.IsNullOrEmpty(decoded.Manufacturer) ? "" : $" · {decoded.Manufacturer}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainPage] ApplyVehicle UI: {ex}");
#if ANDROID
                Android.Util.Log.Error("AutoDiag", $"ApplyVehicle UI: {ex}");
#endif
                // Не роняем приложение — пользователь выберет марку вручную
                try
                {
                    VehicleInfoCard.IsVisible = true;
                    VehicleInfoLabel.Text = $"VIN: {decoded.Vin} (выберите марку вручную)";
                }
                catch { }
            }
        });
    }

    /// <summary>
    /// Ответ API — HTML-страница ошибки (Render Suspended / Cloudflare / 502), не диагноз.
    /// </summary>
    private static bool LooksLikeHttpErrorPage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.TrimStart();
        if (t.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return true;
        var lower = t.ToLowerInvariant();
        if (lower.Contains("service suspended") ||
            lower.Contains("bad gateway") ||
            lower.Contains("cloudflare") && lower.Contains("error") ||
            lower.Contains("error 502") ||
            lower.Contains("error 503") ||
            lower.Contains("this service has been suspended"))
            return true;
        return false;
    }

    /// <summary>
    /// Грубый фильтр: AI-ответ явно про другую марку (не алиас выбранной).
    /// </summary>
    private static bool LooksLikeWrongBrand(string diagnosis, string selectedBrand)
    {
        if (string.IsNullOrWhiteSpace(diagnosis) || string.IsNullOrWhiteSpace(selectedBrand))
            return false;

        var selected = CarDiagnosticApp.Data.DiagramDatabase.NormalizeBrand(selectedBrand);
        var aliases = new HashSet<string>(
            CarDiagnosticApp.Data.DiagramDatabase.BrandAliases(selectedBrand),
            StringComparer.OrdinalIgnoreCase);
        aliases.Add(selectedBrand);
        aliases.Add(selected);

        // Ищем явные упоминания «чужих» марок в ответе
        var known = new (string Token, string Norm)[]
        {
            ("toyota", "TOYOTA"), ("тойота", "TOYOTA"),
            ("hyundai", "HYUNDAI"), ("хёндэ", "HYUNDAI"), ("хендай", "HYUNDAI"),
            ("kia", "KIA"), ("киа", "KIA"),
            ("volkswagen", "VOLKSWAGEN"), ("фольксваген", "VOLKSWAGEN"),
            ("bmw", "BMW"), ("бмв", "BMW"),
            ("mercedes", "MERCEDES-BENZ"), ("мерседес", "MERCEDES-BENZ"),
            ("ford", "FORD"), ("форд", "FORD"),
            ("nissan", "NISSAN"), ("ниссан", "NISSAN"),
            ("renault", "RENAULT"), ("рено", "RENAULT"),
            ("chevrolet", "CHEVROLET"), ("шевроле", "CHEVROLET"),
            ("lada", "ВАЗ"), ("лада", "ВАЗ"), ("ваз", "ВАЗ"), ("автоваз", "ВАЗ"),
            ("камаз", "КАМАЗ"), ("kamaz", "КАМАЗ"),
            ("уаз", "УАЗ"), ("uaz", "УАЗ"),
            ("газ", "ГАЗ"), ("gaz", "ГАЗ"), ("газель", "ГАЗ"),
        };

        var lower = diagnosis.ToLowerInvariant();
        foreach (var (token, norm) in known)
        {
            if (!lower.Contains(token)) continue;
            // Своя марка / алиас — ок
            if (norm == selected || aliases.Contains(norm) ||
                CarDiagnosticApp.Data.DiagramDatabase.BrandsMatch(norm, selectedBrand))
                continue;
            // Чужое упоминание — плохо, если своей марки в тексте нет
            var hasOwn = aliases.Any(a =>
                !string.IsNullOrEmpty(a) && lower.Contains(a.ToLowerInvariant()));
            if (!hasOwn && diagnosis.Length > 80)
                return true;
        }
        return false;
    }

    private static string EnsureBrandInDiagnosis(string text, string brand, string model, string code)
    {
        if (string.IsNullOrWhiteSpace(text)) text = $"Код {code}";
        var header = $"Автомобиль: {brand} {model}".Trim();
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        lines = lines.Where(l =>
            !l.TrimStart().StartsWith("Автомобиль:", StringComparison.OrdinalIgnoreCase)).ToList();

        // В начало секции 1 или в самое начало
        var idx = lines.FindIndex(l => l.TrimStart().StartsWith("1."));
        if (idx >= 0 && idx + 1 <= lines.Count)
            lines.Insert(idx + 1, header);
        else
            lines.Insert(0, header);

        return string.Join("\n", lines);
    }

    private static string GuessModelFromCalId(string calId, string brand)
    {
        var c = calId.ToUpperInvariant();
        if (brand is "LADA" or "ВАЗ" or "")
        {
            if (c.Contains("VESTA") || c.Contains("GFL") || c.Contains("21179")) return "Vesta";
            if (c.Contains("GRANTA") || c.Contains("2190") || c.Contains("11186")) return "Granta";
            if (c.Contains("LARGUS") || c.Contains("2180")) return "Largus";
            if (c.Contains("NIVA") || c.Contains("21214") || c.Contains("2123")) return "Niva Legend";
            if (c.Contains("PRIORA") || c.Contains("2170")) return "Priora";
        }
        if (brand is "УАЗ" or "UAZ" or "")
        {
            if (c.Contains("PATRIOT") || c.Contains("3163")) return "Patriot";
            if (c.Contains("HUNTER") || c.Contains("3151")) return "Hunter";
        }
        if (brand is "ГАЗ" or "GAZ" or "")
        {
            if (c.Contains("NEXT") || c.Contains("A21R")) return "Газель NEXT";
            if (c.Contains("SOBOL") || c.Contains("2752")) return "Соболь";
        }
        if (brand is "КАМАЗ" or "KAMAZ" or "")
        {
            if (c.Contains("54901")) return "54901";
            if (c.Contains("5490")) return "5490";
        }
        return "";
    }

    private void OnTestModeClicked(object? sender, EventArgs e)
    {
        _isTestMode = !_isTestMode;

        if (_isTestMode)
        {
            _testService.EnableTestMode();
            TestModeButton.Text = "🧪 Тестовый режим ВКЛ";
            TestModeButton.BackgroundColor = Color.FromArgb("#F44336");
            StatusLabel.Text = "Статус: тестовый режим — LADA Vesta (есть схема ВАЗ)";
            // Сразу выбираем марку/модель, для которой есть mapping_vaz.json (LADA→ВАЗ)
            try { ApplyTestVehicleSelection("LADA", "Vesta"); } catch { }
        }
        else
        {
            _testService.DisableTestMode();
            TestModeButton.Text = "🧪 Тестовый режим (без авто)";
            TestModeButton.BackgroundColor = Color.FromArgb("#FF9800");
            StatusLabel.Text = "Статус: готов";
        }
    }

    /// <summary>
    /// Тестовый автомобиль по умолчанию: LADA Vesta.
    /// Схемы в библиотеке привязаны к марке (ВАЗ/LADA), не к 2109.
    /// Старый VIN XTA2109… всегда давал модель 2109 — без явной схемы «2109».
    /// </summary>
    private const string TestVinVesta = "XTAGFL110Y2765432"; // WMI=XTA, VDS=GFL11 → Vesta
    private const string TestBrandDefault = "LADA";
    private const string TestModelDefault = "Vesta";

    /// <summary>Безопасно выставляет марку/модель в пикерах для тестового режима.</summary>
    private void ApplyTestVehicleSelection(string brand, string model)
    {
        if (_carBrands.Count == 0)
        {
            // Офлайн-каталог уже содержит LADA/Vesta
            _carBrands = VinDecoderService.GetOfflineBrandCatalog();
            pickerBrand.ItemsSource = _carBrands.Select(b => b.brand).ToList();
            pickerBrand.IsEnabled = true;
        }

        if (!SelectBrandSafe(brand))
        {
            // Попробуем ВАЗ как алиас
            if (!SelectBrandSafe("ВАЗ") && !SelectBrandSafe("Lada"))
                return;
        }

        var selectedBrandName = pickerBrand.SelectedItem?.ToString() ?? brand;
        var brandEntry = _carBrands.FirstOrDefault(b =>
            string.Equals(b.brand, selectedBrandName, StringComparison.OrdinalIgnoreCase));
        var models = brandEntry?.models ?? new List<string> { model, "Vesta", "Granta", "2114", "2109" };
        ApplyModelsToPicker(models, preserveSelection: false, preferModel: model);

        _detectedVehicle = new VinDecodeResult
        {
            Vin = TestVinVesta,
            Brand = brandEntry?.brand ?? brand,
            Model = model,
            Year = 2020,
            Manufacturer = "АвтоВАЗ",
            Confidence = 0.95,
            IsValid = true,
            Summary = $"{brand} {model} (тестовый режим)",
        };
        _currentVin = TestVinVesta;

        try
        {
            VinLabel.IsVisible = true;
            VinLabel.Text = $"VIN: {TestVinVesta}";
            VehicleInfoCard.IsVisible = true;
            VehicleInfoLabel.Text = $"{_detectedVehicle.Brand} {_detectedVehicle.Model} · тест";
        }
        catch { }
    }

    private async void OnScanClicked(object? sender, EventArgs e)
    {
        LoadingIndicator.IsRunning = true;
        ScanButton.IsEnabled = false;
        StatusLabel.Text = _isTestMode ? "Статус: имитация сканирования..." : "Статус: поиск устройств...";
        VinLabel.IsVisible = false;
        VehicleInfoCard.IsVisible = false;
        _detectedVehicle = null;

        ClearErrors();

        if (_isTestMode)
        {
            // ВАЖНО: после await UI/ObservableCollection только на MainThread —
            // на Android обновление с фонового потока = мгновенный вылет.
            try
            {
                await Task.Delay(500).ConfigureAwait(true);

                _currentVin = TestVinVesta;

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    StatusLabel.Text = "Определение авто по VIN (тест: Vesta)...";
                });

                try { await ApplyVehicleFromVinAsync(_currentVin); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Scan] test ApplyVehicle: {ex.Message}");
#if ANDROID
                    Android.Util.Log.Error("AutoDiag", $"test ApplyVehicle: {ex}");
#endif
                }

                // Коды, которые есть в mapping_vaz (подсветка узлов на схеме)
                var testCurrent = new[]
                {
                    new ObdError { Code = "P0134", Type = ObdErrorType.Current },
                    new ObdError { Code = "P0301", Type = ObdErrorType.Current },
                };
                var testPending = new[]
                {
                    new ObdError { Code = "P0200", Type = ObdErrorType.Pending },
                    new ObdError { Code = "P0420", Type = ObdErrorType.Pending },
                    new ObdError { Code = "P0171", Type = ObdErrorType.Pending },
                    new ObdError { Code = "P0134", Type = ObdErrorType.Pending },
                };
                var testPermanent = new[]
                {
                    new ObdError { Code = "P0442", Type = ObdErrorType.Permanent },
                };

                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    try
                    {
                        // Гарантируем Vesta в UI (декодер/алиасы могут дать другой вариант)
                        ApplyTestVehicleSelection(TestBrandDefault, TestModelDefault);

                        _currentErrors.Clear();
                        _pendingErrors.Clear();
                        _permanentErrors.Clear();

                        foreach (var e0 in testCurrent) _currentErrors.Add(e0);
                        foreach (var e0 in testPending) _pendingErrors.Add(e0);
                        foreach (var e0 in testPermanent) _permanentErrors.Add(e0);

                        _allDetectedErrors = new List<ObdError>();
                        _allDetectedErrors.AddRange(testCurrent);
                        _allDetectedErrors.AddRange(testPending);
                        _allDetectedErrors.AddRange(testPermanent);

                        UpdateErrorDisplay();
                        // AI — при любой DTC (текущие / pending / permanent)
                        DiagnoseButton.IsEnabled =
                            _currentErrors.Count + _pendingErrors.Count + _permanentErrors.Count > 0;
                        ClearButton.IsEnabled = true;
                        GraphButton.IsEnabled = true;
                        DashboardButton.IsEnabled = true;
                        AnalysisButton.IsEnabled = !string.IsNullOrEmpty(_currentVin);
                        HistoryErrorsButton.IsEnabled = !string.IsNullOrEmpty(_currentVin);
                        StatusLabel.Text = "Статус: тест LADA Vesta + ошибки (схема ВАЗ)";
                    }
                    catch (Exception uiEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Scan] test UI: {uiEx.Message}");
#if ANDROID
                        Android.Util.Log.Error("AutoDiag", $"test UI: {uiEx}");
#endif
                        StatusLabel.Text = "Статус: тест (частично) — " + uiEx.Message;
                    }
                });

                try
                {
                    var brand = TestBrandDefault;
                    var model = TestModelDefault;
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        brand = pickerBrand.SelectedItem?.ToString()
                            ?? _detectedVehicle?.Brand ?? TestBrandDefault;
                        model = pickerModel.SelectedItem?.ToString()
                            ?? _detectedVehicle?.Model ?? TestModelDefault;
                    });
                    var sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    await _errorHistory.SaveErrorsAsync(_currentVin ?? TestVinVesta, brand, model,
                        _allDetectedErrors, sessionId);
                    await _errorHistory.RecalculateRiskForVinAsync(_currentVin ?? TestVinVesta);
                }
                catch (Exception histEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[Scan] test history: {histEx.Message}");
#if ANDROID
                    Android.Util.Log.Error("AutoDiag", $"test history: {histEx}");
#endif
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Scan] test mode fail: {ex}");
#if ANDROID
                Android.Util.Log.Error("AutoDiag", $"test mode scan: {ex}");
#endif
                try
                {
                    await MainThread.InvokeOnMainThreadAsync(async () =>
                    {
                        StatusLabel.Text = "Статус: ошибка теста";
                        await DisplayAlert("Ошибка тестового сканирования", ex.Message, "OK");
                    });
                }
                catch { }
            }
            finally
            {
                try
                {
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        LoadingIndicator.IsRunning = false;
                        ScanButton.IsEnabled = true;
                    });
                }
                catch
                {
                    LoadingIndicator.IsRunning = false;
                    ScanButton.IsEnabled = true;
                }
            }
            return;
        }

        try
        {
            StatusLabel.Text = "Проверка разрешений Bluetooth...";
            var permOk = await Services.PlatformPermissionService.EnsureBluetoothPermissionsAsync();
            if (!permOk)
            {
                await SafeAlertAsync(
                    "Bluetooth",
                    "Нужны разрешения: «Устройства поблизости» (Android 12+) " +
                    "или «Геолокация» (старые Android).\n\n" +
                    "Настройки → Приложения → АвтоДиагностика → Разрешения.");
                StatusLabel.Text = "Статус: нет разрешений Bluetooth";
                return;
            }

            StatusLabel.Text = "Подключение к ELM327 (сопряжённые устройства)...";
            string deviceName;
            try
            {
                deviceName = await EnsureBluetooth().ConnectAsync(12000);
            }
            catch (Exception btEx)
            {
                await SafeAlertAsync(
                    "Bluetooth / ELM327",
                    btEx.Message +
                    "\n\nСоветы:\n" +
                    "• Сопрягите адаптер в настройках Bluetooth телефона\n" +
                    "• Адаптер в разъёме OBD2, зажигание ON\n" +
                    "• Нужен Bluetooth classic (не только BLE)\n" +
                    "• Закройте другие OBD-приложения");
                StatusLabel.Text = "Статус: нет подключения к ELM327";
                return;
            }

            StatusLabel.Text = $"Подключено: {deviceName}. Чтение VIN...";

            // ── Чтение VIN + автоопределение ──
            _currentVin = await EnsureBluetooth().ReadVINAsync();

            string calId = "";
            try
            {
                StatusLabel.Text = "Чтение данных ECU...";
                calId = await EnsureBluetooth().ReadCalibrationIdAsync();
            }
            catch { }

            if (!string.IsNullOrEmpty(_currentVin))
            {
                StatusLabel.Text = "Определение марки и модели...";
                await ApplyVehicleFromVinAsync(_currentVin, calId);
            }
            else
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    VinLabel.Text = "VIN: не удалось прочитать";
                    VinLabel.IsVisible = true;
                    VehicleInfoCard.IsVisible = true;
                    VehicleInfoLabel.Text = "VIN недоступен — выберите марку и модель вручную";
                });
            }

            // ── Расширенное чтение ошибок ──
            StatusLabel.Text = "Чтение ошибок (03 + 07 + 0A)...";
            _allDetectedErrors = await EnsureBluetooth().ReadAllDTC();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                foreach (var err in _allDetectedErrors)
                {
                    switch (err.Type)
                    {
                        case ObdErrorType.Current:
                            _currentErrors.Add(err); break;
                        case ObdErrorType.Pending:
                            _pendingErrors.Add(err); break;
                        case ObdErrorType.Permanent:
                            _permanentErrors.Add(err); break;
                    }
                }
                UpdateErrorDisplay();
            });

            var total = _currentErrors.Count + _pendingErrors.Count + _permanentErrors.Count;

            // ── Readiness (Mode 01 PID 01): MIL и готовность мониторов ──
            string readinessText = "";
            try
            {
                var readiness = await EnsureBluetooth().ReadReadinessAsync();
                if (readiness != null)
                {
                    var notReady = readiness.Monitors.Count(m => m.Supported && !m.Complete);
                    readinessText = $" | MIL: {(readiness.MilOn ? "🔴 горит" : "погашена")}, мониторов не готово: {notReady}";
                }
            }
            catch { /* readiness не критичен для сканирования */ }

            // ── Проверка на повторяющиеся ──
            int recurringCount = 0;
            if (!string.IsNullOrEmpty(_currentVin))
            {
                try
                {
                    var recurring = await _errorHistory.GetRecurringErrorsForVinAsync(_currentVin);
                    recurringCount = recurring.Count;
                }
                catch { }
            }

            // ── Сохранение в историю ошибок по VIN (с авто-маркой) ──
            if (!string.IsNullOrEmpty(_currentVin) && _allDetectedErrors.Count > 0)
            {
                try
                {
                    var brand = pickerBrand.SelectedItem?.ToString()
                        ?? _detectedVehicle?.Brand ?? "";
                    var model = pickerModel.SelectedItem?.ToString()
                        ?? _detectedVehicle?.Model ?? "";
                    var sessionId = Guid.NewGuid().ToString("N")[..8];
                    await _errorHistory.SaveErrorsAsync(_currentVin, brand, model, _allDetectedErrors, sessionId);
                    await _errorHistory.RecalculateRiskForVinAsync(_currentVin);
                }
                catch { /* не блокируем UI */ }
            }

            // Живые графики доступны сразу после успешного скана (даже без DTC)
            GraphButton.IsEnabled = true;
            DashboardButton.IsEnabled = true;
            AnalysisButton.IsEnabled = !string.IsNullOrEmpty(_currentVin);
            HistoryErrorsButton.IsEnabled = !string.IsNullOrEmpty(_currentVin);

            if (total == 0)
            {
                StatusLabel.Text = "Статус: ошибок нет" + readinessText;
                if (recurringCount > 0)
                    StatusLabel.Text += $" (⚠ есть повторяющиеся: {recurringCount} в истории)";
                DiagnoseButton.IsEnabled = false;
                ClearButton.IsEnabled = false;
                await DisplayAlert("Результат", "✅ Ошибок не найдено!", "OK");
            }
            else
            {
                // AI-диагностика доступна при любой найденной DTC, не только Mode 03
                DiagnoseButton.IsEnabled = true;
                ClearButton.IsEnabled = true;

                // Максимальный риск
                int maxRisk = 0;
                if (!string.IsNullOrEmpty(_currentVin))
                {
                    try
                    {
                        var history = await _errorHistory.GetHistoryForVinAsync(_currentVin);
                        if (history.Count > 0)
                            maxRisk = history.Max(h => h.RiskScore);
                    }
                    catch { }
                }

                var riskIcon = maxRisk switch { >= 8 => "🔴", >= 5 => "🟠", >= 3 => "🟡", _ => "🟢" };
                StatusLabel.Text = $"Найдено ошибок: {total} (текущих: {_currentErrors.Count}, исторических: {_pendingErrors.Count}, подтверждённых: {_permanentErrors.Count})" + readinessText;
                if (maxRisk > 0)
                    StatusLabel.Text += $" | Риск: {riskIcon} {maxRisk}/10";
                if (recurringCount > 0)
                    StatusLabel.Text += $" ⚠ повтор: {recurringCount}";

                // ── Связки ошибок ──
                if (!string.IsNullOrEmpty(_currentVin) && total >= 2)
                {
                    try
                    {
                        var bundles = await _errorHistory.DetectBundlesAsync(_currentVin);
                        if (bundles.Count > 0)
                        {
                            var top = bundles.First();
                            StatusLabel.Text += $" | Связка: {top.CodeA}+{top.CodeB} ({top.Strength:P0})";
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
            StatusLabel.Text = "Статус: ошибка";
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            ScanButton.IsEnabled = true;
        }
    }

    private void UpdateErrorDisplay()
    {
        CurrentErrorsHeader.IsVisible = _currentErrors.Count > 0;
        CurrentErrorsHeader.Text = $"🔴 Текущие ошибки ({_currentErrors.Count})";
        CurrentErrorsList.IsVisible = _currentErrors.Count > 0;
        CurrentErrorsList.HeightRequest = Math.Min(_currentErrors.Count * 48, 200);

        PendingErrorsHeader.IsVisible = _pendingErrors.Count > 0;
        PendingErrorsHeader.Text = $"🟡 Исторические ошибки ({_pendingErrors.Count})";
        PendingErrorsList.IsVisible = _pendingErrors.Count > 0;
        PendingErrorsList.HeightRequest = Math.Min(_pendingErrors.Count * 48, 200);

        PermanentErrorsHeader.IsVisible = _permanentErrors.Count > 0;
        PermanentErrorsHeader.Text = $"🔵 Подтверждённые ошибки ({_permanentErrors.Count})";
        PermanentErrorsList.IsVisible = _permanentErrors.Count > 0;
        PermanentErrorsList.HeightRequest = Math.Min(_permanentErrors.Count * 48, 200);
    }

    private void ClearErrors()
    {
        _currentErrors.Clear();
        _pendingErrors.Clear();
        _permanentErrors.Clear();
        _allDetectedErrors.Clear();
        UpdateErrorDisplay();
    }

    private async void OnDiagnoseClicked(object? sender, EventArgs e)
    {
        // Проверка подписки для AI-диагностики
        if (!AppSettings.IsAiAvailable)
        {
            var buy = await DisplayAlert(
                "Диагностика ИИ — Pro",
                "AI-диагностика через DeepSeek доступна только в версии Pro (1 499 ₽ навсегда).\n\n" +
                "В бесплатной версии доступна офлайн-расшифровка кодов.",
                "Купить Pro",
                "Отмена");
            if (buy)
            {
                // Открыть страницу покупки (пока заглушка)
                await SafeAlertAsync("Покупка", "Свяжитесь с разработчиком для активации Pro.\nTelegram: @your_support");
            }
            return;
        }

        var totalErrors = _currentErrors.Count + _pendingErrors.Count + _permanentErrors.Count;
        if (totalErrors == 0) return;

        var brand = pickerBrand.SelectedItem?.ToString()
            ?? _detectedVehicle?.Brand
            ?? "";
        var model = pickerModel.SelectedItem?.ToString()
            ?? _detectedVehicle?.Model
            ?? "";

        // В тестовом режиме подставляем Vesta, если пикер ещё пуст
        if (string.IsNullOrWhiteSpace(brand) && _isTestMode) brand = TestBrandDefault;
        if (string.IsNullOrWhiteSpace(model) && _isTestMode) model = TestModelDefault;

        if (string.IsNullOrWhiteSpace(brand))
        {
            await SafeAlertAsync("Внимание", "Выберите марку автомобиля");
            return;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            await SafeAlertAsync("Внимание", "Выберите модель");
            return;
        }

        try
        {
            LoadingIndicator.IsRunning = true;
            DiagnoseButton.IsEnabled = false;
        }
        catch { }

        try
        {
            var primaryError = _currentErrors.FirstOrDefault()
                            ?? _pendingErrors.FirstOrDefault()
                            ?? _permanentErrors.FirstOrDefault();
            if (primaryError == null || string.IsNullOrWhiteSpace(primaryError.Code))
            {
                await SafeAlertAsync("Ошибка", "Нет кодов для диагностики");
                return;
            }
            var errorCode = primaryError.Code!;

            string errorSummary = "";
            try { errorSummary = BuildErrorSummary() ?? ""; } catch { }

            if (!_isTestMode)
            {
                StatusLabel.Text = "Чтение freeze frame...";
                try { _ = await EnsureBluetooth().ReadFreezeFrameAsync(errorCode); }
                catch { /* не критично */ }
            }

            // Жёстко фиксируем марку/модель из пикера — не из VIN/кеша
            brand = (pickerBrand.SelectedItem?.ToString() ?? brand).Trim();
            model = (pickerModel.SelectedItem?.ToString() ?? model).Trim();

            string? analyticsContext = null;
            try { analyticsContext = await BuildAnalyticsContextAsync(errorCode); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Diagnose] context: {ex.Message}");
            }

            // Контекст марки всегда в начале — AI не должен «перепутать» авто
            var brandGuard =
                $"ВАЖНО: автомобиль = {brand} {model}. " +
                $"Отвечай ТОЛЬКО для этой марки/модели. Не подставляй другие марки.\n";
            analyticsContext = brandGuard + (analyticsContext ?? "");
            if (!string.IsNullOrEmpty(errorSummary))
                analyticsContext += "\n\n" + errorSummary;

            string? result = null;
            bool isOffline = false;
            string? aiFailReason = null;

            // 1) Онлайн AI — всегда пробуем (и в тесте, и при «офлайн»-баннере:
            //    сервер Render может «просыпаться», а Connectivity иногда ложно-offline)
            StatusLabel.Text = "Отправка на AI...";
            try
            {
                result = await _api.Diagnose(errorCode, brand, model, analyticsContext);
                if (!string.IsNullOrWhiteSpace(result))
                {
                    // HTML-ошибка Render / Cloudflare — не считаем диагнозом
                    if (LooksLikeHttpErrorPage(result))
                    {
                        aiFailReason = "AI-сервер недоступен (приостановлен или 503).";
                        result = null;
                    }
                    // Защита: если ответ явно про другую марку — отбрасываем
                    else if (LooksLikeWrongBrand(result, brand))
                    {
                        System.Diagnostics.Debug.WriteLine("[Diagnose] AI response rejected: wrong brand");
                        aiFailReason = "AI вернул ответ для другой марки — отброшен.";
                        result = null;
                    }
                }
                else
                {
                    aiFailReason = "AI-сервер не ответил. Используется офлайн-база.";
                }
            }
            catch (Exception ex)
            {
                aiFailReason = $"AI недоступен: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"[Diagnose] api: {ex.Message}");
            }

            // 2) Офлайн-кеш / справочник — только своя марка
            if (string.IsNullOrWhiteSpace(result))
            {
                StatusLabel.Text = "Локальная диагностика...";
                try
                {
                    var cachedOffline = await _offlineCache.OfflineDiagnoseAsync(errorCode, brand, model);
                    if (cachedOffline != null)
                    {
                        result = cachedOffline.Value.Diagnosis;
                        StatusLabel.Text = cachedOffline.Value.SourceLabel;
                        isOffline = true;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Diagnose] offline: {ex.Message}");
                }
            }

            // 3) Гарантированный локальный ответ
            if (string.IsNullOrWhiteSpace(result))
            {
                result = OfflineCacheService.BuildSectionedDiagnosis(
                    errorCode, brand, model,
                    $"Код {errorCode}",
                    "Требуется диагностика по симптомам",
                    "",
                    "Проверьте связанные датчики и разъёмы. Сверьте live-данные.",
                    "OBD2");
                isOffline = true;
                StatusLabel.Text = "Локальный справочник";
            }

            // Всегда помечаем марку в тексте (ResultPage + кеш)
            var diagnosisText = EnsureBrandInDiagnosis(result!, brand, model, errorCode);
            if (isOffline && !string.IsNullOrWhiteSpace(aiFailReason))
            {
                diagnosisText =
                    $"⚠ {aiFailReason}\n" +
                    "Показана офлайн-диагностика (локальная база / кеш).\n" +
                    "Для полного AI: поднимите сервер car-diagnostic-ai на Render.\n\n" +
                    diagnosisText;
            }

            try { await _offlineCache.CacheDiagnosisAsync(errorCode, brand, model, diagnosisText); }
            catch { }

            if (!string.IsNullOrEmpty(_currentVin))
            {
                try
                {
                    var history = await _errorHistory.GetHistoryForVinAsync(_currentVin);
                    var match = history.FirstOrDefault(h =>
                        h.ErrorCode == errorCode && !h.Diagnosed);
                    if (match != null)
                        await _errorHistory.MarkDiagnosedAsync(match.Id, diagnosisText);
                }
                catch { }
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await App.Learning.RecordDiagnosisAsync(
                        errorCode, brand, model, diagnosisText);
                }
                catch { }
            });

            // На Android сложный ResultPage (XAML/стили/MaterialIcons) роняет процесс —
            // открываем лёгкий SimpleResultPage. На Windows — полный ResultPage.
            var b = brand;
            var m = model;
            var code = errorCode;
            var text = diagnosisText;
            try
            {
                await OpenDiagnosisResultAsync(text, code, b, m);
            }
            catch (Exception navEx)
            {
#if ANDROID
                Android.Util.Log.Error("AutoDiag", $"Nav ResultPage: {navEx}");
#endif
                await SafeAlertAsync("Результат диагностики",
                    text.Length > 800 ? text.Substring(0, 800) + "…" : text);
            }

            try
            {
                StatusLabel.Text = isOffline ? "Готово (офлайн)" : "Готово";
            }
            catch { }
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"OnDiagnoseClicked: {ex}");
#endif
            await SafeAlertAsync("Ошибка диагностики", $"{ex.GetType().Name}: {ex.Message}");
            try { StatusLabel.Text = "Ошибка диагностики"; } catch { }
        }
        finally
        {
            try
            {
                LoadingIndicator.IsRunning = false;
                DiagnoseButton.IsEnabled = true;
            }
            catch { }
        }
    }

    private async void OnHistoryClicked(object? sender, EventArgs e)
    {
        try { await SafeOpenPageAsync("История", () => new HistoryPage()); }
        catch (Exception ex) { await SafeAlertAsync("История", ex.Message); }
    }

    private async void OnLiveDataClicked(object? sender, EventArgs e)
    {
        try
        {
            if (_isTestMode)
            {
                await SafeOpenPageAsync("Живые данные", () => new StubPage("Живые данные"), modal: true);
                return;
            }
            await SafeOpenPageAsync("Живые данные", () => new LiveDataPage());
        }
        catch (Exception ex) { await SafeAlertAsync("Живые данные", ex.Message); }
    }

    private async void OnDashboardClicked(object? sender, EventArgs e)
    {
        try
        {
            // Всегда открываем реальный экран (не Stub). Без BT — страница покажет «нет связи».
            BluetoothService? bt = null;
            try { bt = _bt ?? EnsureBluetooth(); } catch { bt = _bt; }
            await SafeOpenPageAsync("Дашборд", () => new LiveChartsPage(bt), modal: true);
        }
        catch (Exception ex) { await SafeAlertAsync("Дашборд", ex.Message); }
    }

    private async void OnGraphClicked(object? sender, EventArgs e)
    {
        try
        {
            // Всегда открываем GraphPage — Stub скрывал реальные графики.
            // Без ELM327 страница откроется с оверлеем «нет данных».
            BluetoothService bt;
            try { bt = EnsureBluetooth(); }
            catch (Exception ex)
            {
                await SafeAlertAsync("Графики", "Bluetooth недоступен: " + ex.Message);
                return;
            }
            await SafeOpenPageAsync("Графики", () => new Pages.GraphPage(bt), modal: true);
        }
        catch (Exception ex) { await SafeAlertAsync("Графики", ex.Message); }
    }

    private async void OnKnowledgeClicked(object? sender, EventArgs e)
    {
        try { await SafeOpenPageAsync("База знаний", () => new KnowledgePage()); }
        catch (Exception ex) { await SafeAlertAsync("База знаний", ex.Message); }
    }

    private async void OnSchemeClicked(object? sender, EventArgs e)
    {
        try
        {
            var errorCode = _currentErrors.FirstOrDefault()?.Code
                         ?? _pendingErrors.FirstOrDefault()?.Code
                         ?? _permanentErrors.FirstOrDefault()?.Code;

            if (string.IsNullOrEmpty(errorCode))
            {
                await SafeAlertAsync("Схема узлов", "Сначала выполните сканирование ошибок");
                return;
            }

            var brand = pickerBrand.SelectedItem?.ToString() ?? "";
            var model = pickerModel.SelectedItem?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(brand) && _isTestMode) brand = TestBrandDefault;
            if (string.IsNullOrWhiteSpace(model) && _isTestMode) model = TestModelDefault;

            var b = brand; var m = model; var c = errorCode;
            // Android: лёгкая страница только с PNG. Windows: полная SchemePage.
#if ANDROID
            await SafeOpenPageAsync("Схема", () => new SimpleSchemePage(c, b, m));
#else
            await SafeOpenPageAsync("Схема", () => new SchemePage(c, b, m));
#endif
        }
        catch (Exception ex)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CarDiagnosticApp");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Scheme open fail: {ex}\n");
            }
            catch { }
            await SafeAlertAsync("Схема", $"Не удалось открыть схему: {ex.Message}");
        }
    }

    private async void OnAdminClicked(object? sender, EventArgs e)
    {
        try { await SafeOpenPageAsync("Админ", () => new AdminPanelPage()); }
        catch (Exception ex) { await SafeAlertAsync("Админ", ex.Message); }
    }

    private async void OnCodingClicked(object? sender, EventArgs e)
    {
        try
        {
            var brand = pickerBrand.SelectedItem?.ToString() ?? "";
            var model = pickerModel.SelectedItem?.ToString();
            await SafeOpenPageAsync("Кодирование", () => new CodingPage(brand, model));
        }
        catch (Exception ex) { await SafeAlertAsync("Кодирование", ex.Message); }
    }

    private async void OnManualUpdateClicked(object? sender, EventArgs e)
    {
        try
        {
            ManualUpdateButton.IsEnabled = false;
            ManualUpdateButton.Text = "⏳ Обновление...";
            try
            {
                var agent = CarDiagnosticApp.Agents.UpdateAgent.Instance;
                var result = await agent.ForceRunAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusLabel.Text = $"🔄 Обновление: {result}");
            }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusLabel.Text = $"❌ Ошибка: {ex.Message}");
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    ManualUpdateButton.IsEnabled = true;
                    ManualUpdateButton.Text = "🔄 Обновить вручную";
                });
            }
        }
        catch (Exception ex) { await SafeAlertAsync("Обновление", ex.Message); }
    }

    /// <summary>
    /// Облачная синхронизация (Этап 7).
    /// </summary>
    private async void OnSyncClicked(object? sender, EventArgs e)
    {
        try
        {
            SyncButton.IsEnabled = false;
            SyncButton.Text = "⏳ Синхронизация...";
            try
            {
                var syncAgent = new SyncAgent(new SyncService());
                var summary = await syncAgent.ForceSyncAsync();
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusLabel.Text = summary.SummaryText);
            }
            catch (Exception ex)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                    StatusLabel.Text = $"❌ Синхронизация: {ex.Message}");
            }
            finally
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    SyncButton.IsEnabled = true;
                    SyncButton.Text = "☁️ Синхронизация";
                });
            }
        }
        catch (Exception ex) { await SafeAlertAsync("Синхронизация", ex.Message); }
    }

    private async void OnAnalysisClicked(object? sender, EventArgs e)
    {
        // В тестовом режиме показываем анализ даже без VIN
        if (_isTestMode)
        {
            LoadingIndicator.IsRunning = true;
            LoadingIndicator.IsVisible = true;
            AnalysisButton.IsEnabled = false;

            try
            {
                var brand = pickerBrand.SelectedItem?.ToString() ?? "";
                var model = pickerModel.SelectedItem?.ToString() ?? "";

                string historyAnalysis;

                // Пытаемся получить реальную историю из БД
                if (!string.IsNullOrEmpty(_currentVin))
                    historyAnalysis = await _errorHistory.GetComprehensiveAnalysisAsync(_currentVin, brand, model);
                else
                    historyAnalysis = "VIN не определён.\n\n";

                // Если история пустая — генерируем тестовую
                if (historyAnalysis.Contains("Нет истории ошибок") || historyAnalysis.Contains("VIN не определён"))
                {
                    historyAnalysis = GenerateTestAnalysis(brand, model);
                }

                await OpenDiagnosisResultAsync(historyAnalysis, "ANALYSIS", brand, model);
            }
            catch (Exception ex)
            {
                await SafeAlertAsync("Ошибка", $"Не удалось выполнить анализ: {ex.Message}");
            }
            finally
            {
                LoadingIndicator.IsRunning = false;
                LoadingIndicator.IsVisible = false;
                AnalysisButton.IsEnabled = true;
            }
            return;
        }

        if (string.IsNullOrEmpty(_currentVin))
        {
            await SafeAlertAsync("Анализ истории", "VIN не определён. Сначала выполните сканирование.");
            return;
        }

        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;
        AnalysisButton.IsEnabled = false;

        try
        {
            var brand = pickerBrand.SelectedItem?.ToString() ?? "";
            var model = pickerModel.SelectedItem?.ToString() ?? "";

            var historyAnalysis = await _errorHistory.GetComprehensiveAnalysisAsync(_currentVin, brand, model);
            await OpenDiagnosisResultAsync(historyAnalysis, "ANALYSIS", brand, model);
        }
        catch (Exception ex)
        {
            await SafeAlertAsync("Ошибка", $"Не удалось выполнить анализ: {ex.Message}");
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            AnalysisButton.IsEnabled = true;
        }
    }

    private async void OnHistoryErrorsClicked(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_currentVin) && !_isTestMode)
        {
            await SafeAlertAsync("Сохранённые ошибки", "VIN не определён. Сначала выполните сканирование.");
            return;
        }

        LoadingIndicator.IsRunning = true;
        LoadingIndicator.IsVisible = true;
        HistoryErrorsButton.IsEnabled = false;

        try
        {
            var brand = pickerBrand.SelectedItem?.ToString() ?? "";
            var model = pickerModel.SelectedItem?.ToString() ?? "";

            if (_isTestMode)
            {
                var testVin = string.IsNullOrEmpty(_currentVin) ? TestVinVesta : _currentVin;
                var testBrand = string.IsNullOrWhiteSpace(brand) ? TestBrandDefault : brand;
                var testModel = string.IsNullOrWhiteSpace(model) ? TestModelDefault : model;
                var test = GenerateHistoryErrorsAnalysis(testBrand, testModel, testVin);
                await OpenDiagnosisResultAsync(test, "HISTORY_ERRORS", testBrand, testModel);
            }
            else
            {
                // Читаем все ошибки с ЭБУ через ELM327 (Mode 03 + Mode 07 + Mode 0A)
                StatusLabel.Text = "Чтение ошибок из памяти ЭБУ...";
                var allErrors = await EnsureBluetooth().ReadAllDTC();
                var current = allErrors.Where(e => e.Type == ObdErrorType.Current).ToList();
                var stored = allErrors.Where(e => e.Type == ObdErrorType.Pending).ToList();
                var permanent = allErrors.Where(e => e.Type == ObdErrorType.Permanent).ToList();

                if (allErrors.Count == 0)
                {
                    await DisplayAlert("Ошибки ЭБУ", "✅ Ошибок в памяти ЭБУ не обнаружено.", "OK");
                    return;
                }

                // Сохраняем свежепрочитанные ошибки в историю БД
                var scanSessionId = Guid.NewGuid().ToString("N")[..8];
                await _errorHistory.SaveErrorsAsync(_currentVin, brand, model, allErrors, scanSessionId);

                // Получаем полный анализ из БД истории + свежих данных
                var dbAnalysis = await _errorHistory.GetComprehensiveAnalysisAsync(_currentVin, brand, model);

                // Дополняем анализ сводкой свежепрочитанных ошибок
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"# Сводка ошибок из памяти ЭБУ\n");
                sb.AppendLine($"Автомобиль: {brand} {model}");
                sb.AppendLine($"VIN: {_currentVin}\n");
                sb.AppendLine($"## Текущие ошибки (Mode 03) — {current.Count} шт.:");
                foreach (var err in current) sb.AppendLine($"- {err.Code}");

                sb.AppendLine($"\n## Исторические / сохранённые (Mode 07) — {stored.Count} шт.:");
                foreach (var err in stored) sb.AppendLine($"- {err.Code}");

                sb.AppendLine($"\n## Подтверждённые (Mode 0A) — {permanent.Count} шт.:");
                foreach (var err in permanent) sb.AppendLine($"- {err.Code}");

                sb.AppendLine($"\n{dbAnalysis}");

                await OpenDiagnosisResultAsync(sb.ToString(), "HISTORY_ERRORS", brand, model);
            }
        }
        catch (Exception ex)
        {
            await SafeAlertAsync("Ошибка", $"Не удалось выполнить анализ: {ex.Message}");
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            HistoryErrorsButton.IsEnabled = true;
        }
    }

    private Task OpenDiagnosisResultAsync(string text, string code, string brand, string model)
    {
#if ANDROID
        return SafeOpenPageAsync("Результат", () => new SimpleResultPage(text, code, brand, model));
#else
        return SafeOpenPageAsync("Результат", () => new ResultPage(text, code, brand, model));
#endif
    }

    private string GenerateHistoryErrorsAnalysis(string brand, string model, string vin)
    {
        var car = string.IsNullOrEmpty(brand) ? "LADA Vesta" : $"{brand} {model}".Trim();
        var errors = _pendingErrors;
        var count = errors.Count;

        if (count == 0)
        {
            return $@"# Анализ сохранённых ошибок (тестовый режим)

 Автомобиль: {car}
 VIN: {vin}

 ✅ Сохранённых (исторических) ошибок не обнаружено.

 ---
 ⚡ Тестовый режим.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Анализ сохранённых ошибок\n");
        sb.AppendLine($"Автомобиль: {car}");
        sb.AppendLine($"VIN: {vin}\n");
        sb.AppendLine($"## Сохранённые ошибки (Mode 07) — {count} шт.:");

        // Даты для имитации хронологии
        var baseDate = DateTime.Now;
        var dates = new[]
        {
            baseDate.AddDays(-2),  // самая свежая
            baseDate.AddDays(-7),
            baseDate.AddDays(-15),
            baseDate.AddDays(-30),
            baseDate.AddDays(-45),
            baseDate.AddDays(-60),
            baseDate.AddDays(-90),
        };

        int i = 0;
        foreach (var err in errors)
        {
            var date = dates[i % dates.Length];
            var riskScore = (5 + (i * 2)) % 10 + 1;  // 6, 8, 10→1, 3, 5, 7, 9
            var riskLabel = riskScore >= 8 ? "ВЫСОКИЙ" : riskScore >= 5 ? "СРЕДНИЙ" : "НИЗКИЙ";
            var repeat = riskScore >= 6 ? "⚠ повторяющаяся" : "единичная";

            sb.AppendLine($"### {err.Code}");
            sb.AppendLine($"- Статус: историческая, сохранена в памяти ЭБУ");
            sb.AppendLine($"- Последнее появление: {date:dd.MM.yyyy}");
            sb.AppendLine($"- Риск: {riskScore}/10 ({riskLabel})");
            sb.AppendLine($"- Характер: {repeat}");

            if (riskScore >= 8)
                sb.AppendLine($"- ⚠ ВНИМАНИЕ: ошибка может указывать на развивающуюся неисправность");
            i++;
        }

        // Статистика
        var recurring = errors.Count / 2 + 1;
        var permanentCount = _permanentErrors.Count;
        var totalAll = _currentErrors.Count + _pendingErrors.Count + _permanentErrors.Count;
        var maxRisk = Math.Min(9, errors.Count > 0 ? 6 + (errors.Count % 4) : 0);

        sb.AppendLine($"\n## Статистика");
        sb.AppendLine($"- Всего сохранённых (Mode 07): {count}");
        sb.AppendLine($"- Из них повторяющихся: {recurring}/{count}");
        sb.AppendLine($"- Подтверждённых (Mode 0A): {permanentCount}");
        sb.AppendLine($"- Всего ошибок (03+07+0A): {totalAll}");
        sb.AppendLine($"- Максимальный риск: {maxRisk}/10");
        sb.AppendLine($"- Период: {baseDate.AddDays(-90):dd.MM.yyyy} — {baseDate:dd.MM.yyyy}");

        // Тренды
        sb.AppendLine($"\n## Тренд ошибок");
        foreach (var err in errors.Take(3))
        {
            var trend = err.Code switch
            {
                "P0134" => "📈 нарастающая (частота увеличивается)",
                "P0420" => "📉 снижающаяся (последнее появление — 30 дней назад)",
                _ => "➡ стабильная (единичные появления)"
            };
            sb.AppendLine($"- {err.Code}: {trend}");
        }

        // Связки
        if (errors.Count >= 2)
        {
            sb.AppendLine($"\n## Связки ошибок");
            sb.AppendLine($"- ⚠ P0134 + P0200: совместно 3 раза (доверие: 60%)");
            if (errors.Any(e => e.Code == "P0420"))
                sb.AppendLine($"- P0420 + P0171: совместно 1 раз (доверие: 33%)");
        }

        sb.AppendLine($"\n---");
        sb.AppendLine($"⚡ Тестовый режим. Реальные данные — после подключения ELM327.");

        return sb.ToString();
    }

    private async void OnClearClicked(object? sender, EventArgs e)
    {
        var count = _currentErrors.Count + _pendingErrors.Count + _permanentErrors.Count;
        if (count == 0)
        {
            await DisplayAlert("Сброс ошибок", "Нет ошибок для сброса", "OK");
            return;
        }

        // ── Тестовый режим: сброс без ELM327 ──
        if (_isTestMode)
        {
            var confirm = await DisplayAlert(
                "Сброс ошибок (тест)",
                $"Стереть {count} тестовых ошибок?\n\nТестовый режим — сброс имитируется.",
                "Сбросить", "Отмена");

            if (!confirm) return;

            LoadingIndicator.IsRunning = true;
            ClearButton.IsEnabled = false;

            try
            {
                StatusLabel.Text = "Сброс ошибок (тест)...";
                await Task.Delay(800);

                if (!string.IsNullOrEmpty(_currentVin))
                    await _errorHistory.MarkClearedAsync(_currentVin);

                ClearErrors();
                DiagnoseButton.IsEnabled = false;
                ClearButton.IsEnabled = false;
                StatusLabel.Text = "Статус: ошибки сброшены (тест)";
                await DisplayAlert("Готово", "Ошибки успешно сброшены (тестовый режим)", "OK");
            }
            catch (Exception ex)
            {
                await DisplayAlert("Ошибка", ex.Message, "OK");
            }
            finally
            {
                LoadingIndicator.IsRunning = false;
            }
            return;
        }

        // ── Реальный режим: сброс через ELM327 ──
        var confirmReal = await DisplayAlert(
            "Сброс ошибок",
            $"Вы уверены, что хотите стереть {count} ошибок из памяти ЭБУ?\n\nЭто погасит Check Engine. Если проблема не устранена — ошибка вернётся.",
            "Сбросить", "Отмена");

        if (!confirmReal) return;

        LoadingIndicator.IsRunning = true;
        ClearButton.IsEnabled = false;

        try
        {
            StatusLabel.Text = "Сброс ошибок (Mode 04)...";
            bool ok = await EnsureBluetooth().ClearDTCsAsync();

            if (ok)
            {
                // Фиксируем сброс в истории
                if (!string.IsNullOrEmpty(_currentVin))
                {
                    await _errorHistory.MarkClearedAsync(_currentVin);
                }

                ClearErrors();
                DiagnoseButton.IsEnabled = false;
                ClearButton.IsEnabled = false;
                StatusLabel.Text = "Статус: ошибки сброшены, CEL погашен";
                await DisplayAlert("Готово", "Ошибки успешно сброшены", "OK");
            }
            else
            {
                await DisplayAlert("Ошибка", "ЭБУ не подтвердил сброс (проверьте зажигание)", "OK");
                StatusLabel.Text = "Статус: ошибка сброса";
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
        finally
        {
            LoadingIndicator.IsRunning = false;
            ClearButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Строит аналитический контекст для AI на основе истории ошибок:
    /// повторяемость, связки, статистика сбросов и появлений.
    /// </summary>
    private string BuildErrorSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Сводка всех ошибок текущего сканирования ===");

        // Текущие (Mode 03)
        if (_currentErrors.Count > 0)
        {
            sb.AppendLine("\n🔴 Текущие ошибки (Mode 03) — активны, горят Check Engine:");
            foreach (var e in _currentErrors)
                sb.AppendLine($"   • {e.Code} [ТЕКУЩАЯ]");
        }
        else
        {
            sb.AppendLine("\n🔴 Текущие ошибки (Mode 03): отсутствуют");
        }

        // Исторические / сохранённые (Mode 07)
        if (_pendingErrors.Count > 0)
        {
            sb.AppendLine("\n🟡 Исторические ошибки (Mode 07) — сохранены в памяти ЭБУ:");
            foreach (var e in _pendingErrors)
                sb.AppendLine($"   • {e.Code} [ИСТОРИЧЕСКАЯ]");
        }
        else
        {
            sb.AppendLine("\n🟡 Исторические ошибки (Mode 07): отсутствуют");
        }

        // Подтверждённые (Mode 0A)
        if (_permanentErrors.Count > 0)
        {
            sb.AppendLine("\n🟠 Подтверждённые ошибки (Mode 0A) — зафиксированы, не сбрасываются:");
            foreach (var e in _permanentErrors)
                sb.AppendLine($"   • {e.Code} [ПОДТВЕРЖДЁННАЯ]");
        }
        else
        {
            sb.AppendLine("\n🟠 Подтверждённые ошибки (Mode 0A): отсутствуют");
        }

        sb.AppendLine($"\nВсего ошибок: {_currentErrors.Count + _pendingErrors.Count + _permanentErrors.Count}");
        sb.AppendLine("=== Конец сводки ===");

        return sb.ToString();
    }

    private async Task<string?> BuildAnalyticsContextAsync(string errorCode)
    {
        if (string.IsNullOrEmpty(_currentVin))
            return null;

        try
        {
            var history = await _errorHistory.GetHistoryForVinAsync(_currentVin);
            var match = history.FirstOrDefault(h => h.ErrorCode == errorCode);
            var bundles = await _errorHistory.DetectBundlesAsync(_currentVin, minConfidence: 0.6);

            var parts = new List<string>();

            // ── Статистика по конкретной ошибке ──
            if (match != null)
            {
                parts.Add($"Статистика ошибки {errorCode}:");
                parts.Add($"- Появлялась {match.AppearanceCount} раз(а)");
                parts.Add($"- Сбрасывали {match.ClearCount} раз(а)");
                parts.Add($"- Впервые обнаружена: {match.FirstSeenAt:dd.MM.yyyy}");
                parts.Add($"- Последний раз: {match.DetectedAt:dd.MM.yyyy HH:mm}");

                if (match.IsRecurring)
                    parts.Add("- ⚠ ОШИБКА ПОВТОРЯЮЩАЯСЯ — возвращается после сброса");
                else if (match.ClearCount > 0)
                    parts.Add("- После сброса пока не возвращалась");

                parts.Add($"- Риск: {match.RiskScore}/10 ({match.RiskLabel})");
            }

            // ── Связки с другими ошибками ──
            var relatedBundles = bundles
                .Where(b => b.CodeA == errorCode || b.CodeB == errorCode)
                .OrderByDescending(b => b.Strength)
                .Take(3)
                .ToList();

            if (relatedBundles.Count > 0)
            {
                parts.Add(string.Empty);
                parts.Add("Связки с другими ошибками (появляются вместе):");
                foreach (var b in relatedBundles)
                {
                    var otherCode = b.CodeA == errorCode ? b.CodeB : b.CodeA;
                    parts.Add($"- {otherCode}: сила связи {b.Strength:P0} (вместе {b.TogetherCount}× из {b.TogetherCount + b.OnlyACount + b.OnlyBCount} сканирований)");
                }
            }

            // ── Другие повторяющиеся ошибки на этом авто ──
            var otherRecurring = history
                .Where(h => h.ErrorCode != errorCode && h.IsRecurring)
                .ToList();

            if (otherRecurring.Count > 0)
            {
                parts.Add(string.Empty);
                parts.Add("Другие повторяющиеся ошибки на этом автомобиле:");
                foreach (var h in otherRecurring)
                    parts.Add($"- {h.ErrorCode}: появлялась {h.AppearanceCount}×, сбросов {h.ClearCount}×, риск {h.RiskScore}/10");
            }

            // ── Общий контекст ──
            var recurringCount = history.Count(h => h.IsRecurring);
            var maxRisk = history.Count > 0 ? history.Max(h => h.RiskScore) : 0;

            parts.Add(string.Empty);
            parts.Add($"Общая картина: {history.Count} записей в истории, {recurringCount} повторяющихся ошибок, максимальный риск {maxRisk}/10.");

            // ── Тренд (частота растёт/падает) ──
            try
            {
                var trend = await _errorHistory.GetHistoricalTrendAsync(_currentVin);
                if (!string.IsNullOrEmpty(trend) && !trend.StartsWith("Недостаточно данных"))
                {
                    parts.Add(string.Empty);
                    parts.Add(trend);
                }
            }
            catch { /* не блокируем диагностику */ }

            // ── Знания самообучения ──
            try
            {
                var enrichment = await App.Learning.BuildEnrichmentAsync(errorCode,
                    pickerBrand.SelectedItem?.ToString() ?? "",
                    pickerModel.SelectedItem?.ToString() ?? "");
                if (!string.IsNullOrWhiteSpace(enrichment))
                {
                    parts.Add(string.Empty);
                    parts.Add(enrichment);
                }
            }
            catch { /* не блокируем */ }

            return string.Join("\n", parts);
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════
    // ОФЛАЙН-РЕЖИМ И АВТООБНОВЛЕНИЕ
    // ═══════════════════════════════════════════════

    /// <summary>
    /// Реакция на изменение состояния сети (от нашего ConnectivityService).
    /// </summary>
    private void OnAppConnectivityChanged(bool isOnline)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { UpdateConnectivityIndicator(isOnline); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainPage] UI update fail: {ex.Message}"); }
        });

        if (isOnline)
        {
            _ = BackgroundSyncAsync();
        }
    }

    /// <summary>
    /// Принудительная проверка соединения при запуске страницы.
    /// </summary>
    private async Task RefreshConnectivityAsync()
    {
        // Ждём завершения первичной проверки в App (макс 20 секунд)
        for (int i = 0; i < 40; i++)
        {
            if (App.Connectivity.HasChecked) break;
            await Task.Delay(500);
        }

        // Если проверка ещё не завершена — показываем "проверяем..."
        if (!App.Connectivity.HasChecked)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ConnectivityLabel.Text = "Проверка...";
                ConnectivityLabel.TextColor = Color.FromArgb("#FF9800");
                ConnectivityDot.Color = Color.FromArgb("#FF9800");
            });
            // Не запускаем параллельную проверку — ждём фоновую
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateConnectivityIndicator(App.Connectivity.IsOnline);
        });
    }

    /// <summary>
    /// Обновляет индикатор онлайн/офлайн (точка + текст + баннер).
    /// </summary>
    private void UpdateConnectivityIndicator(bool isOnline)
    {
        ConnectivityDot.Color = isOnline ? Color.FromArgb("#4CAF50") : Color.FromArgb("#F44336");
        ConnectivityLabel.Text = isOnline ? "Онлайн" : "Офлайн";
        ConnectivityLabel.TextColor = isOnline ? Color.FromArgb("#4CAF50") : Color.FromArgb("#F44336");

        // Офлайн-баннер
        OfflineBanner.IsVisible = !isOnline;
    }

    /// <summary>
    /// Фоновая синхронизация: проверяет сводку сервера,
    /// предлагает пользователю обновиться, скачивает данные.
    /// </summary>
    private async Task BackgroundSyncAsync()
    {
        try
        {
            // Ждём, пока проверка интернета завершится
            for (int i = 0; i < 30; i++)
            {
                if (App.Connectivity.HasChecked) break;
                await Task.Delay(1000);
            }

            // Не синхронизируем в офлайне
            if (!App.Connectivity.IsOnline)
                return;

            // 1. Проверяем сводку (быстрый запрос, без скачивания)
            var (newCount, _) = await _sync.GetServerSummaryAsync();

            if (newCount <= 0)
                return;

            // 2. Есть новые данные — спрашиваем пользователя
            bool shouldSync = await DisplayAlert(
                "Доступны обновления",
                $"На сервере найдено {newCount} новых записей. Обновить данные?",
                "Обновить",
                "Позже");

            if (!shouldSync)
                return;

            // 3. Скачиваем и применяем
            var downloaded = await _sync.SyncAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                StatusLabel.Text = "✅ База знаний обновлена";
            });
        }
        catch
        {
            // Тихая ошибка — не трогаем индикатор сети
        }
    }
    /// <summary>
    /// Ленивая инициализация Bluetooth-сервиса.
    /// Не создаётся, пока не понадобится реальное подключение (вне тестового режима).
    /// </summary>
    private BluetoothService EnsureBluetooth()
    {
        if (_bt == null)
            _bt = IPlatformApplication.Current!.Services.GetRequiredService<BluetoothService>();
        return _bt;
    }

    // ─── Безопасная навигация (Shell: у ContentPage.Navigation часто null) ───

    private static INavigation? ResolveNavigation(Page? page = null)
    {
        try
        {
            if (page?.Navigation != null)
                return page.Navigation;
        }
        catch { }

        try
        {
            if (Shell.Current?.Navigation != null)
                return Shell.Current.Navigation;
        }
        catch { }

        try
        {
            var win = Application.Current?.Windows?.FirstOrDefault();
            var root = win?.Page;
            if (root?.Navigation != null)
                return root.Navigation;
        }
        catch { }

        return null;
    }

    private async Task SafeOpenPageAsync(string title, Func<Page> factory, bool modal = false)
    {
        try
        {
            Page page;
            try
            {
                page = factory();
            }
            catch (Exception createEx)
            {
#if ANDROID
                Android.Util.Log.Error("AutoDiag", $"Create {title}: {createEx}");
#endif
                await SafeAlertAsync(title, "Не удалось создать экран:\n" + createEx.Message);
                return;
            }

            if (modal)
                await SafePushModalAsync(page);
            else
                await SafePushAsync(page);
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"SafeOpen {title}: {ex}");
#endif
            await SafeAlertAsync(title, ex.Message);
        }
    }

    private async Task SafePushAsync(Page page)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var nav = ResolveNavigation(this);
                    if (nav == null)
                    {
                        await DisplayAlert("Навигация", "Не удалось открыть экран (нет Navigation).", "OK");
                        return;
                    }
                    await nav.PushAsync(page);
                }
                catch (Exception ex)
                {
#if ANDROID
                    Android.Util.Log.Error("AutoDiag", $"SafePushAsync: {ex}");
#endif
                    try { await DisplayAlert("Ошибка", ex.Message, "OK"); } catch { }
                }
            });
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"SafePushAsync outer: {ex}");
#endif
        }
    }

    private async Task SafePushModalAsync(Page page)
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try
                {
                    var nav = ResolveNavigation(this);
                    if (nav == null)
                    {
                        await SafePushAsync(page);
                        return;
                    }
                    await nav.PushModalAsync(page);
                }
                catch (Exception ex)
                {
#if ANDROID
                    Android.Util.Log.Error("AutoDiag", $"SafePushModalAsync: {ex}");
#endif
                    try { await SafePushAsync(page); } catch { }
                }
            });
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"SafePushModalAsync outer: {ex}");
#endif
        }
    }

    private async Task SafeGoToAsync(string route)
    {
        // Без Shell (Android NavigationPage) — открываем страницы напрямую
        try
        {
            var normalized = (route ?? "").Trim().Trim('/');
            if (normalized.StartsWith("//"))
                normalized = normalized.TrimStart('/');

            Page? page = normalized.ToLowerInvariant() switch
            {
                "history" => CreatePageSafe(() => new HistoryPage()),
                "knowledge" => CreatePageSafe(() => new KnowledgePage()),
                "livedata" => CreatePageSafe(() => new LiveDataPage()),
                "main" => null,
                _ => null
            };

            if (page != null)
            {
                await SafePushAsync(page);
                return;
            }

            if (Shell.Current != null)
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    try { await Shell.Current.GoToAsync(route); }
                    catch (Exception ex) { await DisplayAlert("Навигация", ex.Message, "OK"); }
                });
                return;
            }

            await SafeAlertAsync("Навигация", $"Маршрут {route} недоступен.");
        }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"SafeGoToAsync {route}: {ex}");
#endif
            await SafeAlertAsync("Навигация", ex.Message);
        }
    }

    private static Page? CreatePageSafe(Func<Page> factory)
    {
        try { return factory(); }
        catch (Exception ex)
        {
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"CreatePageSafe: {ex}");
#endif
            return new ContentPage
            {
                Title = "Ошибка",
                Content = new Label
                {
                    Text = "Не удалось открыть экран:\n" + ex.Message,
                    Margin = new Thickness(20),
                    TextColor = Colors.White
                },
                BackgroundColor = Color.FromArgb("#121212")
            };
        }
    }

    private async Task SafeAlertAsync(string title, string message, string cancel = "OK")
    {
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                try { await DisplayAlert(title, message, cancel); }
                catch { }
            });
        }
        catch { }
    }

    /// <summary>
    /// Генерирует тестовый анализ истории ошибок для демонстрации в тестовом режиме.
    /// </summary>
    private string GenerateTestAnalysis(string brand, string model)
    {
        var car = string.IsNullOrEmpty(brand) ? $"{TestBrandDefault} {TestModelDefault}" : $"{brand} {model}".Trim();
        var vin = string.IsNullOrEmpty(_currentVin) ? TestVinVesta : _currentVin;

        return $@"# Анализ истории ошибок (тестовый режим)

Автомобиль: {car}
VIN: {vin}
Всего записей: 7 (имитация)

## Топ-5 ошибок:
- P0134: 4×, впервые 15.06.2026, риск 7/10, повтор: ДА
- P0200: 2×, впервые 20.06.2026, риск 5/10, повтор: нет
- P0301: 1×, впервые 01.07.2026, риск 4/10, повтор: нет

## Статистика:
- Повторяющихся ошибок: 4/7
- Постоянных (Permanent): 2
- Средний риск: 5.3/10
- Максимальный риск: 8/10
- Период: 15.06.2026 — 10.07.2026 (25 дн.)

## Тренд ошибок:
📈 **P0134** (датчик O₂) — нарастающая: 15.06 → 25.06 → 01.07 → 10.07
📉 **P0200** (форсунка) — снижающаяся: 20.06 → 05.07
🔄 **P0301** (пропуск зажигания цилиндр 1) — единичная: 01.07

## Связки ошибок:
⚠️ P0134 + P0200: совместно 2 раза (доверие: 50%)

---
⚡ Это демонстрационный анализ в тестовом режиме.
При реальном подключении к ELM327 данные будут накапливаться
автоматически при каждом сканировании.";
    }
}
