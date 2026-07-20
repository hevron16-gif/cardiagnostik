using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using Microsoft.Maui.Controls.Shapes;
using System.Linq;

namespace CarDiagnosticApp.Pages;

public partial class LiveDataPage : ContentPage
{
    private BluetoothService? _bt;
    private LiveDataService? _liveData;

    // Карта: PID → (Border, Label-значение, Label-имя, Label-мин, Label-макс)
    private readonly Dictionary<string,
        (Border Card, Label ValueLabel, Label NameLabel, Label MinLabel, Label MaxLabel)>
        _gaugeCards = new();

    // Трекинг min/max значений
    private readonly Dictionary<string, (double Min, double Max)> _extremes = new();

    // Уровень опасности: 0 = норма, 1 = внимание, 2 = опасно
    private readonly Dictionary<string, int> _pidSeverity = new();

    // Цвета индикации
    private static readonly Color GreenOk    = Color.FromArgb("#4CAF50");
    private static readonly Color YellowWarn = Color.FromArgb("#FF9800");
    private static readonly Color RedDanger  = Color.FromArgb("#F44336");
    private static readonly Color BlueCold   = Color.FromArgb("#2196F3");
    private static readonly Color GrayNA     = Color.FromArgb("#9E9E9E");

    private static readonly Color PrimaryColor = Color.FromArgb("#1565C0");
    // НЕ читать Resources в static-полях — при первом обращении к типу
    // Application.Current может быть null / ключ отсутствовать → краш Android.
    private static Color SurfaceColor => ResolveColor("Surface", Color.FromArgb("#1E1E1E"));
    private static Color OnSurface => ResolveColor("OnSurface", Color.FromArgb("#E0E0E0"));
    private static Color SubText => ResolveColor("Gray500", Colors.Gray);

    private static Color ResolveColor(string key, Color fallback)
    {
        try
        {
            var res = Application.Current?.Resources;
            if (res != null && res.TryGetValue(key, out var v) && v is Color c)
                return c;
        }
        catch { }
        return fallback;
    }

    public LiveDataPage()
    {
        InitializeComponent();
        BuildGauges();
    }

    /// <summary>
    /// Строит карточки для каждого PID.
    /// </summary>
    private void BuildGauges()
    {
        foreach (var pid in LiveDataService.AvailablePids)
        {
            _extremes[pid.PidHex] = (double.PositiveInfinity, double.NegativeInfinity);
            _pidSeverity[pid.PidHex] = 0;

            var card = CreateGaugeCard(pid);
            GaugesLayout.Children.Add(card);

            var stack = (VerticalStackLayout)((Border)card).Content;
            _gaugeCards[pid.PidHex] = (
                (Border)card,
                (Label)stack.Children[0],   // value
                (Label)stack.Children[2],   // name (index 2, after min/max row)
                (Label)stack.Children[1],   // min/max row (HorizontalStackLayout) — обновим агрегатно
                null!                        // не используется отдельно
            );
        }
    }

    /// <summary>
    /// Создаёт карточку-индикатор для одного PID.
    /// </summary>
    private static View CreateGaugeCard(LiveDataPid pid)
    {
        var isDark = Application.Current!.RequestedTheme == AppTheme.Dark;

        var card = new Border
        {
            WidthRequest = 140,
            HeightRequest = 90,
            Margin = new Thickness(4),
            Padding = new Thickness(10, 6),
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            StrokeThickness = 1.5,
            Stroke = isDark ? Color.FromArgb("#333") : Color.FromArgb("#E0E0E0"),
            BackgroundColor = SurfaceColor,
        };

        var stack = new VerticalStackLayout { Spacing = 1, VerticalOptions = LayoutOptions.Center };

        // Значение
        var valueLabel = new Label
        {
            Text = "—",
            FontFamily = "InterSemiBold",
            FontSize = 22,
            TextColor = OnSurface,
            HorizontalOptions = LayoutOptions.Center,
        };

        // Строка min / max
        var minMaxRow = new HorizontalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Center,
        };
        var minLabel = new Label
        {
            Text = "",
            FontFamily = "InterRegular",
            FontSize = 9,
            TextColor = GreenOk,
        };
        var maxLabel = new Label
        {
            Text = "",
            FontFamily = "InterRegular",
            FontSize = 9,
            TextColor = RedDanger,
        };
        // Сохраним ссылки в Tag для обновления
        minLabel.ClassId = "min";
        maxLabel.ClassId = "max";
        minMaxRow.Add(minLabel);
        minMaxRow.Add(maxLabel);

        // Имя
        var nameLabel = new Label
        {
            Text = pid.Name,
            FontFamily = "InterRegular",
            FontSize = 10,
            TextColor = SubText,
            HorizontalTextAlignment = TextAlignment.Center,
            MaxLines = 2,
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        stack.Children.Add(valueLabel);
        stack.Children.Add(minMaxRow);
        stack.Children.Add(nameLabel);

        card.Content = stack;
        return card;
    }

    // ─── Подключение / отключение ────────────────────────────

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        try
        {
            BtnConnect.IsEnabled = false;
            LabelStatus.Text = "Поиск устройств...";

            _bt = IPlatformApplication.Current!.Services.GetRequiredService<BluetoothService>();
            var name = await _bt.ConnectAsync();
            _deviceName = name;

            LabelStatus.Text = $"Подключено: {name}";
            BtnConnect.IsVisible = false;
            BtnStop.IsVisible = true;
            BtnAi.IsVisible = true;
            BtnGraph.IsVisible = true;
            BtnDashboard.IsVisible = true;
            LegendBar.IsVisible = true;
            StatusDot.Color = GrayNA;
            LabelWarnings.Text = "";
            LabelHeader.Text = "Live Data ▶";

            // Сбрасываем min/max при новом подключении
            foreach (var key in _extremes.Keys.ToArray())
                _extremes[key] = (double.PositiveInfinity, double.NegativeInfinity);
            foreach (var (card, valueLabel, _, minLabel, maxLabel) in _gaugeCards.Values)
            {
                minLabel.Text = "";
                maxLabel.Text = "";
                valueLabel.Text = "—";
                card.Stroke = Color.FromArgb("#E0E0E0");
                card.Opacity = 1.0;
            }

            // Создаём сервис живых данных
            _liveData = new LiveDataService(_bt);
            _liveData.OnValueUpdated += OnPidUpdated;
            _liveData.OnError += OnLiveDataError;
            _liveData.OnPidSupportProgress += OnPidDetectProgress;
            _liveData.OnCycleCompleted += OnCycleCompleted;

            // ── Автоопределение поддерживаемых PID ──
            LabelStatus.Text = "Определение PID...";
            var supportedCount = await _liveData.DetectSupportedPidsAsync();

            // Помечаем неподдерживаемые карточки серым
            foreach (var pid in LiveDataService.AvailablePids)
            {
                if (_gaugeCards.TryGetValue(pid.PidHex, out var card))
                {
                    if (!pid.IsSupported)
                    {
                        card.Card.Opacity = 0.35;
                        card.Card.Stroke = Color.FromArgb("#555");
                        card.ValueLabel.Text = "—";
                        card.ValueLabel.TextColor = Color.FromArgb("#666");
                    }
                }
            }

            LabelStatus.Text = $"Подключено: {name}  |  PID: {supportedCount} — запуск опроса...";
            LabelHeader.Text = "Live Data ▶";

            // Запускаем опрос живых данных (только поддерживаемые PID)
            _ = _liveData.StartPollingAsync(targetCycleMs: 500);
        }
        catch (Exception ex)
        {
            LabelStatus.Text = $"Ошибка: {ex.Message}";
            BtnConnect.IsEnabled = true;
        }
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
        _liveData?.StopPolling();
        _ = _bt?.DisconnectAsync();

        LabelStatus.Text = "Отключено";
        LabelHeader.Text = "Live Data";
        LegendBar.IsVisible = false;
        BtnAi.IsVisible = false;
        BtnGraph.IsVisible = false;
        BtnDashboard.IsVisible = false;
        StatusDot.Color = GrayNA;
        BtnConnect.IsVisible = true;
        BtnStop.IsVisible = false;
        BtnConnect.IsEnabled = true;
    }

    // ─── Обновление значений ────────────────────────────────

    private void OnPidUpdated(LiveDataPid pid, double value)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_gaugeCards.TryGetValue(pid.PidHex, out var card))
                return;

            if (double.IsNaN(value))
            {
                card.ValueLabel.Text = "—";
                card.ValueLabel.TextColor = SubText;
                card.Card.Stroke = Color.FromArgb("#E0E0E0");
                _pidSeverity[pid.PidHex] = 0;
                return;
            }

            // Обновляем min/max
            if (_extremes.TryGetValue(pid.PidHex, out var ext))
            {
                var newMin = double.Min(value, ext.Min);
                var newMax = double.Max(value, ext.Max);
                _extremes[pid.PidHex] = (newMin, newMax);

                // Обновляем min/max метки в строке
                var stack = (VerticalStackLayout)card.Card.Content;
                if (stack.Children[1] is HorizontalStackLayout minMaxRow)
                {
                    foreach (var child in minMaxRow.Children)
                    {
                        if (child is Label lbl)
                        {
                            if (lbl.ClassId == "min")
                                lbl.Text = double.IsInfinity(newMin) || double.IsNaN(newMin)
                                    ? "" : $"▼{FormatCompact(pid, newMin)}";
                            else if (lbl.ClassId == "max")
                                lbl.Text = double.IsInfinity(newMax) || double.IsNaN(newMax)
                                    ? "" : $"▲{FormatCompact(pid, newMax)}";
                        }
                    }
                }
            }

            card.ValueLabel.Text = string.Format(pid.DisplayFormat, value);

            // Цветовое кодирование
            var severity = GetSeverity(pid, value);
            _pidSeverity[pid.PidHex] = severity;
            card.ValueLabel.TextColor = GetValueColor(pid, value);

            // Нормализованная яркость рамки
            if (pid.Min < pid.Max)
            {
                var ratio = Math.Clamp((value - pid.Min) / (pid.Max - pid.Min), 0, 1);
                card.Card.Stroke = ColorFromRatio(ratio, pid, severity);
            }

            UpdateGlobalStatus();
        });
    }

    private void OnLiveDataError(string error)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LabelStatus.Text = error;
        });
    }

    /// <summary>
    /// Обновляет статус в процессе определения PID.
    /// </summary>
    private void OnPidDetectProgress(int step, int total, string message)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LabelStatus.Text = $"Определение PID ({step}/{total}): {message}";
        });
    }

    /// <summary>
    /// Обновляет статус после каждого полного цикла опроса.
    /// </summary>
    private string? _deviceName;
    private void OnCycleCompleted(int pidCount, double elapsedMs)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var hz = elapsedMs > 0 ? 1000.0 / elapsedMs : 0;
            LabelStatus.Text =
                $"{_deviceName}  |  PID: {pidCount}  |  цикл: {elapsedMs:0} мс  ({hz:0.0} Гц)";
        });
    }

    // ─── Цветовое кодирование ──────────────────────────────

    /// <summary>
    /// Компактный формат для min/max: без единиц измерения, округлённо.
    /// </summary>
    private static string FormatCompact(LiveDataPid pid, double value)
    {
        var hex = pid.PidHex;
        // 2-байтовые с дробной частью
        if (hex is "10" or "14" or "15" or "42" or "24" or "71" or "5E" or "34")
            return $"{value:0.0}";
        // Топливная коррекция (со знаком)
        if (hex is "06" or "07" or "08" or "09" or "2D")
            return $"{value:+0.0;-0.0}";
        // Время (крупные числа — без дробей)
        if (hex is "1F" or "7F")
            return $"{value:0}";
        return $"{value:0.#}";
    }

    private static Color GetValueColor(LiveDataPid pid, double value)
    {
        var hex = pid.PidHex;
        // Обороты
        if (hex == "0C")
        {
            if (value < 1000) return GreenOk;
            if (value < 3000) return OnSurface;
            if (value < 5000) return YellowWarn;
            return RedDanger;
        }
        // Температура ОЖ
        if (hex == "05")
        {
            if (value < 60) return BlueCold;
            if (value < 90) return GreenOk;
            if (value < 105) return YellowWarn;
            return RedDanger;
        }
        // Топливная коррекция
        if (hex is "06" or "07" or "08" or "09")
        {
            if (Math.Abs(value) < 5) return GreenOk;
            if (Math.Abs(value) < 10) return YellowWarn;
            return RedDanger;
        }
        // Дроссель
        if (hex is "11" or "45" or "5A")
        {
            if (value < 5) return BlueCold;
            if (value < 30) return GreenOk;
            if (value < 70) return YellowWarn;
            return RedDanger;
        }
        // Напряжение
        if (hex == "42")
        {
            if (value < 11.5 || value > 15) return RedDanger;
            if (value < 12.5 || value > 14.5) return YellowWarn;
            return GreenOk;
        }
        // O₂ датчики
        if (hex is "14" or "15")
        {
            if (value < 0.1 || value > 1.0) return RedDanger;
            return GreenOk;
        }
        // Давление масла
        if (hex == "B0")
        {
            if (value < 50) return RedDanger;
            if (value < 100) return YellowWarn;
            return GreenOk;
        }
        // Универсальное правило: цвет по положению в диапазоне
        if (pid.Min < pid.Max)
        {
            var ratio = Math.Clamp((value - pid.Min) / (pid.Max - pid.Min), 0, 1);
            return ratio switch
            {
                < 0.3 => GreenOk,
                < 0.7 => YellowWarn,
                _     => RedDanger
            };
        }
        return OnSurface;
    }

    /// <summary>
    /// Уровень опасности 0/1/2 для сводного индикатора.
    /// </summary>
    private static int GetSeverity(LiveDataPid pid, double value)
    {
        var hex = pid.PidHex;

        if (hex == "0C") // RPM
        {
            if (value < 1000) return 0;
            if (value < 5000) return 1;
            return 2;
        }
        if (hex == "05") // ОЖ
        {
            if (value is >= 60 and < 105) return 0;
            if (value >= 40 && value < 60) return 1;
            return 2;
        }
        if (hex is "06" or "07") // STFT/LTFT
        {
            if (Math.Abs(value) < 5) return 0;
            if (Math.Abs(value) < 10) return 1;
            return 2;
        }
        if (hex == "42") // Напряжение
        {
            if (value is >= 12.5 and <= 14.5) return 0;
            if (value is >= 11.5 and <= 15) return 1;
            return 2;
        }
        if (hex == "B0") // Давление масла
        {
            if (value >= 100) return 0;
            if (value >= 50) return 1;
            return 2;
        }
        // Универсальное
        if (pid.Min < pid.Max)
        {
            var ratio = Math.Clamp((value - pid.Min) / (pid.Max - pid.Min), 0, 1);
            if (ratio < 0.3) return 0;
            if (ratio < 0.7) return 1;
            return 2;
        }
        return 0;
    }

    /// <summary>
    /// Обновляет глобальный индикатор и счётчик проблем.
    /// </summary>
    private void UpdateGlobalStatus()
    {
        var warnings = _pidSeverity.Count(kv => kv.Value == 1);
        var dangers  = _pidSeverity.Count(kv => kv.Value == 2);

        if (dangers > 0)
        {
            StatusDot.Color = RedDanger;
            LabelWarnings.Text = $"⚠ {dangers}";
            LabelWarnings.TextColor = RedDanger;
        }
        else if (warnings > 0)
        {
            StatusDot.Color = YellowWarn;
            LabelWarnings.Text = $"⚡ {warnings}";
            LabelWarnings.TextColor = YellowWarn;
        }
        else
        {
            StatusDot.Color = GreenOk;
            LabelWarnings.Text = "";
        }
    }

    private static Color ColorFromRatio(double ratio, LiveDataPid pid, int severity)
    {
        var hex = pid.PidHex;
        // Температура и RPM: высокие значения — тревожные
        if (hex is "05" or "0C" or "5C")
        {
            if (ratio < 0.3) return GreenOk;
            if (ratio < 0.6) return YellowWarn;
            return RedDanger;
        }
        // Универсальная рамка по severity
        return severity switch
        {
            2 => RedDanger,
            1 => YellowWarn,
            _ => Color.FromRgb(76, 175, 80)  // GreenOk
        };
    }

    // ─── Жизненный цикл ────────────────────────────────────

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _liveData?.StopPolling();
        _ = _bt?.DisconnectAsync();
    }

    // ─── AI-анализ живых данных ─────────────────────────────

    private async void OnDashboardClicked(object? sender, EventArgs e)
    {
        if (_bt == null || !_bt.IsConnected)
        {
            LabelStatus.Text = "Нет подключения для дашборда";
            return;
        }
        await Navigation.PushAsync(new LiveChartsPage(_bt));
    }

    private async void OnGraphClicked(object? sender, EventArgs e)
    {
        if (_bt == null || !_bt.IsConnected)
        {
            LabelStatus.Text = "Нет подключения для графиков";
            return;
        }

        await Navigation.PushAsync(new GraphPage(_bt));
    }

    private async void OnAiAnalyzeClicked(object? sender, EventArgs e)
    {
        if (_bt == null || !_bt.IsConnected)
        {
            LabelStatus.Text = "Нет подключения для анализа";
            return;
        }

        BtnAi.IsEnabled = false;
        BtnAi.Text = "⏳...";
        LabelStatus.Text = "AI анализирует параметры...";

        try
        {
            // Собираем текущие значения из кэша
            var pidItems = new List<LivePidItem>();
            foreach (var (hex, (pid, value)) in _liveData!.Cache)
            {
                if (double.IsNaN(value)) continue;
                _pidSeverity.TryGetValue(hex, out var severity);
                pidItems.Add(new LivePidItem
                {
                    Name = pid.Name,
                    Value = Math.Round(value, 1),
                    Unit = pid.Unit,
                    MinVal = pid.Min,
                    MaxVal = pid.Max,
                    Severity = severity
                });
            }

            if (pidItems.Count == 0)
            {
                LabelStatus.Text = "Нет данных для анализа";
                return;
            }

            var api = IPlatformApplication.Current!.Services.GetRequiredService<ApiService>();
            var result = await api.AnalyzeLiveData(
                _brand ?? "",
                _model ?? "",
                pidItems);

            if (result == null)
            {
                LabelStatus.Text = "❌ Ошибка анализа: сервер недоступен";
                return;
            }

            // Показываем результат в модальном окне
            var dangerInfo = result.DangerCount > 0 ? $"🔴 {result.DangerCount} опасно  " : "";
            var warnInfo = result.WarningCount > 0 ? $"🟡 {result.WarningCount} подозрительно" : "";
            var summary = (dangerInfo + warnInfo).Trim();
            if (string.IsNullOrEmpty(summary))
                summary = "✅ Все параметры в норме";

            await DisplayAlert(
                $"🧠 AI-анализ — {summary}",
                result.Analysis.Length > 500
                    ? result.Analysis[..500] + "..."
                    : result.Analysis,
                "OK");

            // После закрытия алерта показываем полный текст в большом окне
            await ShowFullAnalysis(result.Analysis);

            LabelStatus.Text = $"Анализ завершён. PID: {result.PidCount}";
        }
        catch (Exception ex)
        {
            LabelStatus.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            BtnAi.IsEnabled = true;
            BtnAi.Text = "🧠 Анализ";
        }
    }

    /// <summary>
    /// Показывает полный текст AI-анализа в прокручиваемом окне.
    /// </summary>
    private async Task ShowFullAnalysis(string text)
    {
        // Рендерим Frame с WebView или Label в ScrollView через страницу алерта
        var page = new ContentPage
        {
            Title = "AI-анализ параметров",
            Padding = new Thickness(16)
        };

        var scroll = new ScrollView();
        var label = new Label
        {
            Text = text,
            FontFamily = "InterRegular",
            FontSize = 14,
            TextColor = Color.FromArgb("#1F1F1F"),
            FormattedText = ParseAnalysisMarkup(text)
        };

        scroll.Content = new Border
        {
            Padding = new Thickness(16),
            BackgroundColor = Color.FromArgb("#F5F5F5"),
            Stroke = Color.FromArgb("#E0E0E0"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label
                    {
                        Text = "📊 Результат анализа",
                        FontFamily = "InterSemiBold",
                        FontSize = 18,
                        TextColor = Color.FromArgb("#1F1F1F")
                    },
                    new BoxView
                    {
                        HeightRequest = 1,
                        Color = Color.FromArgb("#E0E0E0"),
                        Margin = new Thickness(0, 4)
                    },
                    label
                }
            }
        };

        page.Content = scroll;

        await Navigation.PushModalAsync(new NavigationPage(page)
        {
            BarBackgroundColor = Color.FromArgb("#1565C0"),
            BarTextColor = Colors.White
        });
    }

    /// <summary>
    /// Простейшая разметка: выделяет секции [ОБЩАЯ ОЦЕНКА], [АНАЛИЗ], [ВЫВОДЫ].
    /// </summary>
    private static FormattedString ParseAnalysisMarkup(string text)
    {
        var fs = new FormattedString();

        var sections = new[] { "[ОБЩАЯ ОЦЕНКА]", "[АНАЛИЗ]", "[ВЫВОДЫ]" };
        int lastIdx = 0;

        for (int i = 0; i <= sections.Length; i++)
        {
            int nextIdx = i < sections.Length ? text.IndexOf(sections[i], lastIdx, StringComparison.OrdinalIgnoreCase) : text.Length;
            if (nextIdx < 0) continue;

            // Body text before this section
            if (lastIdx < nextIdx)
            {
                var bodyText = text[lastIdx..nextIdx].Trim();
                if (!string.IsNullOrEmpty(bodyText))
                    fs.Spans.Add(new Span { Text = bodyText + "\n\n", FontSize = 14 });
            }

            if (i < sections.Length)
            {
                // Section header
                var header = sections[i];
                var headerEnd = text.IndexOf('\n', nextIdx);
                if (headerEnd < 0) headerEnd = text.Length;
                var headerText = text[nextIdx..Math.Min(headerEnd + 1, text.Length)];

                fs.Spans.Add(new Span
                {
                    Text = headerText,
                    FontFamily = "InterSemiBold",
                    FontSize = 15,
                    TextColor = Color.FromArgb("#1565C0")
                });

                lastIdx = headerEnd + 1;
            }
        }

        return fs;
    }

    private string? _brand;
    private string? _model;
}
