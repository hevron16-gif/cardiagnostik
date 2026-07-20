using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Шаг ремонтного руководства.
/// Может быть обычным шагом или точкой принятия решения (ветвление).
/// Сохраняется в repair_steps таблице.
/// </summary>
public class RepairStep
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>ID родительского руководства.</summary>
    [Indexed]
    public int GuideId { get; set; }

    /// <summary>Номер шага (1, 2, 3…).</summary>
    public int StepNumber { get; set; }

    /// <summary>Краткий заголовок шага.</summary>
    public string Title { get; set; } = "";

    /// <summary>Подробная инструкция.</summary>
    public string Instruction { get; set; } = "";

    /// <summary>На что обратить внимание / что искать визуально.</summary>
    public string ImageHint { get; set; } = "";

    /// <summary>Ожидаемый результат после выполнения.</summary>
    public string ExpectedResult { get; set; } = "";

    /// <summary>Это точка принятия решения (да/нет)?</summary>
    public bool IsDecisionPoint { get; set; }

    /// <summary>Вопрос для решения (если IsDecisionPoint).</summary>
    public string DecisionQuestion { get; set; } = "";

    /// <summary>Номер шага при ответе «Да».</summary>
    public int? NextOnSuccess { get; set; }

    /// <summary>Номер шага при ответе «Нет».</summary>
    public int? NextOnFailure { get; set; }

    /// <summary>Примерное время (мин).</summary>
    public int EstimatedMinutes { get; set; }

    /// <summary>Предупреждения (осторожно, горячо, высокое напряжение…).</summary>
    public string WarningNotes { get; set; } = "";

    /// <summary>Ссылка на изображение/схему (локальный путь или URL).</summary>
    public string? ImageUrl { get; set; }

    /// <summary>Момент затяжки для данного шага в Н·м (напр. "25-30 Н·м"). null если шаг не требует затяжки.</summary>
    public string? TorqueNm { get; set; }

    /// <summary>Порядок сортировки при нескольких изображениях.</summary>
    public int SortOrder { get; set; }

    public override string ToString() =>
        IsDecisionPoint
            ? $"[Шаг {StepNumber}] РЕШЕНИЕ: {DecisionQuestion}"
            : $"[Шаг {StepNumber}] {Title}";
}
