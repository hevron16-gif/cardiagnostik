using System.Collections.ObjectModel;
using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.VisualElements;
using SkiaSharp;

namespace CarDiagnosticApp.Pages;

public partial class LiveChartsPage : ContentPage, IDisposable
{
    private LiveDataService? _liveData;
    private BluetoothService? _bt;

    // Состояние
    private bool _isRunning;
    private bool _disposed;
    private int _trendWindowSec = 30;
    private int _tickCounter;

    // ═══ Радар ═══
    private readonly ObservableCollection<ObservablePoint> _radarValues;
    private readonly PolarLineSeries<ObservablePoint> _radarSeries;

    // ═══ Линейные графики (тренд) ═══
    private readonly ObservableCollection<ISeries> _trendSeries = new();
    private readonly Dictionary<string, LineSeries<DateTimePoint>> _pidTrendSeries = new();
    private readonly Dictionary<string, ObservableCollection<DateTimePoint>> _pidTrendValues = new();
    private readonly HashSet<string> _visiblePids = new(); // PID, отображаемые на тренде
    private static readonly SKColor[] TrendPalette =
    {
        new(33, 150, 243), new(244, 67, 54), new(76, 175, 80),
        new(255, 152, 0), new(156, 39, 176), new(0, 188, 212),
        new(255, 87, 34), new(63, 81, 181), new(205, 220, 57), new(233, 30, 99),
    };
    private int _trendColorIdx;

    // ═══ Столбчатая диаграмма ═══
    private readonly ObservableCollection<ISeries> _columnSeries = new();
    private readonly ColumnSeries<double> _columnNormal;
    private readonly ColumnSeries<double> _columnWarning;
    private readonly ColumnSeries<double> _columnDanger;
    private ObservableCollection<double> _columnNormalVals = new();
    private ObservableCollection<double> _columnWarnVals = new();
    private ObservableCollection<double> _columnDangerVals = new();
    private readonly List<LiveDataPid> _columnPids = new(); // PID, отображаемые на столбцах
    private readonly Dictionary<string, double> _pidLastValue = new();
    private readonly Dictionary<string, (string State, int Severity)> _pidState = new();

    // ═══ Цифровые индикаторы ═══
    private readonly Dictionary<string, (Border Card, Label NameLabel, Label ValueLabel, Label UnitLabel)> _indicatorCards = new();

    // ═══ Круговая диаграмма ═══
    private readonly ObservableCollection<ISeries> _pieSeries = new();
    private readonly PieSeries<int> _pieNormal;
    private readonly PieSeries<int> _pieWarning;
    private readonly PieSeries<int> _pieDanger;
    private int _normalCount, _warningCount, _dangerCount;

    // ═══ Фиксированный набор PID для радара (основные датчики) ═══
    private static readonly string[] RadarPidHexes =
    {
        "0C", "0D", "05", "0B", "11", "0F", "10", "04"
    };

    private static readonly string[] RadarPidNames =
    {
        "RPM", "Скорость", "ОЖ", "MAP", "Дроссель",
        "Воздух", "MAF", "Нагрузка"
    };

    public LiveChartsPage(BluetoothService? bt = null)
    {
        InitializeComponent();
        _bt = bt;

        // Если BT не передан — страница в режиме просмотра (без live-данных)
        if (_bt == null || !_bt.IsConnected)
        {
            ShowDisconnectedState();
        }

        // ── Радар ──
        _radarValues = new ObservableCollection<ObservablePoint>();
        foreach (var name in RadarPidNames)
            _radarValues.Add(new ObservablePoint { X = Array.IndexOf(RadarPidNames, name), Y = 0 });

        _radarSeries = new PolarLineSeries<ObservablePoint>
        {
            Values = _radarValues,
            IsClosed = true,
            Stroke = new SolidColorPaint(new SKColor(33, 150, 243), 2.5f),
            Fill = new SolidColorPaint(new SKColor(33, 150, 243, 60)),
            GeometrySize = 6,
            GeometryFill = new SolidColorPaint(new SKColor(33, 150, 243)),
            GeometryStroke = new SolidColorPaint(new SKColor(255, 255, 255), 2),
            LineSmoothness = 0.3,
            AnimationsSpeed = TimeSpan.FromMilliseconds(350),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };

        RadarChart.Series = new ObservableCollection<ISeries> { _radarSeries };

        // Оси Polar/Cartesian задаём в C# (тип Axis не в assembly .Maui — XAML падает)
        try
        {
            RadarChart.AngleAxes = new[]
            {
                new PolarAxis
                {
                    MinStep = 1,
                    ForceStepToMin = true,
                    LabelsRotation = 0,
                    TextSize = 11,
                    ShowSeparatorLines = true,
                    Labeler = value =>
                    {
                        int idx = (int)Math.Round(value);
                        return idx >= 0 && idx < RadarPidNames.Length ? RadarPidNames[idx] : "";
                    }
                }
            };
            RadarChart.RadiusAxes = new[]
            {
                new PolarAxis
                {
                    MinLimit = 0,
                    MaxLimit = 100,
                    ShowSeparatorLines = true,
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LiveCharts] radar axis: {ex.Message}");
        }

        // ── Тренд ──
        TrendChart.Series = _trendSeries;
        try
        {
            TrendChart.XAxes = new[]
            {
                new DateTimeAxis(TimeSpan.FromSeconds(5), date => date.ToString("HH:mm:ss"))
                {
                    Name = "Время",
                    TextSize = 10,
                    ShowSeparatorLines = true,
                }
            };
            TrendChart.YAxes = new[]
            {
                new Axis
                {
                    Name = "Значение",
                    TextSize = 10,
                    ShowSeparatorLines = true,
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LiveCharts] trend axis: {ex.Message}");
        }

        try
        {
            ColumnChart.XAxes = new[]
            {
                new Axis
                {
                    Name = "Параметр",
                    TextSize = 9,
                    LabelsRotation = 45,
                    ShowSeparatorLines = false,
                }
            };
            ColumnChart.YAxes = new[]
            {
                new Axis
                {
                    Name = "Нормированное значение (%)",
                    MinLimit = 0,
                    MaxLimit = 100,
                    TextSize = 10,
                    ShowSeparatorLines = true,
                }
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LiveCharts] column axis: {ex.Message}");
        }

        // ── Круговая диаграмма ──
        _pieNormal = new PieSeries<int>
        {
            Values = new ObservableCollection<int> { 0 },
            Name = "Норма",
            Fill = new SolidColorPaint(new SKColor(76, 175, 80)),
            DataLabelsSize = 14,
            DataLabelsPaint = new SolidColorPaint(SKColors.White),
            DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer,
            InnerRadius = 55,
            HoverPushout = 8,
            AnimationsSpeed = TimeSpan.FromMilliseconds(400),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };
        _pieWarning = new PieSeries<int>
        {
            Values = new ObservableCollection<int> { 0 },
            Name = "Внимание",
            Fill = new SolidColorPaint(new SKColor(255, 152, 0)),
            DataLabelsSize = 14,
            DataLabelsPaint = new SolidColorPaint(SKColors.White),
            DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer,
            InnerRadius = 55,
            HoverPushout = 8,
            AnimationsSpeed = TimeSpan.FromMilliseconds(400),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };
        _pieDanger = new PieSeries<int>
        {
            Values = new ObservableCollection<int> { 0 },
            Name = "Опасно",
            Fill = new SolidColorPaint(new SKColor(244, 67, 54)),
            DataLabelsSize = 14,
            DataLabelsPaint = new SolidColorPaint(SKColors.White),
            DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer,
            InnerRadius = 55,
            HoverPushout = 8,
            AnimationsSpeed = TimeSpan.FromMilliseconds(400),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };

        _pieSeries.Add(_pieNormal);
        _pieSeries.Add(_pieWarning);
        _pieSeries.Add(_pieDanger);
        StatePieChart.Series = _pieSeries;

        // ── Столбчатая диаграмма ──
        var emptyDoubles = new ObservableCollection<double>();
        _columnNormal = new ColumnSeries<double>
        {
            Values = emptyDoubles,
            Name = "Норма",
            Fill = new SolidColorPaint(new SKColor(76, 175, 80)),
            Stroke = new SolidColorPaint(new SKColor(76, 175, 80), 1),
            MaxBarWidth = 28,
            Padding = 2,
            IgnoresBarPosition = true,
            AnimationsSpeed = TimeSpan.FromMilliseconds(350),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };
        _columnWarning = new ColumnSeries<double>
        {
            Values = new ObservableCollection<double>(),
            Name = "Внимание",
            Fill = new SolidColorPaint(new SKColor(255, 152, 0)),
            Stroke = new SolidColorPaint(new SKColor(255, 152, 0), 1),
            MaxBarWidth = 28,
            Padding = 2,
            IgnoresBarPosition = true,
            AnimationsSpeed = TimeSpan.FromMilliseconds(350),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };
        _columnDanger = new ColumnSeries<double>
        {
            Values = new ObservableCollection<double>(),
            Name = "Опасно",
            Fill = new SolidColorPaint(new SKColor(244, 67, 54)),
            Stroke = new SolidColorPaint(new SKColor(244, 67, 54), 1),
            MaxBarWidth = 28,
            Padding = 2,
            IgnoresBarPosition = true,
            AnimationsSpeed = TimeSpan.FromMilliseconds(350),
            EasingFunction = LiveChartsCore.EasingFunctions.CubicInOut,
        };
        _columnSeries.Add(_columnNormal);
        _columnSeries.Add(_columnWarning);
        _columnSeries.Add(_columnDanger);
        ColumnChart.Series = _columnSeries;
    }

    // ═══════════════════════════════════════════════════════
    //  Жизненный цикл
    // ═══════════════════════════════════════════════════════

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_bt == null || !_bt.IsConnected)
        {
            ShowDisconnectedState();
            return;
        }

        _liveData = new LiveDataService(_bt);
        _liveData.OnValueUpdated += OnLiveValueUpdated;

        try
        {
            await _liveData.DetectSupportedPidsAsync();
            InitTrendSeries();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LiveChartsPage] Init error: {ex.Message}");
        }

        StartPolling();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPolling();
        if (_liveData != null)
            _liveData.OnValueUpdated -= OnLiveValueUpdated;
        _liveData = null;
    }

    private void InitTrendSeries()
    {
        var allPids = LiveDataService.AvailablePids
            .Where(p => p.IsSupported)
            .ToList();

        // ── Линейные графики: создаём серии для всех PID ──
        foreach (var pid in allPids)
        {
            var values = new ObservableCollection<DateTimePoint>();
            _pidTrendValues[pid.PidHex] = values;

            var color = TrendPalette[_trendColorIdx % TrendPalette.Length];
            _trendColorIdx++;

            var series = new LineSeries<DateTimePoint>
            {
                Values = values,
                Name = pid.Name,
                Stroke = new SolidColorPaint(color, 2f),
                GeometrySize = 0,
                Fill = null,
                LineSmoothness = 0.3,
                AnimationsSpeed = TimeSpan.FromMilliseconds(120),
                EasingFunction = LiveChartsCore.EasingFunctions.Lineal,
                IsVisible = false,
            };

            _pidTrendSeries[pid.PidHex] = series;
            _trendSeries.Add(series);
        }

        // ── Строим чипсы выбора PID для линейных графиков ──
        BuildLinePidChips(allPids);

        // ── По умолчанию включаем первые 3 PID ──
        var defaults = allPids.Take(3).ToList();
        foreach (var pid in defaults)
            ToggleTrendPid(pid.PidHex, pid.Name);

        LabelPidCount.Text = allPids.Count.ToString();

        // ── Столбчатая диаграмма: инициализируем для ВСЕХ PID ──
        InitColumnChart(allPids);

        // ── Цифровые индикаторы ──
        BuildDigitalIndicators(allPids);
    }

    /// <summary>
    /// Строит чипсы-переключатели для выбора PID на линейном графике.
    /// </summary>
    private void BuildLinePidChips(List<LiveDataPid> pids)
    {
        LinePidChips.Children.Clear();
        foreach (var pid in pids)
        {
            var chip = new Button
            {
                Text = pid.Name,
                FontSize = 10,
                Padding = new Thickness(8, 3),
                CornerRadius = 10,
                Margin = new Thickness(0, 0, 6, 4),
                BackgroundColor = Color.FromArgb("#333333"),
                TextColor = Color.FromArgb("#999999"),
                BorderWidth = 1,
                BorderColor = Color.FromArgb("#444444"),
            };
            var pidHex = pid.PidHex;
            var pidName = pid.Name;
            chip.Clicked += (_, _) => ToggleTrendPid(pidHex, pidName);
            chip.ClassId = pidHex;
            LinePidChips.Children.Add(chip);
        }
    }

    /// <summary>
    /// Включает/выключает PID на линейном тренде и подсвечивает чипс.
    /// </summary>
    private void ToggleTrendPid(string pidHex, string pidName)
    {
        if (!_pidTrendSeries.TryGetValue(pidHex, out var series)) return;

        if (_visiblePids.Contains(pidHex))
        {
            _visiblePids.Remove(pidHex);
            series.IsVisible = false;
            UpdateChipStyle(pidHex, Color.FromArgb("#333333"), Color.FromArgb("#999999"), Color.FromArgb("#444444"));
        }
        else
        {
            _visiblePids.Add(pidHex);
            series.IsVisible = true;
            var colorIdx = Array.IndexOf(_pidTrendSeries.Keys.ToArray(), pidHex) % TrendPalette.Length;
            var color = TrendPalette[colorIdx];
            var mauiColor = Color.FromRgb(color.Red, color.Green, color.Blue);
            UpdateChipStyle(pidHex, mauiColor.WithAlpha(0.25f), mauiColor, mauiColor);
        }
    }

    private void UpdateChipStyle(string pidHex, Color bg, Color fg, Color border)
    {
        foreach (var child in LinePidChips.Children)
        {
            if (child is Button btn && btn.ClassId == pidHex)
            {
                btn.BackgroundColor = bg;
                btn.TextColor = fg;
                btn.BorderColor = border;
                break;
            }
        }
    }

    /// <summary>
    /// Инициализирует столбчатую диаграмму: одна колонка на PID, цвет по состоянию.
    /// </summary>
    private void InitColumnChart(List<LiveDataPid> pids)
    {
        _columnPids.Clear();
        _columnPids.AddRange(pids);

        _columnNormalVals = new ObservableCollection<double>();
        _columnWarnVals = new ObservableCollection<double>();
        _columnDangerVals = new ObservableCollection<double>();

        for (int i = 0; i < pids.Count; i++)
        {
            _columnNormalVals.Add(100);
            _columnWarnVals.Add(0);
            _columnDangerVals.Add(0);
        }

        _columnNormal.Values = _columnNormalVals;
        _columnWarning.Values = _columnWarnVals;
        _columnDanger.Values = _columnDangerVals;

        var xAxis = ColumnChart.XAxes?.FirstOrDefault() as Axis;
        if (xAxis != null)
        {
            xAxis.Labels = pids.ConvertAll(p => p.Name);
        }

        LabelColumnSubtitle.Text = $"Текущие значения {pids.Count} поддерживаемых PID";
    }

    /// <summary>
    /// Строит карточки цифровых индикаторов для всех поддерживаемых PID.
    /// </summary>
    private void BuildDigitalIndicators(List<LiveDataPid> pids)
    {
        DigitalIndicators.Children.Clear();
        _indicatorCards.Clear();

        foreach (var pid in pids)
        {
            var card = new Border
            {
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                Stroke = Color.FromArgb("#444444"),
                StrokeThickness = 1,
                BackgroundColor = Color.FromArgb("#2A2A2A"),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 0, 8, 8),
                WidthRequest = 120,
                HeightRequest = 76,
            };

            var stack = new VerticalStackLayout { Spacing = 2 };

            var nameLabel = new Label
            {
                Text = pid.Name,
                FontSize = 9,
                TextColor = Color.FromArgb("#888888"),
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation,
                MaximumWidthRequest = 110,
            };

            var valueLabel = new Label
            {
                Text = "—",
                FontFamily = "InterSemiBold",
                FontSize = 22,
                TextColor = Color.FromArgb("#FFFFFF"),
                HorizontalTextAlignment = TextAlignment.Center,
            };

            var unitLabel = new Label
            {
                Text = pid.Unit,
                FontSize = 9,
                TextColor = Color.FromArgb("#777777"),
                HorizontalTextAlignment = TextAlignment.Center,
            };

            stack.Children.Add(nameLabel);
            stack.Children.Add(valueLabel);
            stack.Children.Add(unitLabel);
            card.Content = stack;

            DigitalIndicators.Children.Add(card);
            _indicatorCards[pid.PidHex] = (card, nameLabel, valueLabel, unitLabel);
        }
    }

    /// <summary>
    /// Обновляет цифровой индикатор: значение, цвет фона по состоянию.
    /// </summary>
    private void UpdateDigitalIndicator(string pidHex, double value)
    {
        if (!_indicatorCards.TryGetValue(pidHex, out var card)) return;

        var (state, _) = ClassifyPidValue(pidHex, value);

        // Цвет текста значения
        var valueColor = state switch
        {
            "Опасно" => Color.FromArgb("#FF5252"),
            "Внимание" => Color.FromArgb("#FFB74D"),
            _ => Color.FromArgb("#FFFFFF"),
        };

        // Цвет фона карточки
        var bgColor = state switch
        {
            "Опасно" => Color.FromArgb("#3E1515"),
            "Внимание" => Color.FromArgb("#3E2E15"),
            _ => Color.FromArgb("#2A2A2A"),
        };

        // Цвет рамки
        var borderColor = state switch
        {
            "Опасно" => Color.FromArgb("#F44336"),
            "Внимание" => Color.FromArgb("#FF9800"),
            _ => Color.FromArgb("#444444"),
        };

        var oldText = card.ValueLabel.Text;
        var newText = FormatIndicatorValue(pidHex, value);

        card.ValueLabel.Text = newText;
        card.ValueLabel.TextColor = valueColor;
        card.Card.BackgroundColor = bgColor;
        card.Card.Stroke = borderColor;

        // Плавный пульс-эффект при изменении значения
        if (oldText != newText)
        {
            _ = PulseIndicatorAsync(card.ValueLabel);
        }
    }

    /// <summary>
    /// Кратковременный масштабный пульс на метке при изменении значения.
    /// </summary>
    private static async Task PulseIndicatorAsync(Label label)
    {
        try
        {
            await label.ScaleTo(1.25, 80, Easing.CubicOut);
            await label.ScaleTo(1.0, 120, Easing.CubicIn);
        }
        catch { /* ignore if label disposed */ }
    }

    /// <summary>
    /// Форматирует значение PID для цифрового индикатора.
    /// </summary>
    private static string FormatIndicatorValue(string pidHex, double value)
    {
        return pidHex switch
        {
            "0C" => $"{value:F0}",                      // RPM — целые
            "0D" => $"{value:F0}",                      // Скорость — целые
            "05" => $"{value:F0}",                      // ОЖ — целые
            "0B" => $"{value:F0}",                      // MAP — целые
            "11" => $"{value:F1}",                      // Дроссель — десятые
            "0F" => $"{value:F0}",                      // Воздух — целые
            "10" => $"{value:F1}",                      // MAF — десятые
            "04" => $"{value:F1}",                      // Нагрузка — десятые
            "06" => $"{value:F1}",                      // STFT — десятые
            "07" => $"{value:F1}",                      // LTFT — десятые
            "42" => $"{value:F2}",                      // Напряжение — сотые
            _ => $"{value:F1}",                          // По умолчанию — десятые
        };
    }

    // ═══════════════════════════════════════════════════════
    //  Состояние без ELM327
    // ═══════════════════════════════════════════════════════

    private void ShowDisconnectedState()
    {
        BtnPlayPause.IsEnabled = false;
        BtnClear.IsEnabled = false;
        LabelPidCount.Text = "0";
        LabelCycle.Text = "—";
        LabelPoints.Text = "🔌 Нет подключения";

        // Сбрасываем заголовок
        LabelHeader.Text = "Дашборд (нет данных)";

        // Также отключаем кнопки тренда
        BtnTrend30.IsEnabled = false;
        BtnTrend60.IsEnabled = false;
        BtnTrend120.IsEnabled = false;
    }

    // ═══════════════════════════════════════════════════════
    //  Поток данных
    // ═══════════════════════════════════════════════════════

    private void OnLiveValueUpdated(LiveDataPid pid, double value)
    {
        if (!_isRunning) return;

        var now = DateTime.Now;

        // ── Обновление радара (in-place для плавной анимации) ──
        var radarIdx = Array.IndexOf(RadarPidHexes, pid.PidHex);
        if (radarIdx >= 0 && _radarValues.Count > radarIdx)
        {
            var normalized = NormalizePidValue(pid.PidHex, value);
            var pt = _radarValues[radarIdx];
            pt.X = radarIdx;
            pt.Y = normalized;
        }

        // ── Обновление тренда ──
        if (_pidTrendValues.TryGetValue(pid.PidHex, out var trendValues))
        {
            trendValues.Add(new DateTimePoint(now, value));
            var cutoff = now.AddSeconds(-_trendWindowSec);
            while (trendValues.Count > 0 && trendValues[0].DateTime < cutoff)
                trendValues.RemoveAt(0);
        }

        // ── Обновление цифрового индикатора (каждый тик) ──
        MainThread.BeginInvokeOnMainThread(() => UpdateDigitalIndicator(pid.PidHex, value));

        // ── Обновление столбчатой диаграммы ──
        _pidLastValue[pid.PidHex] = value;
        _pidState[pid.PidHex] = ClassifyPidValue(pid.PidHex, value);
        if (_tickCounter % 3 == 0)
            MainThread.BeginInvokeOnMainThread(UpdateColumnChart);

        // ── Обновление круговой диаграммы ──
        UpdatePieCounts(pid, value);

        _tickCounter++;

        if (_tickCounter % 5 == 0)
            MainThread.BeginInvokeOnMainThread(UpdateStatusBar);
    }

    /// <summary>
    /// Нормализует значение PID в 0–100 для радара.
    /// </summary>
    private static double NormalizePidValue(string pidHex, double value)
    {
        return pidHex switch
        {
            "0C" => Math.Min(value / 6000.0 * 100.0, 100.0),   // RPM: 0–6000
            "0D" => Math.Min(value / 200.0 * 100.0, 100.0),    // Скорость: 0–200 км/ч
            "05" => Math.Min(value / 120.0 * 100.0, 100.0),    // ОЖ: 0–120°C
            "0B" => Math.Min(value / 255.0 * 100.0, 100.0),    // MAP: 0–255 kPa
            "11" => Math.Min(value / 100.0 * 100.0, 100.0),    // Дроссель: 0–100%
            "0F" => Math.Min(value / 120.0 * 100.0, 100.0),    // Воздух: 0–120°C
            "10" => Math.Min(value / 500.0 * 100.0, 100.0),    // MAF: 0–500 g/s
            "04" => Math.Min(value / 100.0 * 100.0, 100.0),    // Нагрузка: 0–100%
            _ => Math.Clamp(value, 0, 100),
        };
    }

    /// <summary>
    /// Определяет состояние датчика и обновляет счётчики для круговой диаграммы.
    /// </summary>
    private (string State, int Severity) ClassifyPidValue(string pidHex, double value)
    {
        return pidHex switch
        {
            "0C" => value switch { < 1000 => ("Норма", 0), < 3000 => ("Внимание", 1), _ => ("Опасно", 2) },
            "05" => value switch { >= 80 and <= 100 => ("Норма", 0), < 60 or > 105 => ("Опасно", 2), _ => ("Внимание", 1) },
            "0D" => value switch { <= 180 => ("Норма", 0), _ => ("Внимание", 1) },
            "11" => value switch { < 30 => ("Норма", 0), < 70 => ("Внимание", 1), _ => ("Опасно", 2) },
            "04" => value switch { < 75 => ("Норма", 0), < 90 => ("Внимание", 1), _ => ("Опасно", 2) },
            _ => ("Норма", 0),
        };
    }

    private int _lastPieUpdateTick;
    private readonly Dictionary<string, (string State, int Severity)> _pidLastState = new();

    private void UpdatePieCounts(LiveDataPid pid, double value)
    {
        var (state, severity) = ClassifyPidValue(pid.PidHex, value);

        if (_pidLastState.TryGetValue(pid.PidHex, out var prev) && prev.State == state)
            return;

        // Корректируем счётчики
        switch (prev.State)
        {
            case "Норма": _normalCount = Math.Max(0, _normalCount - 1); break;
            case "Внимание": _warningCount = Math.Max(0, _warningCount - 1); break;
            case "Опасно": _dangerCount = Math.Max(0, _dangerCount - 1); break;
        }
        switch (state)
        {
            case "Норма": _normalCount++; break;
            case "Внимание": _warningCount++; break;
            case "Опасно": _dangerCount++; break;
        }

        _pidLastState[pid.PidHex] = (state, severity);

        // Обновляем UI каждые ~20 тиков или при изменении
        if (_tickCounter - _lastPieUpdateTick > 20)
        {
            _lastPieUpdateTick = _tickCounter;
            MainThread.BeginInvokeOnMainThread(UpdatePieChart);
        }
    }

    /// <summary>
    /// Обновляет столбчатую диаграмму: перераспределяет высоты по рядам Норма/Внимание/Опасно.
    /// </summary>
    private void UpdateColumnChart()
    {
        for (int i = 0; i < _columnPids.Count; i++)
        {
            var pid = _columnPids[i];
            double normalized = 0;
            string state = "Норма";

            if (_pidLastValue.TryGetValue(pid.PidHex, out var rawValue) && !double.IsNaN(rawValue))
            {
                normalized = NormalizePidValue(pid.PidHex, rawValue);
                state = ClassifyPidValue(pid.PidHex, rawValue).State;
            }

            // Обновляем in-place — LiveCharts2 анимирует переход
            _columnNormalVals[i] = state == "Норма" ? normalized : 0;
            _columnWarnVals[i] = state == "Внимание" ? normalized : 0;
            _columnDangerVals[i] = state == "Опасно" ? normalized : 0;
        }
    }

    private void UpdatePieChart()
    {
        // Update in-place — LiveCharts2 анимирует переход
        if (_pieNormal.Values is ObservableCollection<int> nv && nv.Count > 0) nv[0] = _normalCount;
        if (_pieWarning.Values is ObservableCollection<int> wv && wv.Count > 0) wv[0] = _warningCount;
        if (_pieDanger.Values is ObservableCollection<int> dv && dv.Count > 0) dv[0] = _dangerCount;
    }

    private void UpdateStatusBar()
    {
        var maxPoints = _pidTrendValues.Values.Any()
            ? _pidTrendValues.Values.Max(v => v.Count)
            : 0;
        LabelPoints.Text = maxPoints.ToString();

        if (_liveData != null)
        {
            LabelCycle.Text = $"{_liveData.LastCycleMs}мс ({_liveData.RefreshRateHz:F1}Гц)";
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Опрос
    // ═══════════════════════════════════════════════════════

    private async void StartPolling()
    {
        if (_isRunning || _liveData == null) return;

        try
        {
            await _liveData.DetectSupportedPidsAsync();
            _isRunning = true;
            UpdatePlayPauseButton(false);
            await _liveData.StartPollingAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LiveChartsPage] Start error: {ex.Message}");
        }
    }

    private void StopPolling()
    {
        _isRunning = false;
        _liveData?.StopPolling();
        UpdatePlayPauseButton(true);
    }

    private void UpdatePlayPauseButton(bool isPaused)
    {
        if (isPaused)
        {
            BtnPlayPause.Text = "▶";
            BtnPlayPause.BackgroundColor = Color.FromArgb("#4CAF50");
        }
        else
        {
            BtnPlayPause.Text = "⏸";
            BtnPlayPause.BackgroundColor = Color.FromArgb("#EF5350");
        }
    }

    // ═══════════════════════════════════════════════════════
    //  Кнопки
    // ═══════════════════════════════════════════════════════

    private void OnPlayPauseClicked(object? sender, EventArgs e)
    {
        if (_bt == null || !_bt.IsConnected)
        {
            ShowDisconnectedState();
            return;
        }
        if (_isRunning) StopPolling();
        else StartPolling();
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        foreach (var values in _pidTrendValues.Values)
            values.Clear();

        _pidLastValue.Clear();
        _pidState.Clear();

        _normalCount = _warningCount = _dangerCount = 0;
        _pidLastState.Clear();
        UpdatePieChart();
        UpdateColumnChart();
        UpdateStatusBar();
        ResetDigitalIndicators();
    }

    private void ResetDigitalIndicators()
    {
        foreach (var (pidHex, (card, _, valueLabel, _)) in _indicatorCards)
        {
            valueLabel.Text = "—";
            valueLabel.TextColor = Color.FromArgb("#FFFFFF");
            card.BackgroundColor = Color.FromArgb("#2A2A2A");
            card.Stroke = Color.FromArgb("#444444");
        }
    }

    private void OnTrendWindowClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;

        _trendWindowSec = btn.Text switch
        {
            "30с" => 30,
            "60с" => 60,
            "120с" => 120,
            _ => _trendWindowSec
        };

        BtnTrend30.BackgroundColor = Color.FromArgb(_trendWindowSec == 30 ? "#1565C0" : "#333333");
        BtnTrend30.TextColor = Color.FromArgb(_trendWindowSec == 30 ? "#FFFFFF" : "#999999");
        BtnTrend60.BackgroundColor = Color.FromArgb(_trendWindowSec == 60 ? "#1565C0" : "#333333");
        BtnTrend60.TextColor = Color.FromArgb(_trendWindowSec == 60 ? "#FFFFFF" : "#999999");
        BtnTrend120.BackgroundColor = Color.FromArgb(_trendWindowSec == 120 ? "#1565C0" : "#333333");
        BtnTrend120.TextColor = Color.FromArgb(_trendWindowSec == 120 ? "#FFFFFF" : "#999999");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPolling();
        if (_liveData != null)
            _liveData.OnValueUpdated -= OnLiveValueUpdated;
        _liveData = null;
    }
}
