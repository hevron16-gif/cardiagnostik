using CarDiagnosticApp.Data;
using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;

namespace CarDiagnosticApp.Pages;

public partial class KnowledgeBasePage : ContentPage
{
    private readonly List<KnowledgeItem> _allItems;
    private readonly ApiService _api = IPlatformApplication.Current!.Services.GetRequiredService<ApiService>();
    private string? _selectedBrand;

    public KnowledgeBasePage()
    {
        InitializeComponent();
        _allItems = OBD2Codes.All;
        LabelUpdated.Text = $"Обновлено: {OBD2Codes.LastUpdated:dd MMMM yyyy}";
        ShowItems(_allItems);
        _ = LoadBrandsAsync();
    }

    /// <summary>
    /// Загружает список марок с сервера в Picker.
    /// </summary>
    private async Task LoadBrandsAsync()
    {
        try
        {
            var brands = await _api.GetCarBrands();
            if (brands is { Count: > 0 })
            {
                var items = new List<string> { "Все марки" };
                items.AddRange(brands.Select(b => b.brand));
                BrandPicker.ItemsSource = items;
                BrandPicker.SelectedIndex = 0;
            }
        }
        catch
        {
            // Нет связи — Picker остаётся пустым, все коды видны
        }
    }

    /// <summary>
    /// Фильтр по марке: оставляет коды, характерные для выбранной марки.
    /// Если марка неизвестна или выбрано «Все» — показывает все коды.
    /// </summary>
    private void OnBrandSelected(object? sender, EventArgs e)
    {
        _selectedBrand = BrandPicker.SelectedIndex > 0
            ? BrandPicker.SelectedItem?.ToString()
            : null;

        ApplyFilters();
    }

    /// <summary>
    /// Фильтрация по поисковому запросу.
    /// </summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    /// <summary>
    /// Применяет оба фильтра: поиск + марка.
    /// </summary>
    private async void ApplyFilters()
    {
        var items = _allItems.AsEnumerable();

        // 1. Фильтр по марке
        if (!string.IsNullOrEmpty(_selectedBrand))
        {
            var brandCodes = BrandCodeInfo.GetCodesForBrand(_selectedBrand);
            if (brandCodes != null)
            {
                items = items.Where(i => brandCodes.Contains(i.Code));
            }
            // Если марка не в словаре — показываем все (ODB2 универсальны)
        }

        // 2. Поисковый фильтр
        var q = (SearchBar.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(q))
        {
            items = items.Where(i =>
                i.Code.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                i.Causes.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        var list = items.ToList();

        // 3. Не нашли локально — ищем в офлайн-справочнике на 12 000+ кодов
        if (list.Count == 0 && IsDtcCode(q))
        {
            var found = await App.Dtc.GetAsync(q);
            if (found != null)
                list = new List<KnowledgeItem> { found };
        }

        ShowItems(list);
    }

    /// <summary>Похоже ли на код неисправности (P0300, B0123, C1ABC, U0100).</summary>
    private static bool IsDtcCode(string s)
        => System.Text.RegularExpressions.Regex.IsMatch(s, @"^[PBUCpbuc]\d[0-9A-Fa-f]{3}$");

    /// <summary>
    /// Раскрытие/сворачивание карточки с деталями.
    /// </summary>
    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is KnowledgeItem item)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    /// <summary>
    /// Группирует и отображает элементы.
    /// </summary>
    private void ShowItems(List<KnowledgeItem> items)
    {
        var grouped = items
            .GroupBy(i => i.Category)
            .OrderBy(g => g.Key)
            .ToList();

        CodeList.ItemsSource = grouped;
        LabelCount.Text = $"Записей в базе: {items.Count}";
    }
}
