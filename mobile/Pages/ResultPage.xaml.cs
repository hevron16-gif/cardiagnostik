using System.Text.RegularExpressions;

namespace CarDiagnosticApp.Pages;

public partial class ResultPage : ContentPage
{
    private readonly string _diagnosisText;
    private readonly string _errorCode;
    private readonly string _carBrand;
    private readonly string _carModel;
    private readonly Services.ApiService? _api;
    private bool _feedbackGiven;
    private bool _hasClarifyingQuestions;
    private bool _uiBuilt;

    public ResultPage(string result, string? errorCode = null, string? carBrand = null, string? carModel = null)
    {
        InitializeComponent();
        _diagnosisText = result ?? "";
        _errorCode = errorCode ?? "";
        _carBrand = carBrand ?? "";
        _carModel = carModel ?? "";

        try
        {
            _api = IPlatformApplication.Current?.Services?.GetService<Services.ApiService>();
        }
        catch
        {
            _api = null;
        }

        try
        {
            ResultLabel.Text = System.Net.WebUtility.HtmlDecode(_diagnosisText);
            if (LabelErrorCode != null)
                LabelErrorCode.Text = string.IsNullOrWhiteSpace(_errorCode) ? "—" : _errorCode;
            if (LabelCarInfo != null)
                LabelCarInfo.Text = $"{_carBrand} {_carModel}".Trim();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ResultPage] ctor UI: {ex.Message}");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_uiBuilt) return;
        _uiBuilt = true;

        try
        {
            PopulateSafetyDisclaimer();
            PopulateExplanation();
            PopulateCauses();
            PopulateRepairs();
            PopulateSources();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ResultPage] OnAppearing: {ex}");
#if ANDROID
            Android.Util.Log.Error("AutoDiag", $"ResultPage.OnAppearing: {ex}");
#endif
            try
            {
                if (ResultLabel != null && string.IsNullOrWhiteSpace(ResultLabel.Text))
                    ResultLabel.Text = _diagnosisText;
            }
            catch { }
        }
    }

    /// <summary>
    /// Добавляет предупреждение о безопасности под блоком источников.
    /// </summary>
    private void PopulateSafetyDisclaimer()
    {
        if (SourcesList?.Parent is not Layout parentLayout)
            return;

        // Не дублируем карточку при повторном OnAppearing
        foreach (var child in parentLayout.Children)
        {
            if (child is Border b && b.ClassId == "safety-disclaimer")
                return;
        }

        var card = CreateSafetyDisclaimerCard();
        card.ClassId = "safety-disclaimer";
        var sourceIndex = parentLayout.Children.IndexOf(SourcesList);
        if (sourceIndex >= 0 && sourceIndex < parentLayout.Children.Count - 1)
            parentLayout.Children.Insert(sourceIndex + 1, card);
        else
            parentLayout.Children.Add(card);
    }

    /// <summary>
    /// Создаёт карточку-дисклеймер о безопасности ремонта.
    /// </summary>
    private static Border CreateSafetyDisclaimerCard()
    {
        Color dangerColor = Colors.Red;
        try
        {
            if (Application.Current?.Resources.TryGetValue("ErrorRed", out var er) == true && er is Color c)
                dangerColor = c;
        }
        catch { }
        var bgColor = new Color(dangerColor.Red, dangerColor.Green, dangerColor.Blue, 0.08f);

        var border = new Border
        {
            BackgroundColor = bgColor,
            Stroke = dangerColor,
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Padding = new Thickness(16),
            Margin = new Thickness(0, 12, 0, 8),
        };

        var stack = new VerticalStackLayout { Spacing = 8 };

        // Заголовок с иконкой
        var header = new HorizontalStackLayout { Spacing = 8 };
        header.Children.Add(new Label
        {
            Text = "&#xe002;",     // warning
            FontFamily = "MaterialIcons",
            FontSize = 20,
            TextColor = dangerColor,
            VerticalOptions = LayoutOptions.Center,
        });
        header.Children.Add(new Label
        {
            Text = "Внимание! Безопасность прежде всего",
            FontAttributes = FontAttributes.Bold,
            FontSize = 14,
            TextColor = dangerColor,
            VerticalOptions = LayoutOptions.Center,
        });

        // Текст дисклеймера
        Color bodyColor = Colors.Black;
        try
        {
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
            if (dark && Application.Current?.Resources.TryGetValue("LightOnDarkBackground", out var l) == true && l is Color lc)
                bodyColor = lc;
            else if (!dark && Application.Current?.Resources.TryGetValue("DarkOnLightBackground", out var d) == true && d is Color dc)
                bodyColor = dc;
            else if (dark)
                bodyColor = Colors.White;
        }
        catch { }

        var body = new Label
        {
            Text = "Некоторые советы по ремонту могут требовать профессиональных навыков. "
                 + "Операции с пометкой «Только специалист» выполняйте в автосервисе. "
                 + "Приложение не несёт ответственности за самостоятельный ремонт — "
                 + "всегда оценивайте свои силы и соблюдайте технику безопасности.",
            FontSize = 13,
            LineHeight = 1.4,
            TextColor = bodyColor,
        };

        stack.Children.Add(header);
        stack.Children.Add(body);
        border.Content = stack;
        return border;
    }

    /// <summary>
    /// Парсит AI-ответ и заполняет блок расшифровки (секция 1).
    /// </summary>
    private void PopulateExplanation()
    {
        if (string.IsNullOrWhiteSpace(_diagnosisText))
        {
            LabelExplanation.Text = "Нет данных для отображения.";
            return;
        }

        // Извлекаем секцию 1 — расшифровка ошибки
        var section = ExtractSection(_diagnosisText, 1);

        // Если секция не найдена — показываем весь текст
        LabelExplanation.Text = section ?? _diagnosisText;
    }

    /// <summary>
    /// Парсит AI-ответ и заполняет блок вероятных причин (секция 2).
    /// Каждая строка становится отдельным пунктом с иконкой.
    /// </summary>
    private void PopulateCauses()
    {
        var section = ExtractSection(_diagnosisText, 2);

        if (string.IsNullOrWhiteSpace(section))
        {
            AddCauseItem("Нет данных", isPlaceholder: true);
            return;
        }

        // Разбиваем секцию на строки и фильтруем пустые
        var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => l.Length > 3)
                          .ToList();

        if (lines.Count == 0)
        {
            AddCauseItem(section, isPlaceholder: false);
            return;
        }

        foreach (var line in lines.Take(5))
        {
            // Убираем ведущие тире/звёздочки/цифры, если есть
            var clean = Regex.Replace(line, @"^[-–•*\d]+\.?\s*", "");
            AddCauseItem(clean);
        }
    }

    /// <summary>
    /// Парсит AI-ответ и заполняет блок рекомендаций по ремонту.
    /// </summary>
    private void PopulateRepairs()
    {
        // Секция 3 = способы устранения, если нет — секция 4 = рекомендация
        var section = ExtractSection(_diagnosisText, 3)
                   ?? ExtractSection(_diagnosisText, 4);

        // Fallback: неструктурированный офлайн-текст (симулятор / старый кеш)
        if (string.IsNullOrWhiteSpace(section))
            section = ExtractUnstructuredRecommendations(_diagnosisText);

        if (string.IsNullOrWhiteSpace(section))
        {
            AddRepairItem("Нет данных", isPlaceholder: true);
            return;
        }

        var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .Where(l => l.Length > 3)
                          .ToList();

        if (lines.Count == 0)
        {
            AddRepairItem(section, isPlaceholder: false);
            return;
        }

        foreach (var line in lines.Take(6))
        {
            var clean = Regex.Replace(line, @"^[-–•*\d]+\.?\s*", "");
            var (safetyLevel, cleanText) = ParseSafetyLevel(clean);
            AddRepairItem(cleanText, safetyLevel: safetyLevel);
        }
    }

    /// <summary>
    /// Достаёт рекомендации из текста без нумерованных секций
    /// (старый офлайн-справочник, кеш симулятора).
    /// </summary>
    private static string? ExtractUnstructuredRecommendations(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var patterns = new[]
        {
            @"(?i)(?:рекомендац\w*|способ\w*\s+устранен\w*|решени\w*|что\s+делать)\s*:?\s*\n([\s\S]{10,800})",
            @"(?i)возможные\s+причины\s*:?\s*\n([\s\S]{10,600})",
        };

        foreach (var p in patterns)
        {
            var m = Regex.Match(text, p);
            if (m.Success && m.Groups.Count > 1)
            {
                var body = m.Groups[1].Value.Trim();
                // Обрезаем на следующем заголовке
                var cut = Regex.Match(body, @"\n\s*(?:симптом|категор|источник|⚠)");
                if (cut.Success) body = body[..cut.Index].Trim();
                if (body.Length > 8) return body;
            }
        }

        // Последний шанс: строки с «проверь/замени/очисти»
        var actionLines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 8 &&
                Regex.IsMatch(l, @"(?i)проверь|замен|очист|измер|диагност|подтян|сброс"))
            .Take(5)
            .ToList();
        return actionLines.Count > 0 ? string.Join("\n", actionLines) : null;
    }

    /// <summary>
    /// Извлекает уровень безопасности из текста совета.
    /// Возвращает (уровень, очищенный текст).
    /// Уровни: safe, caution, danger, none.
    /// </summary>
    private static (string Level, string Text) ParseSafetyLevel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return ("none", text);

        var lower = text.ToLower();

        if (lower.Contains("[только специалист]") || lower.Contains("только специалист"))
            return ("danger", Regex.Replace(text, @"\[Только специалист\]\s*", "", RegexOptions.IgnoreCase));
        if (lower.Contains("[осторожно]") || lower.Contains("осторожно]"))
            return ("caution", Regex.Replace(text, @"\[Осторожно\]\s*", "", RegexOptions.IgnoreCase));
        if (lower.Contains("[безопасно]"))
            return ("safe", Regex.Replace(text, @"\[Безопасно\]\s*", "", RegexOptions.IgnoreCase));

        return ("none", text);
    }

    /// <summary>
    /// Добавляет один пункт рекомендации в RepairList.
    /// Цвет зависит от уровня безопасности: зелёный (safe) / жёлтый (caution) / красный (danger).
    /// </summary>
    private void AddRepairItem(string text, bool isPlaceholder = false, string safetyLevel = "none")
    {
        var stack = new HorizontalStackLayout { Spacing = 10 };

        // Выбираем цвет и иконку по уровню безопасности
        var (iconCode, iconColor) = GetSafetyIcon(safetyLevel, isPlaceholder);

        var icon = new Label
        {
            Text = iconCode,
            FontFamily = "MaterialIcons",
            FontSize = 18,
            TextColor = iconColor,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 1, 0, 0)
        };

        var label = new Label
        {
            Text = text,
            FontFamily = "InterRegular",
            FontSize = 14,
            LineHeight = 1.4,
            TextColor = isPlaceholder
                ? Application.Current!.Resources["Gray400"] as Color ?? Colors.Gray
                : (Application.Current!.RequestedTheme == AppTheme.Dark
                    ? Application.Current!.Resources["LightOnDarkBackground"] as Color ?? Colors.White
                    : Application.Current!.Resources["DarkOnLightBackground"] as Color ?? Colors.Black)
        };

        stack.Children.Add(icon);
        stack.Children.Add(label);
        RepairList.Children.Add(stack);
    }

    /// <summary>
    /// Извлекает ссылки из AI-ответа и заполняет блок источников.
    /// Если ссылок нет — добавляет справочные ресурсы по OBD2.
    /// </summary>
    private void PopulateSources()
    {
        // Ищем URL в тексте ответа: https?://...
        var urlPattern = @"https?://[^\s,\.\)]+\.\w{2,}[^\s,\.\)]*";
        var matches = Regex.Matches(_diagnosisText, urlPattern);
        var uniqueUrls = matches.Select(m => m.Value.TrimEnd('.', ',', ')'))
                                .Distinct()
                                .ToList();

        // Добавляем найденные в ответе ссылки
        foreach (var url in uniqueUrls.Take(3))
        {
            AddSourceItem(GetFriendlyDomain(url), url);
        }

        // Если ничего не найдено — справочные ресурсы
        if (SourcesList.Children.Count == 0)
        {
            AddSourceItem("OBD-Codes.com", "https://www.obd-codes.com/");
            AddSourceItem("AutoErrorCodes.com", "https://www.autoerrorcodes.com/");
            AddSourceItem("Drive2 — сообщество", "https://www.drive2.ru/");
        }
    }

    /// <summary>
    /// Извлекает читаемое название домена из URL.
    /// </summary>
    private static string GetFriendlyDomain(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Host.Replace("www.", "");
        }
        catch
        {
            return url.Length > 40 ? url[..40] + "…" : url;
        }
    }

    /// <summary>
    /// Добавляет одну ссылку-источник в SourcesList.
    /// </summary>
    private void AddSourceItem(string label, string url)
    {
        var stack = new HorizontalStackLayout { Spacing = 10 };

        var icon = new Label
        {
            Text = "&#xe157;",           // link
            FontFamily = "MaterialIcons",
            FontSize = 16,
            TextColor = Application.Current!.Resources["Primary"] as Color ?? Colors.Blue,
            VerticalOptions = LayoutOptions.Center
        };

        var linkLabel = new Label
        {
            Text = label,
            FontFamily = "InterRegular",
            FontSize = 14,
            TextColor = Application.Current!.Resources["Primary"] as Color ?? Colors.Blue,
            TextDecorations = TextDecorations.Underline,
            VerticalOptions = LayoutOptions.Center
        };

        // Открываем URL по тапу
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                await Launcher.Default.OpenAsync(new Uri(url));
            }
            catch
            {
                await DisplayAlert("Ошибка", "Не удалось открыть ссылку", "OK");
            }
        };
        stack.GestureRecognizers.Add(tap);

        stack.Children.Add(icon);
        stack.Children.Add(linkLabel);
        SourcesList.Children.Add(stack);
    }

    /// <summary>
    /// Обработчик кнопки «👍 Помогло».
    /// </summary>
    private async void OnHelpfulTapped(object? sender, TappedEventArgs e)
    {
        if (_feedbackGiven) return;
        _feedbackGiven = true;

        DisableFeedbackButtons();
        LabelFeedback.Text = "✅ Спасибо за отзыв! Рады, что помогли.";
        LabelFeedback.IsVisible = true;

        if (_api != null)
            await _api.SendFeedback(_errorCode, helpful: true, carBrand: _carBrand, carModel: _carModel, diagnosis: _diagnosisText);

        // Локальная запись в базу самообучения
        _ = Task.Run(async () =>
        {
            try
            {
                await App.Learning.RecordFeedbackAsync(_errorCode, _carBrand, _carModel, wasHelpful: true);
            }
            catch { }
        });
    }

    /// <summary>
    /// Обработчик кнопки «👎 Не помогло».
    /// </summary>
    private async void OnNotHelpfulTapped(object? sender, TappedEventArgs e)
    {
        if (_feedbackGiven) return;
        _feedbackGiven = true;

        // Диалог для уточняющего вопроса
        var question = await DisplayPromptAsync(
            "Что не так?",
            "Опишите, чего не хватило в ответе или что нужно уточнить:",
            accept: "Отправить",
            cancel: "Пропустить",
            placeholder: "Например: нет симптомов для дизеля...",
            maxLength: 500);

        DisableFeedbackButtons();
        LabelFeedback.Text = string.IsNullOrWhiteSpace(question)
            ? "📝 Спасибо! Мы учтём это для улучшения ответов."
            : "📝 Спасибо! Ваш комментарий поможет улучшить ответы.";
        LabelFeedback.IsVisible = true;

        if (_api != null)
            await _api.SendFeedback(_errorCode, helpful: false, carBrand: _carBrand, carModel: _carModel, diagnosis: _diagnosisText, comment: question);

        // Локальная запись в базу самообучения
        _ = Task.Run(async () =>
        {
            try
            {
                await App.Learning.RecordFeedbackAsync(_errorCode, _carBrand, _carModel, wasHelpful: false);
            }
            catch { }
        });
    }

    /// <summary>
    /// Обработчик кнопки «🔧 Схема узлов» — переход на страницу схемы.
    /// </summary>
    private async void OnDiagramTapped(object? sender, TappedEventArgs e)
    {
        SchemePage.PendingAiAnalysis = _diagnosisText;
        await Navigation.PushAsync(new SchemePage(_errorCode, _carBrand, _carModel));
    }

    /// <summary>
    /// Обработчик кнопки «🛠️ Пошаговая инструкция».
    /// Генерирует руководство с картинками и схемами через RepairGuideService.GenerateGuideAsync,
    /// затем открывает RepairGuidePage.
    /// </summary>
    private async void OnStepInstructionTapped(object? sender, TappedEventArgs e)
    {
        BtnStepInstruction.IsEnabled = false;
        BtnStepInstruction.Opacity = 0.5;

        try
        {
            var guideSvc = new RepairGuideService();

            // Показываем индикатор генерации
            var loadingLabel = new Label
            {
                Text = "🔍 Генерирую инструкцию...\nПоиск схем и картинок в интернете...",
                TextColor = Colors.White,
                FontSize = 14,
                HorizontalOptions = LayoutOptions.Center,
            };

            // Генерируем руководство
            int guideId = await Task.Run(() =>
                guideSvc.GenerateGuideAsync(_errorCode, _carBrand, _carModel));

            if (guideId < 0)
            {
                await DisplayAlert("Ошибка",
                    $"Не удалось сгенерировать инструкцию для {_errorCode}.",
                    "OK");
                return;
            }

            var page = new RepairGuidePage();
            await page.LoadGuideAsync(guideId);
            await page.ShowPreCheckAsync();
            await Navigation.PushAsync(page);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ResultPage] Generate instruction error: {ex.Message}");
            await DisplayAlert("Ошибка",
                $"Не удалось сгенерировать инструкцию: {ex.Message}",
                "OK");
        }
        finally
        {
            BtnStepInstruction.IsEnabled = true;
            BtnStepInstruction.Opacity = 1.0;
        }
    }

    /// <summary>
    /// Обработчик кнопки «📖 Пошаговое руководство по ремонту».
    /// Открывает RepairGuidePage для текущего кода ошибки и авто.
    /// </summary>
    private async void OnRepairGuideTapped(object? sender, TappedEventArgs e)
    {
        var page = new RepairGuidePage();
        var found = await page.LoadBestGuideAsync(_errorCode, _carBrand, _carModel);

        if (!found)
        {
            await DisplayAlert("Нет руководства",
                $"Для кода {_errorCode} ({_carBrand} {_carModel}) пока нет пошагового руководства.\n" +
                "Будет добавлено в следующем обновлении.", "OK");
            return;
        }

        await page.ShowPreCheckAsync();
        await Navigation.PushAsync(page);
    }

    /// <summary>
    /// Обработчик кнопки «📋 Копировать» — копирует полный текст ответа в буфер.
    /// </summary>
    private async void OnCopyTapped(object? sender, TappedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_diagnosisText)) return;

        await Clipboard.Default.SetTextAsync(_diagnosisText);

        // Визуальная обратная связь: меняем иконку и текст на ✓
        IconCopy.Text = "&#xe5ca;";        // check
        LabelCopy.Text = "Скопировано";
        BtnCopy.Opacity = 0.6;

        // Через 1.5 с возвращаем исходное состояние
        await Task.Delay(1500);
        IconCopy.Text = "&#xe14d;";        // content_copy
        LabelCopy.Text = "Копировать";
        BtnCopy.Opacity = 1.0;
    }

    /// <summary>
    /// Затемняет обе кнопки после голосования.
    /// </summary>
    private void DisableFeedbackButtons()
    {
        BtnHelpful.Opacity = 0.4;
        BtnNotHelpful.Opacity = 0.4;
    }

    /// <summary>
    /// Добавляет один пункт причины в CausesList.
    /// </summary>
    private void AddCauseItem(string text, bool isPlaceholder = false)
    {
        var stack = new HorizontalStackLayout { Spacing = 10 };

        var icon = new Label
        {
            Text = isPlaceholder ? "&#xe14d;" : "&#xe5ca;",    // error_outline / arrow_forward_ios
            FontFamily = "MaterialIcons",
            FontSize = 16,
            TextColor = isPlaceholder
                ? Application.Current!.Resources["Gray400"] as Color ?? Colors.Gray
                : Application.Current!.Resources["WarningAmber"] as Color ?? Colors.Orange,
            VerticalOptions = LayoutOptions.Start,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var label = new Label
        {
            Text = text,
            FontFamily = "InterRegular",
            FontSize = 14,
            LineHeight = 1.4,
            TextColor = isPlaceholder
                ? Application.Current!.Resources["Gray400"] as Color ?? Colors.Gray
                : (Application.Current!.RequestedTheme == AppTheme.Dark
                    ? Application.Current!.Resources["LightOnDarkBackground"] as Color ?? Colors.White
                    : Application.Current!.Resources["DarkOnLightBackground"] as Color ?? Colors.Black)
        };

        stack.Children.Add(icon);
        stack.Children.Add(label);
        CausesList.Children.Add(stack);
    }

    /// <summary>
    /// Извлекает секцию AI-ответа по номеру (1-4).
    /// Обрабатывает варианты форматирования:
    ///   "1. Заголовок\nТекст"
    ///   "**1. Заголовок**\nТекст"
    ///   "1. **Заголовок**\nТекст"
    /// </summary>
    private static string? ExtractSection(string text, int sectionNumber)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Нормализуем концы строк и убираем \r
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // Ищем заголовок секции: опциональные **, номер, точка, пробел, заголовок
        // Гибкий паттерн: **1. Заголовок** или 1. **Заголовок** или 1. Заголовок
        var headerPattern = $@"(?:^|\n)\*{{0,2}}{sectionNumber}\.\s+\*{{0,2}}[^\n]+\*{{0,2}}\s*\n";
        var headerMatch = Regex.Match(normalized, headerPattern);

        if (!headerMatch.Success)
            return null;

        // Всё от конца заголовка до следующей секции или конца текста
        var startIdx = headerMatch.Index + headerMatch.Length;
        var remainder = normalized[startIdx..];

        // Ищем следующий заголовок секции (любой номер)
        var nextSection = Regex.Match(remainder, @"(?:^|\n)\*{{0,2}}\d+\.\s+\*{{0,2}}[^\n]+\*{{0,2}}\s*\n");

        var body = nextSection.Success
            ? remainder[..nextSection.Index]
            : remainder;

        return body.Trim();
    }

    /// <summary>
    /// Возвращает (иконка, цвет) для уровня безопасности.
    /// </summary>
    private static (string IconCode, Color Color) GetSafetyIcon(string level, bool isPlaceholder = false)
    {
        if (isPlaceholder)
            return ("&#xe14d;", Application.Current!.Resources["Gray400"] as Color ?? Colors.Gray);

        return level switch
        {
            "safe"    => ("&#xe86c;", Application.Current!.Resources["SuccessGreen"] as Color ?? Colors.Green),
            "caution" => ("&#xe002;", Application.Current!.Resources["WarningAmber"] as Color ?? Colors.Orange),
            "danger"  => ("&#xe002;", Application.Current!.Resources["ErrorRed"] as Color ?? Colors.Red),
            _         => ("&#xe86c;", Application.Current!.Resources["SuccessGreen"] as Color ?? Colors.Green),
        };
    }

}
