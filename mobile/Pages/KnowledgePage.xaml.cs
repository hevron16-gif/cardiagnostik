using CarDiagnosticApp.Data;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Pages;

public partial class KnowledgePage : ContentPage
{
    private List<KnowledgeItem> _allItems;

    public KnowledgePage()
    {
        InitializeComponent();
        _allItems = OBD2Codes.All;
        RefreshList(_allItems);
    }

    /// <summary>
    /// Фильтрация по поисковому запросу (код или часть описания).
    /// </summary>
    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = e.NewTextValue?.Trim() ?? "";

        if (string.IsNullOrEmpty(query))
        {
            RefreshList(_allItems);
            return;
        }

        var filtered = _allItems
            .Where(i => i.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || i.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || i.Causes.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        RefreshList(filtered);
    }

    /// <summary>
    /// Раскрытие/скрытие деталей по тапу.
    /// </summary>
    private void OnItemTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is KnowledgeItem item)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    /// <summary>
    /// Группирует и отображает список.
    /// </summary>
    private void RefreshList(List<KnowledgeItem> items)
    {
        var grouped = items
            .GroupBy(i => i.Category)
            .OrderBy(g => g.Key)
            .ToList();

        KnowledgeList.ItemsSource = grouped;
        LabelResultCount.Text = $"Найдено кодов: {items.Count}";
    }
}
