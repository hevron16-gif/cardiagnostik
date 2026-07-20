using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Обновление существующей модели российского авто.
/// Сохраняется в russian_auto_updates таблице.
/// </summary>
public class RussianAutoModelUpdate
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>ID записи в russian_auto_models (FK, опционально).</summary>
    public int? ModelId { get; set; }

    /// <summary>Марка.</summary>
    public string Brand { get; set; } = "";

    /// <summary>Модель.</summary>
    public string ModelName { get; set; } = "";

    /// <summary>
    /// Тип обновления:
    /// "restyling" — рестайлинг/фейслифт
    /// "new_generation" — новое поколение
    /// "new_engine" — новый двигатель/трансмиссия
    /// "new_trim" — новая комплектация
    /// "tech_update" — техническое обновление (новая платформа, ЭБУ, электроника)
    /// "discontinued" — снятие с производства
    /// "safety_update" — обновление систем безопасности
    /// "special_edition" — спецверсия
    /// "price_change" — существенное изменение цены/позиционирования
    /// </summary>
    public string UpdateType { get; set; } = "restyling";

    /// <summary>Описание изменений.</summary>
    public string Description { get; set; } = "";

    /// <summary>Год обновления (если указан).</summary>
    public int? Year { get; set; }

    /// <summary>Источник.</summary>
    public string Source { get; set; } = "";

    /// <summary>URL источника.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Влияет на диагностику (новый протокол, ЭБУ).</summary>
    public bool AffectsDiagnostics { get; set; }

    /// <summary>Обработана ли запись.</summary>
    public bool IsProcessed { get; set; }

    /// <summary>Дата обнаружения.</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"[{UpdateType}] {Brand} {ModelName}{(Year.HasValue ? $" ({Year})" : "")}: {Description[..Math.Min(Description.Length, 80)]}";
}
