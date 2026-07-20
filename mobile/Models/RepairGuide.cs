using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Интерактивное ремонтное руководство.
/// Привязано к коду ошибки и марке/модели авто.
/// Сохраняется в repair_guides таблице.
/// </summary>
public class RepairGuide
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки (P0300, P0420… или "generic").</summary>
    [Indexed]
    public string ErrorCode { get; set; } = "";

    /// <summary>Марка (LADA, УАЗ…). null = универсальное.</summary>
    public string? Brand { get; set; }

    /// <summary>Модель. null = для всей марки.</summary>
    public string? ModelName { get; set; }

    /// <summary>Двигатель (ВАЗ-21129, ЗМЗ-40906…). null = любой.</summary>
    public string? EngineCode { get; set; }

    /// <summary>Заголовок руководства.</summary>
    public string Title { get; set; } = "";

    /// <summary>Краткое описание проблемы.</summary>
    public string Description { get; set; } = "";

    /// <summary>Сложность: easy, medium, hard, expert.</summary>
    public string Difficulty { get; set; } = "medium";

    /// <summary>Примерное время (минут).</summary>
    public int EstimatedMinutes { get; set; }

    /// <summary>Инструменты через запятую (кратко).</summary>
    public string ToolsRequired { get; set; } = "";

    /// <summary>Детальный список инструментов с размерами: ключ на 10, динамометрический ключ 5-25 Н·м, ...</summary>
    public string DetailedTools { get; set; } = "";

    /// <summary>Моменты затяжки для основных соединений (Н·м). Формат: "Свечи: 25-30 Н·м; Колёсные болты: 90-110 Н·м; ..."</summary>
    public string TorqueSpecs { get; set; } = "";

    /// <summary>Запчасти через запятую.</summary>
    public string PartsRequired { get; set; } = "";

    /// <summary>Техника безопасности.</summary>
    public string SafetyNotes { get; set; } = "";

    /// <summary>Симптомы (для поиска).</summary>
    public string Symptoms { get; set; } = "";

    /// <summary>Вероятные причины через запятую.</summary>
    public string PossibleCauses { get; set; } = "";

    /// <summary>Источник руководства.</summary>
    public string Source { get; set; } = "";

    /// <summary>URL источника.</summary>
    public string SourceUrl { get; set; } = "";

    public int ViewCount { get; set; }
    public int CompletionCount { get; set; }
    public int HelpfulCount { get; set; }
    public int NotHelpfulCount { get; set; }

    public double Rating => HelpfulCount + NotHelpfulCount > 0
        ? (double)HelpfulCount / (HelpfulCount + NotHelpfulCount) * 5.0
        : 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"[{Difficulty}] {ErrorCode} {Brand} {ModelName}: {Title}";
}
