using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;

namespace CarDiagnosticApp.Pages;

public partial class RepairGuidePage : ContentPage
{
    private readonly RepairGuideService _guideService = new();
    private RepairGuide? _guide;
    private List<RepairStep> _steps = [];
    private int _currentIndex = -1;
    private HashSet<int> _completedSteps = [];
    private bool _isCompleted;

    // Цвета сложности
    private static readonly Dictionary<string, Color> DifficultyColors = new()
    {
        ["easy"] = Colors.DarkGreen,
        ["medium"] = Color.FromArgb("#E65100"),
        ["hard"] = Color.FromArgb("#BF360C"),
        ["expert"] = Color.FromArgb("#880E4F"),
    };

    private static readonly Dictionary<string, string> DifficultyNames = new()
    {
        ["easy"] = "Лёгкий",
        ["medium"] = "Средний",
        ["hard"] = "Сложный",
        ["expert"] = "Эксперт",
    };

    public RepairGuidePage()
    {
        InitializeComponent();
    }

    /// <summary>Загружает руководство по ID.</summary>
    public async Task LoadGuideAsync(int guideId)
    {
        _guide = await _guideService.GetGuideByIdAsync(guideId);
        if (_guide == null)
        {
            await DisplayAlert("Ошибка", "Руководство не найдено.", "OK");
            await Navigation.PopAsync();
            return;
        }

        _steps = await _guideService.GetStepsAsync(guideId);
        if (_steps.Count == 0)
        {
            await DisplayAlert("Ошибка", "В руководстве нет шагов.", "OK");
            await Navigation.PopAsync();
            return;
        }

        await _guideService.IncrementViewAsync(guideId);

        RenderHeader();
        GoToStep(0);
    }

    /// <summary>Быстрая загрузка первого подходящего руководства.</summary>
    public async Task<bool> LoadBestGuideAsync(string errorCode, string? brand, string? model)
    {
        var guides = await _guideService.FindGuidesAsync(errorCode, brand, model);
        if (guides.Count == 0) return false;

        return await LoadGuideByIdAsync(guides[0].Id);
    }

    private async Task<bool> LoadGuideByIdAsync(int id)
    {
        await LoadGuideAsync(id);
        return _guide != null;
    }

    // ────────────────── Шапка ──────────────────

    private void RenderHeader()
    {
        if (_guide == null) return;

        ErrorCodeLabel.Text = _guide.ErrorCode;
        CarLabel.Text = _guide.Brand == null
            ? "Универсальное"
            : $"{_guide.Brand}{(string.IsNullOrEmpty(_guide.ModelName) ? "" : $" {_guide.ModelName}")}";

        TitleLabel.Text = _guide.Title;

        // Сложность
        var diffKey = _guide.Difficulty.ToLowerInvariant();
        DifficultyBadge.BackgroundColor = DifficultyColors.GetValueOrDefault(diffKey, Colors.Gray);
        DifficultyLabel.Text = DifficultyNames.GetValueOrDefault(diffKey, diffKey);

        TimeLabel.Text = $"≈ {_guide.EstimatedMinutes} мин";

        ProgressBar.Progress = 0;
        UpdateProgress();
    }

    // ────────────────── Навигация по шагам ──────────────────

    private void GoToStep(int index)
    {
        if (_steps.Count == 0) return;

        if (index >= _steps.Count)
        {
            ShowCompletion();
            return;
        }

        _currentIndex = Math.Clamp(index, 0, _steps.Count - 1);
        var step = _steps[_currentIndex];

        // Номер и заголовок
        StepNumberLabel.Text = $"Шаг {step.StepNumber}";
        StepTitleLabel.Text = step.IsDecisionPoint ? "❓ ПРИНЯТИЕ РЕШЕНИЯ" : step.Title;

        // Инструкция
        InstructionLabel.Text = step.Instruction;

        // Предупреждение
        if (!string.IsNullOrWhiteSpace(step.WarningNotes))
        {
            WarningBox.IsVisible = true;
            WarningLabel.Text = step.WarningNotes;
        }
        else
        {
            WarningBox.IsVisible = false;
        }

        // Визуальная подсказка
        if (!string.IsNullOrWhiteSpace(step.ImageHint))
        {
            HintBox.IsVisible = true;
            HintLabel.Text = step.ImageHint;
            ImagePlaceholder.IsVisible = true;
        }
        else
        {
            HintBox.IsVisible = false;
            ImagePlaceholder.IsVisible = false;
        }

        // Ожидаемый результат
        if (!string.IsNullOrWhiteSpace(step.ExpectedResult))
        {
            ResultBox.IsVisible = true;
            ResultLabel.Text = step.ExpectedResult;
        }
        else
        {
            ResultBox.IsVisible = false;
        }

        // Кнопки решений
        DecisionButtons.IsVisible = step.IsDecisionPoint;
        if (step.IsDecisionPoint)
        {
            InstructionLabel.Text = $"❓ {step.DecisionQuestion}\n\n{step.Instruction}";
        }

        // Нижние кнопки
        BtnPrev.IsEnabled = _currentIndex > 0;
        BtnPrev.Opacity = _currentIndex > 0 ? 1.0 : 0.4;

        if (step.IsDecisionPoint)
        {
            BtnNext.IsVisible = false;
        }
        else
        {
            BtnNext.IsVisible = true;
            BtnNext.Text = _currentIndex >= _steps.Count - 1 ? "🏁 Завершить" : "Далее ▶";
        }

        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_steps.Count == 0) return;
        var done = _currentIndex < 0 ? 0 : _currentIndex;
        ProgressBar.Progress = (double)done / _steps.Count;
        ProgressLabel.Text = $"{done}/{_steps.Count}";
    }

    // ────────────────── Обработчики кнопок ──────────────────

    private async void OnNextClicked(object? sender, EventArgs e)
    {
        if (_currentIndex < 0) return;

        // Отмечаем текущий шаг выполненным
        if (_currentIndex < _steps.Count)
        {
            _completedSteps.Add(_steps[_currentIndex].StepNumber);
        }

        // Следующий шаг
        GoToStep(_currentIndex + 1);
    }

    private void OnPrevClicked(object? sender, EventArgs e)
    {
        if (_currentIndex <= 0) return;
        GoToStep(_currentIndex - 1);
    }

    private void OnSuccessClicked(object? sender, EventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _steps.Count) return;

        _completedSteps.Add(_steps[_currentIndex].StepNumber);
        var step = _steps[_currentIndex];

        if (step.NextOnSuccess.HasValue)
        {
            var targetIndex = _steps.FindIndex(s => s.StepNumber == step.NextOnSuccess.Value);
            if (targetIndex >= 0)
                GoToStep(targetIndex);
            else
                GoToStep(_currentIndex + 1);
        }
        else
        {
            GoToStep(_currentIndex + 1);
        }
    }

    private void OnFailureClicked(object? sender, EventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _steps.Count) return;

        _completedSteps.Add(_steps[_currentIndex].StepNumber);
        var step = _steps[_currentIndex];

        if (step.NextOnFailure.HasValue)
        {
            var targetIndex = _steps.FindIndex(s => s.StepNumber == step.NextOnFailure.Value);
            if (targetIndex >= 0)
                GoToStep(targetIndex);
            else
                GoToStep(_currentIndex + 1);
        }
        else
        {
            GoToStep(_currentIndex + 1);
        }
    }

    // ────────────────── Завершение ──────────────────

    private async void ShowCompletion()
    {
        if (_isCompleted) return;
        _isCompleted = true;

        ProgressBar.Progress = 1.0;
        ProgressLabel.Text = $"{_steps.Count}/{_steps.Count}";

        if (_guide != null)
            await _guideService.RecordCompletionAsync(_guide.Id);

        // Показываем экран завершения внутри текущей страницы
        StepNumberLabel.Text = "✅";
        StepTitleLabel.Text = "Руководство пройдено!";
        InstructionLabel.Text = $"Вы успешно прошли все {_steps.Count} шагов руководства:\n\n«{_guide?.Title}»\n\nЗатраченное время: ≈ {_guide?.EstimatedMinutes} мин\nВыполнено шагов: {_completedSteps.Count} из {_steps.Count}";

        WarningBox.IsVisible = false;
        HintBox.IsVisible = false;
        ResultBox.IsVisible = false;
        DecisionButtons.IsVisible = false;
        ImagePlaceholder.IsVisible = false;

        BtnPrev.IsVisible = false;
        BtnNext.Text = "👍 Завершить";
        BtnNext.BackgroundColor = Color.FromArgb("#2E7D32");

        // Переопределяем обработчик кнопки «Завершить»
        BtnNext.Clicked -= OnNextClicked;
        BtnNext.Clicked += OnFinishClicked;
    }

    private async void OnFinishClicked(object? sender, EventArgs e)
    {
        var helpful = await DisplayAlert("Обратная связь", "Руководство было полезным?", "👍 Да", "👎 Нет");
        if (_guide != null)
            await _guideService.RecordFeedbackAsync(_guide.Id, helpful);

        await Navigation.PopAsync();
    }

    // ────────────────── Доп. информация ──────────────────

    /// <summary>Показывает сводку: инструменты, запчасти, безопасность.</summary>
    public async Task ShowPreCheckAsync()
    {
        if (_guide == null) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🔧 ИНСТРУМЕНТЫ: {_guide.ToolsRequired}");
        sb.AppendLine();
        sb.AppendLine($"🛒 ЗАПЧАСТИ: {_guide.PartsRequired}");
        sb.AppendLine();
        sb.AppendLine($"⚠️ БЕЗОПАСНОСТЬ: {_guide.SafetyNotes}");
        sb.AppendLine();
        sb.AppendLine($"⏱ Время: ≈ {_guide.EstimatedMinutes} мин | Сложность: {_guide.Difficulty}");

        await DisplayAlert("Перед началом", sb.ToString(), "Приступить ▶");
    }
}
