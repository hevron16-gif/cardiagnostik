using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Одна запись в базе знаний OBD2. Поддерживает раскрытие деталей.
/// </summary>
public class KnowledgeItem : INotifyPropertyChanged
{
    public string Code { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string Causes { get; set; } = "";
    public string Symptoms { get; set; } = "";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public string CategoryShort
    {
        get
        {
            if (string.IsNullOrEmpty(Category)) return "";
            var idx = Category.IndexOf("(P");
            return idx >= 0 ? Category[(idx + 1)..] : Category;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
