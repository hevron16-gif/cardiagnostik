using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Новость/событие автопрома, влияющее на диагностику.
/// Сохраняется в auto_industry_news таблице.
/// </summary>
public class AutoIndustryNews
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Заголовок новости/события.</summary>
    public string Title { get; set; } = "";

    /// <summary>
    /// Категория:
    /// "new_model" — новая модель/поколение авто
    /// "recall" — отзывная кампания
    /// "standard" — изменение OBD2/EOBD стандарта
    /// "protocol" — новый диагностический протокол
    /// "ecu" — новый блок управления / ЭБУ
    /// "error_codes" — новые коды ошибок производителя
    /// "regulation" — изменения в законодательстве
    /// "other" — прочее
    /// </summary>
    public string Category { get; set; } = "other";

    /// <summary>Источник (домен).</summary>
    public string Source { get; set; } = "";

    /// <summary>URL источника.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Краткое содержание (первые 2-3 предложения).</summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// Важность для нашего приложения:
    /// "critical" — немедленное действие (новый протокол, крупный recall)
    /// "high" — важно в ближайшее время
    /// "medium" — полезно, но не срочно
    /// "low" — для справки
    /// </summary>
    public string Relevance { get; set; } = "medium";

    /// <summary>Обработана ли новость (добавлены ли коды/схемы в базу).</summary>
    public bool IsProcessed { get; set; }

    /// <summary>Дата публикации новости.</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Дата обнаружения нашим мониторингом.</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Связанные марки авто (через запятую).</summary>
    public string RelatedBrands { get; set; } = "";

    /// <summary>Связанные коды ошибок (через запятую).</summary>
    public string RelatedErrorCodes { get; set; } = "";

    public override string ToString() =>
        $"[{Category}] {Title} ({Relevance}) — {Source}";
}
