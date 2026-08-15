using System.Collections.ObjectModel;
using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace CarDiagnosticApp.Pages;

public partial class GraphPage : ContentPage, IDisposable
{
    private LiveDataService? _liveData;
    private readonly BluetoothService _bt;
    private readonly ApiService _api;
    private GraphAnalysisService? _graphAnalysis;

    // Состояние
    private int _timeWindowSec = 60;
    private bool _isRunning;
    private int _tickCounter;
    private bool _disposed;

    // Серии графика (одна на каждый выбранный PID)
    private readonly ObservableCollection<ISeries> _series = new();
    private readonly Dictionary<string, LineSeries<DateTimePoint>> _pidSeries = new();
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _pidValues = new();

    // Кэш выбранных PID (все доступные, просто чипы в UI)
    private readonly HashSet<string> _selectedPids = new();

    // Цветовая палитра (10 цветов по кругу)
    private static readonly SKColor[] Palette =
    {
        new(33, 150, 243),  // синий
        new(244, 67, 54),   // красный
        new(76, 175, 80),   // зелёный
        new(255, 152, 0),   // оранжевый
        new(156, 39, 176),  // фиолетовый
        new(0, 188, 212),   // циан
        new(255, 87, 34),   // deep orange
        new(63, 81, 181),   // индиго
        new(205, 220, 57),  // лайм
        new(233, 30, 99),   // розовый
    };

    private int _colorIdx;

    public GraphPage(BluetoothService bt)
    {
        _bt = bt;
        _api = IPlatformApplication.Current!.Services.GetRequiredService<ApiService>();
        _graphAnalysis = new GraphAnalysisService(_api);

        try
        {
            InitializeComponent();
            MainChart.Series = _series;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphPage] XAML init: {ex}");
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Children =
                {
                    new Label
                    {
                        Text = "Не удалось открыть графики.\n" + ex.Message,
                        TextColor = Colors.White
                    }
                }
            };
            return;
        }

        // DateTimePoint требует DateTimeAxis
        try
        {
            MainChart.XAxes = new[]
            {
                new DateTimeAxis(TimeSpan.FromSeconds(5), date => date.ToString("HH:mm:ss"))
                {
                    Name = "Время",
                    ShowSeparatorLines = true,
                }
            };
            MainChart.YAxes = new[]
            {
                new Axis
                {
                    Name = "Значение",
                    ShowSeparatorLines = true,
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphPage] axis setup: {ex.Message}");
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        _liveData = new LiveDataService(_bt);
        if (_graphAnalysis != null)
            await _graphAnalysis.LoadReferenceDatabaseAsync();

        BuildPidChips();
        _liveData.OnValueUpdated += OnLiveValueUpdated;
        StartPolling();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPolling();
        _liveData?.OnValueUpdated -= OnLiveValueUpdated;
        _liveData = null;
    }

    // ═══════════════════════════════════════════════════
    //  Чипы выбора PID
    // ═══════════════════════════════════════════════════

    private void BuildPidChips()
    {
        PidChips.Children.Clear();
        var pids = LiveDataService.AvailablePids;

        foreach (var pid in pids)
        {
            var label = string.IsNullOrWhiteSpace(pid.Name) ? pid.PidHex : pid.Name;
            if (label.Length > 12) label = label[..12] + "..";

            var chip = new Button
            {
                Text = label,
                FontSize = 10,
                FontFamily = "InterSemiBold",
                Padding = new Thickness(8, 4),
                CornerRadius = 12,
                BackgroundColor = Color.FromArgb("#E0E0E0"),
                TextColor = Color.FromArgb("#616161"),
                CommandParameter = pid
            };
            chip.Clicked += OnPidChipClicked;
            PidChips.Children.Add(chip);
        }
    }

    private void OnPidChipClicked(object? sender, EventArgs e)
    {
        if (sender is not Button chip || chip.CommandParameter is not LiveDataPid pid)
            return;

        if (_selectedPids.Contains(pid.PidHex))
        {
            _selectedPids.Remove(pid.PidHex);
            RemoveSeries(pid);
            chip.BackgroundColor = Color.FromArgb("#E0E0E0");
            chip.TextColor = Color.FromArgb("#616161");
        }
        else
        {
            _selectedPids.Add(pid.PidHex);
            AddSeries(pid);
            chip.BackgroundColor = Color.FromArgb("#1976D2");
            chip.TextColor = Colors.White;
        }

        UpdateStatusBar();
        UpdateAiButtonState();
    }

    // ═══════════════════════════════════════════════════
    //  Управление сериями
    // ═══════════════════════════════════════════════════

    private void AddSeries(LiveDataPid pid)
    {
        if (_pidSeries.ContainsKey(pid.PidHex)) return;

        var values = new ObservableCollection<DateTimePoint>();
        _pidValues[pid.PidHex] = values;

        var color = Palette[_colorIdx % Palette.Length];
        _colorIdx++;

        var series = new LineSeries<DateTimePoint>
        {
            Values = values,
            Name = pid.Name,
            Stroke = new SolidColorPaint(color, 2.5f),
            GeometryStroke = new SolidColorPaint(color, 2),
            GeometryFill = new SolidColorPaint(color.WithAlpha(60)),
            GeometrySize = 4,
            Fill = null,
            LineSmoothness = 0.3,
            AnimationsSpeed = TimeSpan.FromMilliseconds(200),
        };

        _pidSeries[pid.PidHex] = series;
        _series.Add(series);
        NoDataOverlay.IsVisible = false;
    }

    private void RemoveSeries(LiveDataPid pid)
    {
        if (!_pidSeries.TryGetValue(pid.PidHex, out var series)) return;

        _series.Remove(series);
        _pidSeries.Remove(pid.PidHex);
        _pidValues.Remove(pid.PidHex);

        if (_series.Count == 0)
            NoDataOverlay.IsVisible = true;
    }

    // ═══════════════════════════════════════════════════
    //  Поток данных
    // ═══════════════════════════════════════════════════

    private void OnLiveValueUpdated(LiveDataPid pid, double value)
    {
        if (!_isRunning || _disposed) return;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_isRunning || _disposed) return;
            if (!_pidValues.TryGetValue(pid.PidHex, out var values)) return;

            try
            {
                var now = DateTime.Now;
                values.Add(new DateTimePoint(now, value));

                var cutoff = now.AddSeconds(-_timeWindowSec);
                while (values.Count > 0 && values[0].DateTime < cutoff)
                    values.RemoveAt(0);

                _tickCounter++;
                if (_tickCounter % 10 == 0)
                {
                    UpdateStatusBar();
                    UpdateAiButtonState();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GraphPage] UI update: {ex.Message}");
            }
        });
    }

    private void UpdateStatusBar()
    {
        LabelPidCount.Text = $"{_selectedPids.Count} параметров";
        LabelPointCount.Text = _pidValues.Values.Any()
            ? $"точек: {_pidValues.Values.Max(v => v.Count)}"
            : "";
    }

    private void UpdateAiButtonState()
    {
        // AI анализ доступен если есть данные и подключение
        BtnAiAnalyze.IsEnabled = _selectedPids.Count > 0 && _pidValues.Values.Any(v => v.Count >= 10);

        // Для Free показываем Pro-метку
        if (!AppSettings.IsAiAvailable)
        {
            BtnAiAnalyze.Text = "🤖 AI Анализ (Pro)";
            BtnAiAnalyze.BackgroundColor = Color.FromArgb("#9E9E9E");
        }
        else
        {
            BtnAiAnalyze.Text = "🤖 AI Анализ";
            BtnAiAnalyze.BackgroundColor = Color.FromArgb("#7C4DFF");
        }
    }

    // ═══════════════════════════════════════════════════
    //  Опрос
    // ═══════════════════════════════════════════════════

    private async void StartPolling()
    {
        if (_isRunning || _liveData == null) return;

        try
        {
            if (!_bt.IsConnected)
            {
                NoDataOverlay.IsVisible = true;
                LabelPidCount.Text = "ELM327 не подключён";
                return;
            }

            await _liveData.DetectSupportedPidsAsync();

            _isRunning = true;
            BtnPlayPause.Text = "⏸";
            BtnPlayPause.BackgroundColor = Color.FromArgb("#EF5350");

            await _liveData.StartPollingAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphPage] Start error: {ex.Message}");
            try
            {
                LabelPidCount.Text = "Ошибка опроса: " + ex.Message;
                NoDataOverlay.IsVisible = true;
            }
            catch { }
        }
    }

    private void StopPolling()
    {
        _isRunning = false;
        _liveData?.StopPolling();
        BtnPlayPause.Text = "▶";
        BtnPlayPause.BackgroundColor = Color.FromArgb("#4CAF50");
    }

    // ═══════════════════════════════════════════════════
    //  Кнопки
    // ═══════════════════════════════════════════════════

    private void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        if (_isRunning) StopPolling();
        else StartPolling();
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        foreach (var values in _pidValues.Values)
            values.Clear();

        UpdateStatusBar();
        UpdateAiButtonState();
        AiAnalysisPanel.IsVisible = false;
    }

    private void OnWindow30Clicked(object? sender, EventArgs e)
    {
        SetWindow(30);
        UpdateWindowButtons("30с");
    }

    private void OnWindow60Clicked(object? sender, EventArgs e)
    {
        SetWindow(60);
        UpdateWindowButtons("60с");
    }

    private void OnWindow120Clicked(object? sender, EventArgs e)
    {
        SetWindow(120);
        UpdateWindowButtons("120с");
    }

    private void SetWindow(int seconds)
    {
        _timeWindowSec = seconds;
        LabelWindow.Text = $"Окно: {seconds}с";
    }

    private void UpdateWindowButtons(string active)
    {
        Btn30.BackgroundColor = Color.FromArgb(active == "30с" ? "#1565C0" : "#E0E0E0");
        Btn30.TextColor = Color.FromArgb(active == "30с" ? "#FFFFFF" : "#424242");
        Btn60.BackgroundColor = Color.FromArgb(active == "60с" ? "#1565C0" : "#E0E0E0");
        Btn60.TextColor = Color.FromArgb(active == "60с" ? "#FFFFFF" : "#424242");
        Btn120.BackgroundColor = Color.FromArgb(active == "120с" ? "#1565C0" : "#E0E0E0");
        Btn120.TextColor = Color.FromArgb(active == "120с" ? "#FFFFFF" : "#424242");
    }

    private void OnFitClicked(object? sender, EventArgs e)
    {
        MainChart.CoreChart.Update();
    }

    // ═══════════════════════════════════════════════════
    //  AI Анализ графиков
    // ═══════════════════════════════════════════════════

    private async void OnAiAnalyzeClicked(object? sender, EventArgs e)
    {
        if (!AppSettings.IsAiAvailable)
        {
            await DisplayAlert(
                "Требуется Pro",
                "AI-анализ графиков доступен только в версии Pro.\n\n" +
                "Преимущества Pro:\n" +
                "• AI-диагностика ошибок\n" +
                "• AI-анализ графиков с эталоном\n" +
                "• Схемы с маркерами датчиков\n\n" +
                "Обновитесь до Pro для полного доступа.",
                "OK");
            return;
        }

        if (_graphAnalysis == null || _pidValues.Count == 0)
        {
            await DisplayAlert("Нет данных", "Сначала выберите параметры и соберите данные.", "OK");
            return;
        }

        BtnAiAnalyze.IsEnabled = false;
        BtnAiAnalyze.Text = "⏳ Анализ...";

        try
        {
            // Получаем средние значения по каждому PID
            var averages = new Dictionary<string, double>();
            foreach (var (pidHex, values) in _pidValues)
            {
                if (values.Count == 0) continue;
                var avg = values.Average(v => v.Value);
                averages[pidHex] = avg.GetValueOrDefault();
            }

            // Получаем марку/модель из MainPage (через App или Navigation)
            var (brand, model, vin) = GetCurrentVehicle();

            // Сравниваем с эталоном
            var deviations = _graphAnalysis.CompareWithReference(brand, model, averages);

            if (deviations.Count == 0)
            {
                await DisplayAlert("Результат", "✅ Все параметры в пределах нормы. Отклонений не обнаружено.", "OK");
                AiAnalysisPanel.IsVisible = false;
                return;
            }

            // Показываем отклонения
            ShowDeviations(deviations);

            // Отправляем на AI-анализ
            var aiResult = await _graphAnalysis.AnalyzeDeviationsWithAiAsync(brand, model, vin, deviations);

            if (aiResult != null)
            {
                ShowAiAnalysis(aiResult);
            }
            else
            {
                // Локальный анализ без AI
                AiAnalysisSummary.Text = $"Обнаружены отклонения в {deviations.Count} параметрах. " +
                    "AI-анализ недоступен (проверьте подключение к интернету).";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphPage] AI analyze: {ex}");
            await DisplayAlert("Ошибка", "Не удалось выполнить анализ: " + ex.Message, "OK");
        }
        finally
        {
            BtnAiAnalyze.IsEnabled = true;
            UpdateAiButtonState();
        }
    }

    private void ShowDeviations(List<PidDeviation> deviations)
    {
        // Заголовок с количеством отклонений
        var criticalCount = deviations.Count(d => d.Status == DeviationStatus.Critical);
        var warningCount = deviations.Count(d => d.Status == DeviationStatus.Warning);

        AiAnalysisHeader.Text = $"🤖 AI-анализ: {deviations.Count} отклонений " +
            $"(🔴 {criticalCount} ⚠️ {warningCount})";

        AiAnalysisPanel.IsVisible = true;
    }

    private void ShowAiAnalysis(GraphAiAnalysis analysis)
    {
        AiAnalysisSummary.Text = analysis.Summary;

        // Причины
        AiAnalysisCauses.Children.Clear();
        foreach (var cause in analysis.PossibleCauses.Take(5))
        {
            AiAnalysisCauses.Children.Add(new Label
            {
                Text = "• " + cause,
                FontSize = 12,
                TextColor = Color.FromArgb("#E65100"),
            });
        }

        // Рекомендации
        AiAnalysisRecommendations.Children.Clear();
        foreach (var rec in analysis.Recommendations.Take(5))
        {
            AiAnalysisRecommendations.Children.Add(new Label
            {
                Text = "• " + rec,
                FontSize = 12,
                TextColor = Color.FromArgb("#1565C0"),
            });
        }

        // Статус
        AiAnalysisSeverity.Text = "Критичность: " + analysis.Severity;
        AiAnalysisSeverity.TextColor = analysis.Severity switch
        {
            "КРИТИЧЕСКАЯ" => Color.FromArgb("#F44336"),
            "ВЫСОКАЯ" => Color.FromArgb("#FF5722"),
            "СРЕДНЯЯ" => Color.FromArgb("#FF9800"),
            _ => Color.FromArgb("#4CAF50"),
        };

        AiAnalysisCanDrive.Text = "Езда: " + analysis.CanDrive;
        AiAnalysisCanDrive.TextColor = analysis.CanDrive switch
        {
            "Нет" => Color.FromArgb("#F44336"),
            "Осторожно" => Color.FromArgb("#FF9800"),
            _ => Color.FromArgb("#4CAF50"),
        };
    }

    private void OnCloseAiPanelClicked(object? sender, EventArgs e)
    {
        AiAnalysisPanel.IsVisible = false;
    }

    /// <summary>
    /// Получает текущий автомобиль из MainPage (через WeakReference или Navigation).
    /// </summary>
    private (string brand, string model, string? vin) GetCurrentVehicle()
    {
        try
        {
            // Пробуем получить из App.Current или навигации
            if (App.Current?.MainPage is Shell shell)
            {
                // Ищем MainPage в навигации
                var mainPage = FindMainPage(shell);
                if (mainPage != null)
                {
                    // Получаем свойства через рефлексию (чтобы не ломать сборку при изменениях MainPage)
                    var brand = GetFieldValue(mainPage, "pickerBrand")?.GetType()
                        .GetProperty("SelectedItem")?.GetValue(GetFieldValue(mainPage, "pickerBrand"))?.ToString() ?? "";
                    var model = GetFieldValue(mainPage, "pickerModel")?.GetType()
                        .GetProperty("SelectedItem")?.GetValue(GetFieldValue(mainPage, "pickerModel"))?.ToString() ?? "";
                    var vin = GetFieldValue(mainPage, "_currentVin")?.ToString();

                    return (brand, model, vin);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphPage] GetCurrentVehicle: {ex.Message}");
        }

        return ("", "", null);
    }

    private object? GetFieldValue(object obj, string fieldName)
    {
        try
        {
            var field = obj.GetType().GetField(fieldName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(obj);
        }
        catch { return null; }
    }

    private object? FindMainPage(Shell shell)
    {
        try
        {
            // Ищем MainPage в стеке навигации
            foreach (var item in shell.Items)
            {
                foreach (var section in item.Items)
                {
                    foreach (var content in section.Items)
                    {
                        if (content.BindingContext?.GetType().Name.Contains("Main") == true)
                            return content.BindingContext;
                        // ShellContent.Content — это страница
                        var page = content.GetType().GetProperty("Content")?.GetValue(content) as Page;
                        if (page?.GetType().Name == "MainPage")
                            return page;
                    }
                }
            }

            // Fallback: ищем в NavigationStack
            if (shell.CurrentPage?.Navigation?.NavigationStack != null)
            {
                foreach (var page in shell.CurrentPage.Navigation.NavigationStack)
                {
                    if (page.GetType().Name == "MainPage")
                        return page;
                }
            }
        }
        catch { }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        _liveData?.OnValueUpdated -= OnLiveValueUpdated;
        _liveData = null;
    }
}
