using CarDiagnosticApp.Agents;
using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using SQLite;
using System.Text;

namespace CarDiagnosticApp.Pages;

public partial class AdminPanelPage : ContentPage
{
    private readonly ApiService _api = IPlatformApplication.Current!.Services.GetRequiredService<ApiService>();
    private readonly LearningDbService _learningDb = new();
    private readonly DiagramDbService _diagramDb = new();
    private readonly ErrorHistoryService _errorHistory = new();
    private readonly OfflineDatabase _offlineDb = new();

    private int _currentTab = 0;
    private List<Button> _tabButtons = new();

    public AdminPanelPage()
    {
        InitializeComponent();
        _tabButtons = new() { BtnDashboard, BtnKnowledge, BtnSchemes, BtnFeedback, BtnSearch, BtnBrowser, BtnSystem, BtnCompetitors, BtnAutoIndustry, BtnCoding };
        LabelStatus.Text = $"Загружено: {DateTime.Now:dd.MM.yyyy HH:mm}";
        _ = LoadDashboardAsync();
    }

    // ─── Переключение вкладок ────────────────────────────────────────

    private void OnTabClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;

        var panels = new[] { PanelDashboard, PanelKnowledge, PanelSchemes, PanelFeedback, PanelSearch, PanelBrowser, PanelSystem, PanelCompetitors, PanelAutoIndustry, PanelCoding };
        var index = _tabButtons.IndexOf(btn);
        if (index < 0) return;

        for (int i = 0; i < panels.Length; i++)
        {
            panels[i].IsVisible = (i == index);
            _tabButtons[i].BackgroundColor = i == index
                ? Color.FromArgb("#2196F3")
                : Color.FromArgb("#2D2D2D");
            _tabButtons[i].TextColor = i == index
                ? Colors.White
                : Color.FromArgb("#B0B0B0");
            _tabButtons[i].FontFamily = i == index ? "InterSemiBold" : "Inter";
        }

        _currentTab = index;
        _ = LoadTabAsync(index);
    }

    private async Task LoadTabAsync(int tab)
    {
        switch (tab)
        {
            case 0: await LoadDashboardAsync(); break;
            case 1: await LoadKnowledgeAsync(); break;
            case 2: await LoadSchemesAsync(); break;
            case 3: await LoadFeedbackAsync(); break;
            case 5: await LoadBrowserAsync(); break;
            case 6: await LoadSystemAsync(); break;
            case 7: await LoadCompetitorsAsync(); break;
            case 8: await LoadAutoIndustryAsync(); break;
        }
    }

    // ─── Дашборд ─────────────────────────────────────────────────────

    private async void OnRefreshDashboard(object? s, EventArgs e) => await LoadDashboardAsync();

    private async Task LoadDashboardAsync()
    {
        try
        {
            BtnRefreshDashboard.Text = "⏳ Загрузка...";
            BtnRefreshDashboard.IsEnabled = false;

            // История ошибок — за всё время
            var history = await _errorHistory.GetHistorySinceAsync(DateTime.MinValue);
            int total = history.Count;
            int hasDiagnosis = history.Count(h => !string.IsNullOrWhiteSpace(h.DiagnosisSnippet));

            StatDiagnoses.Text = total.ToString();
            StatSuccessful.Text = hasDiagnosis.ToString();

            // Фидбек
            await _offlineDb.InitAsync();
            var feedback = await _offlineDb.Feedback.GetAllAsync();
            int pos = feedback.Count(f => f.Helpful);
            int neg = feedback.Count(f => !f.Helpful);
            StatPositiveFeedback.Text = pos.ToString();
            StatNegativeFeedback.Text = neg.ToString();

            // Размеры БД
            StatDiagDbSize.Text = GetFileSizeMb(Path.Combine(FileSystem.AppDataDirectory, "diagnostics.db"));
            StatOfflineDbSize.Text = GetFileSizeMb(Path.Combine(FileSystem.AppDataDirectory, "offline.db"));
            StatDiagramsDbSize.Text = GetFileSizeMb(Path.Combine(FileSystem.AppDataDirectory, "diagrams.db"));

            // База знаний
            var knowledge = await _learningDb.GetStaleKnowledgeAsync(maxConfidence: 1.0, staleDays: 3650);
            StatKnowledgeCount.Text = $"{knowledge.Count} записей";
            double avgConf = knowledge.Count > 0 ? knowledge.Average(k => k.Confidence) : 0;
            StatKnowledgeAvgConf.Text = $"Средняя уверенность: {avgConf:P0}";

            // Схемы
            var diagrams = await _diagramDb.GetPendingRequestsAsync();
            StatDiagramsCount.Text = $"{diagrams.Count} запросов";
            int found = diagrams.Count(d => d.Status == "found");
            int pending = diagrams.Count(d => d.Status == "pending");
            StatDiagramsBreakdown.Text = $"✅ {found} найдено  ⏳ {pending} ожидают";

            // Топ ошибок за месяц
            var monthAgo = DateTime.Now.AddMonths(-1);
            var recent = history.Where(h => h.DetectedAt >= monthAgo).ToList();
            var topErrors = recent
                .GroupBy(h => h.ErrorCode ?? "—")
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key} ({g.Count()})");
            StatTopErrors.Text = topErrors.Any()
                ? string.Join("\n", topErrors)
                : "Нет данных";

            // Топ марок за месяц
            var topBrands = recent
                .GroupBy(h => string.IsNullOrWhiteSpace(h.Brand) ? "—" : h.Brand)
                .OrderByDescending(g => g.Count())
                .Take(5)
                .Select(g => $"{g.Key} ({g.Count()})");
            StatTopBrands.Text = topBrands.Any()
                ? string.Join("\n", topBrands)
                : "Нет данных";

            // ── Загружаем последний отчёт (если уже был сгенерирован) ──
            var latestReport = await Services.ReportService.LoadLatestReportContentAsync();
            if (latestReport != null)
            {
                ReportText.Text = latestReport;
                ReportContainer.IsVisible = true;
            }

            // ── История файлов отчётов ──
            LoadReportHistory();

            LabelStatus.Text = $"Обновлено: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            LabelStatus.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            BtnRefreshDashboard.Text = "🔄 Обновить статистику";
            BtnRefreshDashboard.IsEnabled = true;
        }
    }

    private static string GetFileSizeMb(string path)
    {
        try
        {
            if (!File.Exists(path)) return "—";
            var size = new FileInfo(path).Length;
            if (size < 1024) return $"{size} B";
            if (size < 1024 * 1024) return $"{size / 1024.0:F1} KB";
            return $"{size / (1024.0 * 1024.0):F1} MB";
        }
        catch { return "—"; }
    }

    /// <summary>
    /// Формирует полный текстовый отчёт по всем разделам админ-панели.
    /// </summary>
    private async void OnGenerateReport(object? sender, EventArgs e)
    {
        try
        {
            BtnGenerateReport.Text = "⏳ Формирование...";
            BtnGenerateReport.IsEnabled = false;

            var text = await Services.ReportService.GenerateReportTextAsync();
            if (text == null)
            {
                ReportText.Text = "Не удалось сформировать отчёт.";
                ReportContainer.IsVisible = true;
                return;
            }

            ReportText.Text = text;
            ReportContainer.IsVisible = true;

            // Сохраняем на рабочий стол
            var filePath = await Services.ReportService.GenerateAndSaveAsync();
            LabelStatus.Text = filePath != null
                ? $"Отчёт сохранён: {Path.GetFileName(filePath)}"
                : $"Отчёт сформирован: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            ReportText.Text = $"Ошибка формирования отчёта: {ex.Message}";
            ReportContainer.IsVisible = true;
        }
        finally
        {
            BtnGenerateReport.Text = "📄 Сформировать полный отчёт";
            BtnGenerateReport.IsEnabled = true;
        }

        // Обновляем историю
        LoadReportHistory();
    }

    /// <summary>
    /// Загружает список сохранённых файлов отчётов с рабочего стола.
    /// </summary>
    private void LoadReportHistory()
    {
        try
        {
            var files = Services.ReportService.GetReportFiles();
            ReportHistoryList.ItemsSource = files;
            LabelReportHistoryCount.Text = files.Count > 0
                ? $"Всего сохранено: {files.Count} файл(ов)"
                : "Файлы не найдены";
        }
        catch (Exception ex)
        {
            LabelReportHistoryCount.Text = $"Ошибка: {ex.Message}";
        }
    }

    /// <summary>
    /// При выборе отчёта из истории — загружает его содержимое.
    /// </summary>
    private async void OnReportHistorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not ReportFileInfo info)
            return;

        try
        {
            var content = await File.ReadAllTextAsync(info.FilePath);
            ReportText.Text = content;
            ReportContainer.IsVisible = true;
        }
        catch (Exception ex)
        {
            ReportText.Text = $"Ошибка чтения: {ex.Message}";
            ReportContainer.IsVisible = true;
        }

        // Сбрасываем выделение
        ReportHistoryList.SelectedItem = null;
    }

    // ─── Знания ──────────────────────────────────────────────────────

    private async void OnRefreshKnowledge(object? s, EventArgs e) => await LoadKnowledgeAsync();
    private async void OnApplyKnowledgeFilter(object? s, EventArgs e) => await LoadKnowledgeAsync();

    private async Task LoadKnowledgeAsync()
    {
        try
        {
            double minConf = 0.3;
            if (double.TryParse(KnowledgeConfidenceFilter.Text, out var parsed))
                minConf = parsed;

            // GetStaleKnowledgeAsync с maxConfidence=1.0 и большим сроком ≈ все записи
            var all = await _learningDb.GetStaleKnowledgeAsync(maxConfidence: 1.0, staleDays: 3650);
            var filtered = all
                .Where(k => k.Confidence >= minConf)
                .OrderByDescending(k => k.Confidence)
                .ToList();

            KnowledgeList.ItemsSource = filtered;
            LabelKnowledgeCount.Text = $"Показано: {filtered.Count} / {all.Count} записей (уверенность ≥ {minConf:P0})";
        }
        catch (Exception ex)
        {
            LabelKnowledgeCount.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ─── Схемы ───────────────────────────────────────────────────────

    private async Task LoadSchemesAsync()
    {
        try
        {
            var pending = await _diagramDb.GetPendingRequestsAsync();
            var waiting = pending.Where(p => p.Status == "pending").ToList();
            var total = pending.Count;

            LabelSchemesCount.Text = $"Всего запросов: {total} | Ожидают: {waiting.Count} | " +
                $"Статус 'found': {pending.Count(p => p.Status == "found")} | " +
                $"Статус 'timeout': {pending.Count(p => p.Status == "timeout")}";

            LabelPendingCount.Text = $"Запросов в ожидании: {waiting.Count}. " +
                $"Макс. попыток: {waiting.Max(p => (int?)p.RetryCount) ?? 0}. " +
                $"Тайм-аутов: {pending.Count(p => p.Status == "timeout")}";

            var found = pending.Where(p => p.Status == "found").ToList();
            SchemeList.ItemsSource = found.Select(p => new
            {
                ErrorCode = p.ErrorCode,
                BrandModel = $"{p.CarBrand} {p.CarModel}",
                Source = "найдено"
            }).ToList();
        }
        catch (Exception ex)
        {
            LabelSchemesCount.Text = $"Ошибка: {ex.Message}";
        }
    }

    private async void OnRetryPending(object? s, EventArgs e)
    {
        try
        {
            BtnRetryPending.Text = "⏳ Выполняется...";
            BtnRetryPending.IsEnabled = false;

            var agent = BackgroundAgent.Instance;
            agent.Start();
            await Task.Delay(2000);

            await LoadSchemesAsync();
            LabelPendingCount.Text += "\n✅ Запущен цикл повтора.";
        }
        catch (Exception ex)
        {
            LabelPendingCount.Text += $"\n❌ {ex.Message}";
        }
        finally
        {
            BtnRetryPending.Text = "🔁 Запустить повтор";
            BtnRetryPending.IsEnabled = true;
        }
    }

    // ─── Фидбек ──────────────────────────────────────────────────────

    private async Task LoadFeedbackAsync()
    {
        try
        {
            await _offlineDb.InitAsync();
            var feedback = await _offlineDb.Feedback.GetAllAsync();

            int pos = feedback.Count(f => f.Helpful);
            int neg = feedback.Count(f => !f.Helpful);
            FeedbackHelpfulCount.Text = pos.ToString();
            FeedbackNotHelpfulCount.Text = neg.ToString();
            LabelFeedbackEntries.Text = $"Всего оценок: {feedback.Count} (👍 {pos} / 👎 {neg})";

            var items = feedback
                .OrderByDescending(f => f.Id)
                .Take(100)
                .Select(f => new FeedbackItem
                {
                    ErrorCode = f.ErrorCode ?? "—",
                    CarBrand = f.CarBrand ?? "—",
                    Comment = f.Helpful ? "Положительная оценка" : "Отрицательная оценка",
                    RatingIcon = f.Helpful ? "👍" : "👎",
                    Timestamp = $"ID: {f.Id}",
                })
                .ToList();

            FeedbackList.ItemsSource = items;
        }
        catch (Exception ex)
        {
            LabelFeedbackEntries.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ─── Поиск ───────────────────────────────────────────────────────

    private async void OnSearchRun(object? s, EventArgs e)
    {
        var code = (SearchErrorCode.Text ?? "").Trim();
        var brand = (SearchBrand.Text ?? "").Trim();
        var model = (SearchModel.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            LabelSearchResults.Text = "⚠ Введите код ошибки";
            LabelSearchResults.IsVisible = true;
            return;
        }

        try
        {
            BtnSearchRun.Text = "⏳ Ищем...";
            BtnSearchRun.IsEnabled = false;
            SearchLoader.IsVisible = true;
            SearchLoader.IsRunning = true;
            SearchResultList.IsVisible = false;
            LabelSearchResults.IsVisible = false;

            var result = await _api.SearchSchemesAsync(code, brand, model);

            if (result?.results != null && result.results.Count > 0)
            {
                // Преобразуем в анонимный тип с PascalCase для XAML-биндингов
                var items = result.results.Select(r => new
                {
                    Title = r.title,
                    Url = r.url,
                    Snippet = r.snippet,
                }).ToList();
                SearchResultList.ItemsSource = items;
                SearchResultList.IsVisible = true;
                LabelSearchResults.Text = $"Найдено: {items.Count} результатов ({result.search_engine})";
            }
            else
            {
                LabelSearchResults.Text = "Ничего не найдено";
                SearchResultList.IsVisible = false;
            }
            LabelSearchResults.IsVisible = true;
        }
        catch (Exception ex)
        {
            LabelSearchResults.Text = $"Ошибка: {ex.Message}";
            LabelSearchResults.IsVisible = true;
        }
        finally
        {
            BtnSearchRun.Text = "🔍 Искать схему";
            BtnSearchRun.IsEnabled = true;
            SearchLoader.IsVisible = false;
            SearchLoader.IsRunning = false;
        }
    }

    // ─── Браузер БД ───────────────────────────────────────────────────

    private int _browserSection = 0; // 0=ошибки, 1=решения, 2=схемы
    private long _editingId = -1; // -1=добавление, >=0=редактирование
    private List<Button> _browserSectionButtons = new();

    private async void OnRefreshBrowser(object? s, EventArgs e) => await LoadBrowserAsync();
    private async void OnBrowserSectionClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;

        if (_browserSectionButtons.Count == 0)
            _browserSectionButtons = new() { BtnSectionErrors, BtnSectionKnowledge, BtnSectionSchemes };

        var index = _browserSectionButtons.IndexOf(btn);
        if (index < 0) return;

        for (int i = 0; i < _browserSectionButtons.Count; i++)
        {
            _browserSectionButtons[i].BackgroundColor = i == index
                ? Color.FromArgb("#2196F3")
                : Color.FromArgb("#2D2D2D");
            _browserSectionButtons[i].TextColor = i == index ? Colors.White : Color.FromArgb("#B0B0B0");
            _browserSectionButtons[i].FontFamily = i == index ? "InterSemiBold" : "Inter";
        }

        _browserSection = index;
        await LoadBrowserAsync();
    }

    private async Task LoadBrowserAsync()
    {
        try
        {
            LabelBrowserCount.Text = "⏳ Загрузка...";
            var items = new List<BrowserEntry>();

            switch (_browserSection)
            {
                case 0: // 🔴 Ошибки — история диагностик
                    var history = await _errorHistory.GetHistorySinceAsync(DateTime.MinValue);
                    items = history
                        .OrderByDescending(h => h.DetectedAt)
                        .Take(300)
                        .Select(h => new BrowserEntry
                        {
                            Icon = "🔴",
                            Code = h.ErrorCode ?? "—",
                            Section = "Ошибка",
                            BadgeColor = Color.FromArgb("#F44336"),
                            Title = $"{h.Brand ?? "—"} {h.Model ?? "—"}",
                            Details = string.IsNullOrWhiteSpace(h.DiagnosisSnippet)
                                ? "Диагноз не выполнялся"
                                : h.DiagnosisSnippet.Length > 120
                                    ? h.DiagnosisSnippet[..120] + "..."
                                    : h.DiagnosisSnippet,
                            Meta = $"📅 {h.DetectedAt:dd.MM.yyyy HH:mm}  |  ID: {h.Id}",
                            Id = h.Id,
                            SectionIndex = 0,
                            Brand = h.Brand ?? "",
                            Model = h.Model ?? "",
                            FullDescription = h.DiagnosisSnippet ?? "",
                        }).ToList<BrowserEntry>();
                    break;

                case 1: // 📚 Решения — база знаний
                    var knowledge = await _learningDb.GetStaleKnowledgeAsync(maxConfidence: 1.0, staleDays: 3650);
                    items = knowledge
                        .OrderByDescending(k => k.Confidence)
                        .Take(300)
                        .Select(k => new BrowserEntry
                        {
                            Icon = "📚",
                            Code = k.ErrorCode ?? "—",
                            Section = "Решение",
                            BadgeColor = Color.FromArgb("#2196F3"),
                            Title = $"Уверенность: {k.Confidence:P0}",
                            Details = (k.DiagnosisSummary ?? "").Length > 120
                                ? k.DiagnosisSummary[..120] + "..."
                                : k.DiagnosisSummary ?? "",
                            Meta = $"🆔 {k.Id}  |  👍 {k.PositiveFeedback} / 👎 {k.NegativeFeedback}",
                            Id = k.Id,
                            SectionIndex = 1,
                            Brand = k.CarBrand ?? "",
                            Model = k.CarModel ?? "",
                            FullDescription = k.KnownSolutions ?? k.LastDiagnosisText ?? "",
                        }).ToList<BrowserEntry>();
                    break;

                case 2: // 🖼️ Схемы — записи DiagramDbService
                    var pending = await _diagramDb.GetPendingRequestsAsync();
                    items = pending
                        .OrderByDescending(p => p.CreatedAt)
                        .Take(300)
                        .Select(p => new BrowserEntry
                        {
                            Icon = p.Status == "found" ? "🖼️" :
                                   p.Status == "pending" ? "⏳" :
                                   p.Status == "timeout" ? "⏱️" : "❓",
                            Code = p.ErrorCode ?? "—",
                            Section = p.Status ?? "?",
                            BadgeColor = p.Status == "found" ? Color.FromArgb("#4CAF50") :
                                          p.Status == "pending" ? Color.FromArgb("#FF9800") :
                                          Color.FromArgb("#9E9E9E"),
                            Title = $"{p.CarBrand ?? "—"} {p.CarModel ?? "—"}",
                            Details = $"Попыток: {p.RetryCount} / 5",
                            Meta = $"📅 {p.CreatedAt:dd.MM.yyyy HH:mm}  |  🆔 {p.Id}",
                            Id = p.Id,
                            SectionIndex = 2,
                            Brand = p.CarBrand ?? "",
                            Model = p.CarModel ?? "",
                            FullDescription = p.ErrorCode ?? "",
                        }).ToList<BrowserEntry>();
                    break;
            }

            BrowserList.ItemsSource = items;
            LabelBrowserCount.Text = $"Показано: {items.Count} записей";
        }
        catch (Exception ex)
        {
            LabelBrowserCount.Text = $"Ошибка: {ex.Message}";
        }
    }

    // ─── Добавление записи вручную ────────────────────────────────────

    private void OnAddManualClicked(object? sender, EventArgs e)
    {
        var isVisible = AddFormPanel.IsVisible;
        AddFormPanel.IsVisible = !isVisible;
        BtnAddManual.Text = isVisible ? "➕ Добавить" : "❌ Закрыть";

        if (!isVisible)
        {
            _editingId = -1;
            BtnSaveAdd.Text = "💾 Сохранить";
            // Предзаполняем тип по текущему разделу
            AddFormType.SelectedIndex = _browserSection;
            var sections = new[] { "Ошибки", "Решения", "Схемы" };
            AddFormSectionLabel.Text = $"(раздел: {sections[_browserSection]})";
            AddFormCode.Text = "";
            AddFormBrand.Text = "";
            AddFormModel.Text = "";
            AddFormDescription.Text = "";
            AddFormStatus.IsVisible = false;
        }
    }

    private void OnCancelAdd(object? sender, EventArgs e)
    {
        AddFormPanel.IsVisible = false;
        BtnAddManual.Text = "➕ Добавить";
        _editingId = -1;
        BtnSaveAdd.Text = "💾 Сохранить";
    }

    /// <summary>
    /// Обработчик нажатия ✏️ на элементе списка — заполняет форму для редактирования.
    /// </summary>
    private async void OnEditEntryClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.BindingContext is not BrowserEntry entry) return;

        _editingId = entry.Id;
        AddFormPanel.IsVisible = true;
        BtnAddManual.Text = "❌ Закрыть";
        BtnSaveAdd.Text = "💾 Обновить";

        AddFormType.SelectedIndex = entry.SectionIndex;
        var sections = new[] { "Ошибки", "Решения", "Схемы" };
        AddFormSectionLabel.Text = $"(редактирование, раздел: {sections[entry.SectionIndex]})";
        AddFormCode.Text = entry.Code == "—" ? "" : entry.Code;
        AddFormBrand.Text = entry.Brand;
        AddFormModel.Text = entry.Model;
        AddFormDescription.Text = entry.FullDescription;
        AddFormStatus.IsVisible = false;
    }

    /// <summary>
    /// Обработчик 🗑️ — удаление записи с подтверждением.
    /// </summary>
    private async void OnDeleteEntryClicked(object? sender, EventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.BindingContext is not BrowserEntry entry) return;

        var sections = new[] { "Ошибки", "Решения", "Схемы" };
        var sectionName = sections[entry.SectionIndex];
        var confirmed = await DisplayAlert(
            "Удаление записи",
            $"Удалить запись «{entry.Code}» из раздела «{sectionName}»?\n\n{entry.Title}\n\nЭто действие нельзя отменить.",
            "🗑️ Удалить",
            "Отмена");

        if (!confirmed) return;

        try
        {
            switch (entry.SectionIndex)
            {
                case 0: // Ошибки
                    var errDb = await Task.Run(() => new SQLiteAsyncConnection(
                        Path.Combine(FileSystem.AppDataDirectory, "diagnostics.db")));
                    await errDb.ExecuteAsync("DELETE FROM car_error_history WHERE Id=?", entry.Id);
                    break;

                case 1: // Решения (база знаний)
                    var learnDb = await Task.Run(() => new SQLiteAsyncConnection(
                        Path.Combine(FileSystem.AppDataDirectory, "learning.db")));
                    await learnDb.ExecuteAsync("DELETE FROM LearnedKnowledge WHERE Id=?", entry.Id);
                    break;

                case 2: // Схемы
                    var diagDb = await Task.Run(() => new SQLiteAsyncConnection(
                        Path.Combine(FileSystem.AppDataDirectory, "diagrams.db")));
                    await diagDb.ExecuteAsync("DELETE FROM PendingDiagramRequests WHERE Id=?", entry.Id);
                    break;
            }

            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", $"Не удалось удалить: {ex.Message}", "OK");
        }
    }

    private async void OnSaveAdd(object? sender, EventArgs e)
    {
        try
        {
            BtnSaveAdd.IsEnabled = false;
            BtnSaveAdd.Text = "⏳ ...";

            var code = (AddFormCode.Text ?? "").Trim().ToUpperInvariant();
            var brand = (AddFormBrand.Text ?? "").Trim();
            var model = (AddFormModel.Text ?? "").Trim();
            var description = (AddFormDescription.Text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(code))
            {
                AddFormStatus.Text = "❌ Введите код ошибки (например P0300)";
                AddFormStatus.TextColor = Color.FromArgb("#F44336");
                AddFormStatus.IsVisible = true;
                return;
            }

            var typeIndex = AddFormType.SelectedIndex;

            if (_editingId >= 0)
            {
                // ═══ Режим редактирования существующей записи ═══
                switch (typeIndex)
                {
                    case 0: // Ошибка — прямой UPDATE через SQLite
                        var errDb = await Task.Run(() => new SQLiteAsyncConnection(
                            Path.Combine(FileSystem.AppDataDirectory, "diagnostics.db")));
                        await errDb.ExecuteAsync(
                            "UPDATE car_error_history SET ErrorCode=?, Brand=?, Model=?, DiagnosisSnippet=? WHERE Id=?",
                            code, brand, model, description, _editingId);
                        break;

                    case 1: // Решение — через EnrichKnowledgeAsync
                        await _learningDb.EnrichKnowledgeAsync(
                            id: (int)_editingId,
                            solutions: description,
                            confidenceBoost: 0);
                        break;

                    case 2: // Схема — прямой UPDATE
                        var diagDb = await Task.Run(() => new SQLiteAsyncConnection(
                            Path.Combine(FileSystem.AppDataDirectory, "diagrams.db")));
                        await diagDb.ExecuteAsync(
                            "UPDATE PendingDiagramRequests SET ErrorCode=?, CarBrand=?, CarModel=? WHERE Id=?",
                            code, brand, model, _editingId);
                        break;
                }

                AddFormStatus.Text = $"✅ Запись обновлена ({code})";
                AddFormStatus.TextColor = Color.FromArgb("#4CAF50");
                AddFormStatus.IsVisible = true;
                _editingId = -1;
                BtnSaveAdd.Text = "💾 Сохранить";
            }
            else
            {
                // ═══ Режим добавления новой записи ═══
                switch (typeIndex)
                {
                    case 0: // Ошибка (история)
                        var historyService = new ErrorHistoryService();
                        await historyService.SaveErrorsAsync(
                            vin: "",
                            brand: brand,
                            model: model,
                            errors: new List<ObdError>
                            {
                                new ObdError { Code = code, Type = ObdErrorType.Current }
                            }
                        );
                        break;

                    case 1: // Решение (база знаний)
                        await _learningDb.RecordDiagnosisAsync(
                            errorCode: code,
                            carBrand: brand,
                            carModel: model,
                            diagnosisText: description,
                            summary: description.Length > 200 ? description[..200] : description,
                            likelyCause: ""
                        );
                        break;

                    case 2: // Схема (запрос)
                        var diagramDb = new DiagramDbService();
                        await diagramDb.SavePendingRequestAsync(
                            brand: brand,
                            model: model,
                            errorCode: code,
                            searchQuery: description.Length > 200 ? description[..200] : description
                        );
                        break;
                }

                AddFormStatus.Text = $"✅ Запись сохранена ({code})";
                AddFormStatus.TextColor = Color.FromArgb("#4CAF50");
                AddFormStatus.IsVisible = true;
            }

            // Обновить список
            await LoadBrowserAsync();
        }
        catch (Exception ex)
        {
            AddFormStatus.Text = $"❌ Ошибка: {ex.Message}";
            AddFormStatus.TextColor = Color.FromArgb("#F44336");
            AddFormStatus.IsVisible = true;
        }
        finally
        {
            BtnSaveAdd.IsEnabled = true;
            if (_editingId < 0)
                BtnSaveAdd.Text = "💾 Сохранить";
        }
    }

    // ─── Система ─────────────────────────────────────────────────────

    private async void OnRefreshSystem(object? s, EventArgs e) => await LoadSystemAsync();

    // ─── Настройки синхронизации ───

    private void OnSyncPeriodChanged(object? s, ValueChangedEventArgs e)
    {
        int hours = (int)Math.Round(e.NewValue);
        LblSyncPeriod.Text = SettingsService.SyncPeriodLabel;
        SettingsService.SyncPeriodHours = hours;
    }

    private void OnSyncEnabledToggled(object? s, ToggledEventArgs e) =>
        SettingsService.SyncEnabled = e.Value;

    private void OnOfflineToggled(object? s, ToggledEventArgs e) =>
        SettingsService.OfflineMode = e.Value;

    private void LoadSyncSettings()
    {
        int hours = SettingsService.SyncPeriodHours;
        SliderSyncPeriod.Value = hours;
        LblSyncPeriod.Text = SettingsService.SyncPeriodLabel;
        SwitchSyncEnabled.IsToggled = SettingsService.SyncEnabled;
        SwitchOffline.IsToggled = SettingsService.OfflineMode;
    }
    private async void OnBgForceRun(object? s, EventArgs e) => await ForceRunAgent("BackgroundAgent", BtnBgForceRun, AgentBgInfo);
    private async void OnUpForceRun(object? s, EventArgs e) => await ForceRunAgent("UpdateAgent", BtnUpForceRun, AgentUpInfo);

    private async Task LoadSystemAsync()
    {
        try
        {
            // BackgroundAgent — singleton
            var bgAgent = BackgroundAgent.Instance;
            AgentBgStatus.Text = "Активен";
            (AgentBgStatus.Parent as Border)!.BackgroundColor = Color.FromArgb("#4CAF50");
            AgentBgInfo.Text = $"Тип: singleton | Интервал: 30 мин | Start()/Stop()";

            // UpdateAgent — singleton (как и BackgroundAgent)
            try
            {
                var upAgent = UpdateAgent.Instance;
                AgentUpStatus.Text = "Активен";
                (AgentUpStatus.Parent as Border)!.BackgroundColor = Color.FromArgb("#4CAF50");
                AgentUpInfo.Text = $"Интервал: 14 дней | ForceRun доступен";
            }
            catch
            {
                AgentUpStatus.Text = "Не активен";
                (AgentUpStatus.Parent as Border)!.BackgroundColor = Color.FromArgb("#F44336");
                AgentUpInfo.Text = "Агент недоступен";
            }

            // Настройки синхронизации
            LoadSyncSettings();

            SystemLog.Text = $"🟢 [{DateTime.Now:HH:mm:ss}] Панель администратора загружена\n" +
                $"— BackgroundAgent: singleton\n" +
                $"— Период синхр: {SettingsService.SyncPeriodLabel}\n" +
                $"— AppData: {FileSystem.AppDataDirectory}";

            LabelStatus.Text = $"Обновлено: {DateTime.Now:dd.MM.yyyy HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SystemLog.Text = $"❌ [{DateTime.Now:HH:mm:ss}] {ex.Message}";
        }
    }

    private async Task ForceRunAgent(string agentName, Button btn, Label infoLabel)
    {
        try
        {
            btn.Text = "⏳ Выполняется...";
            btn.IsEnabled = false;

            if (agentName == "BackgroundAgent")
            {
                var agent = BackgroundAgent.Instance;
                agent.Start();
                await Task.Delay(3000);
                infoLabel.Text += $"\n✅ Запущен: {DateTime.Now:HH:mm}";
                SystemLog.Text += $"\n🟢 [{DateTime.Now:HH:mm:ss}] BackgroundAgent.Start()";
            }
            else if (agentName == "UpdateAgent")
            {
                var agent = UpdateAgent.Instance;
                var result = await agent.ForceRunAsync();
                infoLabel.Text += $"\n✅ ForceRun: {DateTime.Now:HH:mm}";
                SystemLog.Text += $"\n🟢 [{DateTime.Now:HH:mm:ss}] UpdateAgent.ForceRunAsync() → {result}";
            }
        }
        catch (Exception ex)
        {
            infoLabel.Text += $"\n❌ {ex.Message}";
            SystemLog.Text += $"\n🔴 {agentName} — ошибка: {ex.Message}";
        }
        finally
        {
            btn.Text = "▶ ForceRun";
            btn.IsEnabled = true;
        }
    }

    // ═══════════════════════════════════════════════════
    // ВКЛАДКА: КОНКУРЕНТЫ
    // ═══════════════════════════════════════════════════

    private async void OnRefreshCompetitors(object? s, EventArgs e)
    {
        BtnRefreshCompetitors.IsEnabled = false;
        LblCompStatus.Text = "Проверка конкурентов...";
        try
        {
            // Принудительный запуск мониторинга
            var result = await CompetitorMonitor.Instance.ForceCheckAsync();
            LblCompStatus.Text = result.Replace('\n', ' ');
        }
        catch (Exception ex)
        {
            LblCompStatus.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            BtnRefreshCompetitors.IsEnabled = true;
        }

        // Перезагружаем данные
        await LoadCompetitorsAsync();
    }

    private async void OnCompReport(object? s, EventArgs e)
    {
        BtnCompReport.IsEnabled = false;
        try
        {
            var compSvc = new CompetitorService();
            var report = await compSvc.GenerateReportAsync();

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"competitor_report_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);

            await DisplayAlertAsync("Отчёт сохранён",
                $"Файл: {Path.GetFileName(path)}", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            BtnCompReport.IsEnabled = true;
        }
    }

    private async Task LoadCompetitorsAsync()
    {
        LblCompStatus.Text = "Загрузка...";
        BtnRefreshCompetitors.IsEnabled = false;

        try
        {
            var compSvc = new CompetitorService();
            int seeded = await compSvc.SeedDefaultCompetitorsAsync();

            var all = await compSvc.GetAllAsync();
            var changes = await compSvc.GetAllChangesAsync(20);
            var summary = await compSvc.BuildSummaryAsync();

            LblCompSummary.Text = summary;
            LblCompStatus.Text = $"Всего: {all.Count} | Обновлено: {DateTime.Now:HH:mm}";

            // Строим карточки
            CompListStack.Children.Clear();

            foreach (var c in all)
            {
                var card = new Border
                {
                    Stroke = Color.FromArgb("#444"),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
                    BackgroundColor = Color.FromArgb("#2D2D2D"),
                    Padding = new Thickness(12),
                };

                var stack = new VerticalStackLayout { Spacing = 4 };

                // Заголовок
                var header = new HorizontalStackLayout { Spacing = 8 };
                header.Children.Add(new Label
                {
                    Text = c.Name,
                    FontFamily = "InterSemiBold",
                    FontSize = 14,
                    TextColor = Colors.White,
                });

                if (!double.IsNaN(c.Rating))
                    header.Children.Add(new Label
                    {
                        Text = $"★{c.Rating:F1}",
                        FontFamily = "Inter",
                        FontSize = 12,
                        TextColor = Color.FromArgb("#FFD700"),
                        VerticalOptions = LayoutOptions.Center,
                    });

                stack.Children.Add(header);

                // Детали
                var detailParts = new List<string>();
                if (!string.IsNullOrEmpty(c.Developer))
                    detailParts.Add(c.Developer);
                if (!string.IsNullOrEmpty(c.LatestVersion))
                    detailParts.Add($"v{c.LatestVersion}");
                if (!string.IsNullOrEmpty(c.Platform))
                    detailParts.Add(c.Platform);

                stack.Children.Add(new Label
                {
                    Text = string.Join(" · ", detailParts),
                    FontFamily = "Inter",
                    FontSize = 10,
                    TextColor = Color.FromArgb("#999"),
                });

                // Цена
                stack.Children.Add(new Label
                {
                    Text = $"{c.Pricing} ${c.PriceUsd:F2}",
                    FontFamily = "Inter",
                    FontSize = 10,
                    TextColor = Color.FromArgb("#4CAF50"),
                });

                // Фичи
                if (!string.IsNullOrEmpty(c.Strengths))
                    stack.Children.Add(new Label
                    {
                        Text = $"✓ {c.Strengths}",
                        FontFamily = "Inter",
                        FontSize = 10,
                        TextColor = Color.FromArgb("#81C784"),
                    });

                if (!string.IsNullOrEmpty(c.Weaknesses))
                    stack.Children.Add(new Label
                    {
                        Text = $"✗ {c.Weaknesses}",
                        FontFamily = "Inter",
                        FontSize = 10,
                        TextColor = Color.FromArgb("#E57373"),
                    });

                card.Content = stack;
                CompListStack.Children.Add(card);
            }

            // Последние изменения
            if (changes.Count > 0)
            {
                var sep = new Label
                {
                    Text = "📌 Последние изменения",
                    FontFamily = "InterSemiBold",
                    FontSize = 14,
                    TextColor = Colors.White,
                    Margin = new Thickness(0, 8, 0, 0),
                };
                CompListStack.Children.Add(sep);

                foreach (var ch in changes)
                {
                    var comp = all.FirstOrDefault(c => c.Id == ch.CompetitorId);
                    var name = comp?.Name ?? "?";

                    CompListStack.Children.Add(new Label
                    {
                        Text = $"[{ch.DetectedAt:dd.MM HH:mm}] {name}: {ch.ChangeType} «{ch.OldValue}» → «{ch.NewValue}»",
                        FontFamily = "Inter",
                        FontSize = 11,
                        TextColor = Color.FromArgb("#B0B0B0"),
                    });
                }
            }
        }
        catch (Exception ex)
        {
            LblCompSummary.Text = $"❌ {ex.Message}";
            LblCompStatus.Text = "Ошибка загрузки";
        }
        finally
        {
            BtnRefreshCompetitors.IsEnabled = true;
        }
    }

    // ─── Мониторинг автопрома ─────────────────────────────────────

    private string _autoIndustryFilter = "all";

    private async Task LoadAutoIndustryAsync()
    {
        LblAutoStatus.Text = "Загрузка...";
        BtnRefreshAutoIndustry.IsEnabled = false;

        try
        {
            var svc = new AutoIndustryService();
            List<AutoIndustryNews> items;

            if (_autoIndustryFilter == "all")
                items = await svc.GetAllAsync(100);
            else
                items = await svc.GetByCategoryAsync(_autoIndustryFilter, 50);

            var total = await svc.CountAsync();
            var critical = items.Count(n => n.Relevance == "critical");
            var high = items.Count(n => n.Relevance == "high");
            var unprocessed = items.Count(n => !n.IsProcessed);

            LblAutoSummary.Text =
                $"Всего событий в базе: {total}\n" +
                $"🔴 Критических: {critical} | 🟡 Важных: {high} | ⚪ Не обработано: {unprocessed}\n" +
                $"Последняя проверка: {AutoIndustryMonitor.Instance.LastCheckAt:dd.MM.yyyy HH:mm}";

            LblAutoStatus.Text = $"Показано: {items.Count} (фильтр: {GetAutoFilterLabel()})";

            // Рендер списка
            AutoNewsListStack.Children.Clear();
            foreach (var item in items.Take(30))
            {
                var relevanceColor = item.Relevance switch
                {
                    "critical" => "#FF1744",
                    "high" => "#FF9100",
                    "medium" => "#2196F3",
                    _ => "#757575"
                };

                var categoryIcon = item.Category switch
                {
                    "recall" => "🚨",
                    "standard" => "📏",
                    "protocol" => "🔌",
                    "ecu" => "🧠",
                    "error_codes" => "🔢",
                    "new_model" => "🚙",
                    "regulation" => "📜",
                    _ => "📌"
                };

                var processedIcon = item.IsProcessed ? "✓" : "○";

                var border = new Border
                {
                    Stroke = Color.FromArgb("#3A3A3A"),
                    StrokeThickness = 1,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                    BackgroundColor = Color.FromArgb("#2D2D2D"),
                    Padding = new Thickness(12, 8),
                };

                var stack = new VerticalStackLayout { Spacing = 4 };

                // Заголовок
                var headerLayout = new HorizontalStackLayout { Spacing = 6 };

                var marker = new Label
                {
                    Text = $"{processedIcon} {categoryIcon}",
                    FontFamily = "InterSemiBold",
                    FontSize = 12,
                    TextColor = Color.FromArgb(relevanceColor),
                    VerticalOptions = LayoutOptions.Center,
                };

                var titleLabel = new Label
                {
                    Text = item.Title,
                    FontFamily = "InterSemiBold",
                    FontSize = 13,
                    TextColor = Color.FromArgb("#E0E0E0"),
                    LineBreakMode = LineBreakMode.TailTruncation,
                    HorizontalOptions = LayoutOptions.FillAndExpand,
                };

                headerLayout.Children.Add(marker);
                headerLayout.Children.Add(titleLabel);
                stack.Children.Add(headerLayout);

                // Сниппет
                if (!string.IsNullOrEmpty(item.Summary))
                {
                    stack.Children.Add(new Label
                    {
                        Text = item.Summary,
                        FontFamily = "Inter",
                        FontSize = 11,
                        TextColor = Color.FromArgb("#999999"),
                        LineBreakMode = LineBreakMode.TailTruncation,
                        MaxLines = 2,
                        Margin = new Thickness(24, 0, 0, 0),
                    });
                }

                // Мета
                stack.Children.Add(new Label
                {
                    Text = $"{item.Source} | {item.DetectedAt:dd.MM.yyyy} | {GetRelevanceLabel(item.Relevance)}",
                    FontFamily = "Inter",
                    FontSize = 10,
                    TextColor = Color.FromArgb("#666666"),
                    Margin = new Thickness(24, 0, 0, 0),
                });

                border.Content = stack;
                AutoNewsListStack.Children.Add(border);
            }
        }
        catch (Exception ex)
        {
            LblAutoStatus.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            BtnRefreshAutoIndustry.IsEnabled = true;
        }
    }

    private async void OnRefreshAutoIndustry(object? s, EventArgs e)
    {
        BtnRefreshAutoIndustry.IsEnabled = false;
        LblAutoStatus.Text = "Поиск событий автопрома...";
        try
        {
            var result = await AutoIndustryMonitor.Instance.ForceRunAsync();
            LblAutoStatus.Text = result.Replace('\n', ' ');
        }
        catch (Exception ex)
        {
            LblAutoStatus.Text = $"Ошибка: {ex.Message}";
        }
        finally
        {
            await LoadAutoIndustryAsync();
        }
    }

    private async void OnAutoReport(object? s, EventArgs e)
    {
        BtnAutoReport.IsEnabled = false;
        try
        {
            var svc = new AutoIndustryService();
            var report = await svc.GenerateReportAsync();

            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"autoprom_report_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt");
            await File.WriteAllTextAsync(path, report, Encoding.UTF8);

            await DisplayAlertAsync("Отчёт сохранён",
                $"Файл: {Path.GetFileName(path)}", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
        finally
        {
            BtnAutoReport.IsEnabled = true;
        }
    }

    private async void OnAutoIndustryFilter(object? s, EventArgs e)
    {
        if (s is not Button btn) return;

        var allButtons = new[] { BtnAutoFilterAll, BtnAutoFilterRecall, BtnAutoFilterStandards, BtnAutoFilterModels };
        var filterMap = new Dictionary<Button, string>
        {
            [BtnAutoFilterAll] = "all",
            [BtnAutoFilterRecall] = "recall",
            [BtnAutoFilterStandards] = "standard",
            [BtnAutoFilterModels] = "new_model",
        };

        _autoIndustryFilter = filterMap.GetValueOrDefault(btn, "all");

        foreach (var b in allButtons)
        {
            b.BackgroundColor = b == btn
                ? Color.FromArgb("#2196F3")
                : Color.FromArgb("#2D2D2D");
            b.TextColor = b == btn
                ? Colors.White
                : Color.FromArgb("#B0B0B0");
            b.FontFamily = b == btn ? "InterSemiBold" : "Inter";
        }

        await LoadAutoIndustryAsync();
    }

    private static string GetAutoFilterLabel() => "Все";

    private static string GetRelevanceLabel(string relevance) => relevance switch
    {
        "critical" => "🔴 Крит.",
        "high" => "🟡 Важно",
        "medium" => "🔵 Среднее",
        "low" => "⚪ Низкое",
        _ => relevance
    };

    // ────────────────────────────────────────────────────────────────
    //  Вкладка: Кодирование
    // ────────────────────────────────────────────────────────────────

    private async void OnCodingRefreshClicked(object? sender, EventArgs e)
    {
        try
        {
            var coding = new CodingService();
            var count = await coding.GetFeatureCountAsync();
            LblCodingTotalFeatures.Text = count.ToString();

            var sessions = await coding.GetSessionsAsync(30);
            LblCodingTotalSessions.Text = sessions.Count.ToString();
            CodingSessionsList.ItemsSource = sessions;
        }
        catch (Exception ex)
        {
            await DisplayAlert(ex.Message, "", "OK");
        }
    }

    private async void OnCodingSeedClicked(object? sender, EventArgs e)
    {
        try
        {
            var coding = new CodingService();
            var added = await coding.SeedAsync();

            if (added > 0)
            {
                var count = await coding.GetFeatureCountAsync();
                LblCodingTotalFeatures.Text = count.ToString();
                await DisplayAlert("Сидирование", $"Добавлено {added} скрытых функций. Всего: {count}.", "OK");
            }
            else
            {
                await DisplayAlert("Сидирование", "База уже заполнена.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Ошибка", ex.Message, "OK");
        }
    }
}

/// <summary>
/// Запись для отображения в браузере БД (все разделы).
/// </summary>
public class BrowserEntry
{
    public string Icon { get; set; } = "";
    public string Code { get; set; } = "";
    public string Section { get; set; } = "";
    public Color BadgeColor { get; set; } = Colors.Gray;
    public string Title { get; set; } = "";
    public string Details { get; set; } = "";
    public string Meta { get; set; } = "";

    // Для редактирования
    public long Id { get; set; }
    public int SectionIndex { get; set; }
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public string FullDescription { get; set; } = "";
}

/// <summary>
/// Модель элемента фидбека для отображения в списке.
/// </summary>
public class FeedbackItem
{
    public string ErrorCode { get; set; } = "";
    public string CarBrand { get; set; } = "";
    public string Comment { get; set; } = "";
    public string RatingIcon { get; set; } = "👍";
    public string Timestamp { get; set; } = "";
}
