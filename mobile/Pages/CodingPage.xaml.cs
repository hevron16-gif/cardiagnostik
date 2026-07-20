using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;

namespace CarDiagnosticApp.Pages;

/// <summary>
/// Страница кодирования и активации скрытых функций автомобиля.
/// Позволяет просматривать каталог функций по брендам, активировать/деактивировать.
/// </summary>
public partial class CodingPage : ContentPage
{
    private readonly CodingService _coding;
    private List<HiddenFeature> _allFeatures = new();
    private string? _currentBrand;
    private string? _currentModel;
    private string _searchText = "";

    public CodingPage()
    {
        InitializeComponent();
        _coding = new CodingService();
    }

    /// <summary>
    /// Инициализация с данными авто.
    /// </summary>
    public CodingPage(string brand, string? model = null)
    {
        InitializeComponent();
        _coding = new CodingService();
        _currentBrand = brand;
        _currentModel = model;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadDataAsync();
    }

    // ──────────────── Загрузка данных ────────────────

    public async Task LoadDataAsync()
    {
        try
        {
            LabelStatus.Text = "Загрузка каталога скрытых функций...";

            // Seed если база пуста
            await _coding.SeedAsync();

            // Загружаем все функции
            _allFeatures = await _coding.GetFeaturesAsync();

            // Строим фильтры по брендам
            BuildBrandFilters();

            // Применяем текущие фильтры
            ApplyFilters();

            var count = _allFeatures.Count;
            LabelStatus.Text = $"Загружено {count} функций. {(string.IsNullOrEmpty(_currentBrand) ? "Все бренды" : _currentBrand)} {(string.IsNullOrEmpty(_currentModel) ? "" : _currentModel)}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodingPage] Load error: {ex.Message}");
            LabelStatus.Text = "Ошибка загрузки";
        }
    }

    // ──────────────── Фильтры брендов ────────────────

    private void BuildBrandFilters()
    {
        BrandFilters.Children.Clear();

        var brands = _allFeatures
            .Where(f => f.Brand != null)
            .Select(f => f.Brand!)
            .Distinct()
            .OrderBy(b => b)
            .ToList();

        // Кнопка «Все»
        AddBrandChip("Все", null, _currentBrand == null);

        // Авто из текущего контекста — выделяем
        if (!string.IsNullOrEmpty(_currentBrand) && brands.Contains(_currentBrand))
        {
            AddBrandChip(_currentBrand, _currentBrand, true);
            brands.Remove(_currentBrand);
        }

        foreach (var brand in brands)
        {
            bool selected = _currentBrand == brand;
            AddBrandChip(brand, brand, selected);
        }
    }

    private void AddBrandChip(string label, string? brandFilter, bool isSelected)
    {
        var chip = new Border
        {
            Padding = new Thickness(14, 8),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            BackgroundColor = isSelected ? Color.FromArgb("#1565C0") : Color.FromArgb("#2A2A2A"),
            Content = new Label
            {
                Text = label,
                FontSize = 13,
                FontFamily = "InterSemiBold",
                TextColor = isSelected ? Colors.White : Color.FromArgb("#B0B0B0"),
                VerticalOptions = LayoutOptions.Center,
            },
        };

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            _currentBrand = brandFilter;
            BuildBrandFilters();
            ApplyFilters();
        };
        chip.GestureRecognizers.Add(tap);

        BrandFilters.Children.Add(chip);
    }

    // ──────────────── Поиск ────────────────

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchText = e.NewTextValue?.Trim() ?? "";
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var filtered = _allFeatures.AsEnumerable();

        if (!string.IsNullOrEmpty(_currentBrand))
            filtered = filtered.Where(f => f.Brand == null || f.Brand == _currentBrand);

        if (!string.IsNullOrEmpty(_searchText))
        {
            var s = _searchText.ToLower();
            filtered = filtered.Where(f =>
                f.FeatureName.ToLower().Contains(s) ||
                f.Description.ToLower().Contains(s) ||
                f.Category.ToLower().Contains(s));
        }

        FeaturesList.ItemsSource = null;
        FeaturesList.ItemsSource = filtered
            .OrderBy(f => f.Category)
            .ThenBy(f => f.FeatureName)
            .ToList();
    }

    // ──────────────── Активация ────────────────

    private async void OnActivateTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not HiddenFeature feature) return;

        // Проверка безопасности
        if (feature.RequiresSecurity)
        {
            bool proceed = await DisplayAlert(
                "🔐 Защищённая функция",
                $"«{feature.FeatureName}» требует доступа к защищённой зоне блока.\n\nНекорректное кодирование может привести к сбоям.\n\nПродолжить?",
                "Да, продолжить", "Отмена");

            if (!proceed) return;
        }

        bool confirm = await DisplayAlert(
            "Активация функции",
            $"Активировать «{feature.FeatureName}»?\n\n{feature.Description}",
            "Активировать", "Отмена");

        if (!confirm) return;

        LabelStatus.Text = $"Выполняется активация «{feature.FeatureName}»...";
        var (success, message) = await _coding.ActivateFeatureAsync(
            feature, _currentBrand ?? "", _currentModel);

        LabelStatus.Text = message;

        await DisplayAlert(success ? "✅ Успех" : "❌ Ошибка", message, "OK");
        await LoadDataAsync();
    }

    // ──────────────── Деактивация ────────────────

    private async void OnDeactivateTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not HiddenFeature feature) return;

        bool confirm = await DisplayAlert(
            "Деактивация функции",
            $"Деактивировать «{feature.FeatureName}»?\n\nФункция вернётся к заводским настройкам.",
            "Деактивировать", "Отмена");

        if (!confirm) return;

        LabelStatus.Text = $"Выполняется деактивация «{feature.FeatureName}»...";
        var (success, message) = await _coding.DeactivateFeatureAsync(
            feature, _currentBrand ?? "", _currentModel);

        LabelStatus.Text = message;

        await DisplayAlert(success ? "✅ Успех" : "❌ Ошибка", message, "OK");
        await LoadDataAsync();
    }

    // ──────────────── Подгрузка ────────────────

    private void OnLoadMore(object? sender, EventArgs e)
    {
        // Страница загружает всё сразу — доп. подгрузка не требуется
    }
}
