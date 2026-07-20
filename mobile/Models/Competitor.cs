using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Информация о конкуренте.
/// </summary>
[Table("competitors")]
public class Competitor
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Название приложения/сервиса.</summary>
    public string Name { get; set; } = "";

    /// <summary>Разработчик/компания.</summary>
    public string Developer { get; set; } = "";

    /// <summary>URL магазина (Google Play / App Store).</summary>
    public string StoreUrl { get; set; } = "";

    /// <summary>URL сайта.</summary>
    public string WebsiteUrl { get; set; } = "";

    /// <summary>Платформа: android / ios / desktop / multi.</summary>
    public string Platform { get; set; } = "android";

    /// <summary>Ценовая модель: free / freemium / paid / subscription.</summary>
    public string Pricing { get; set; } = "freemium";

    /// <summary>Цена в USD (если paid/subscription).</summary>
    public double PriceUsd { get; set; }

    /// <summary>Рейтинг (1–5, NaN если неизвестен).</summary>
    public double Rating { get; set; } = double.NaN;

    /// <summary>Количество отзывов/установок.</summary>
    public long ReviewCount { get; set; }

    /// <summary>Последняя известная версия.</summary>
    public string LatestVersion { get; set; } = "";

    /// <summary>Дата последнего обновления версии.</summary>
    public DateTime? LastVersionDate { get; set; }

    /// <summary>Ключевые фичи (JSON-массив строк).</summary>
    public string KeyFeatures { get; set; } = "[]";

    /// <summary>Сильные стороны (текст).</summary>
    public string Strengths { get; set; } = "";

    /// <summary>Слабые стороны (текст).</summary>
    public string Weaknesses { get; set; } = "";

    /// <summary>AI-анализ? (true/false).</summary>
    public bool HasAiFeatures { get; set; }

    /// <summary>Поддержка русского языка.</summary>
    public bool HasRussianLanguage { get; set; }

    /// <summary>Дата добавления в базу.</summary>
    [Indexed]
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Дата последней проверки.</summary>
    public DateTime? LastCheckedAt { get; set; }

    /// <summary>Активен (не снят с продажи).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Заметки/комментарий.</summary>
    public string Notes { get; set; } = "";

    public override string ToString() => $"{Name} v{LatestVersion} ★{Rating:F1} ({ReviewCount})";
}

/// <summary>
/// История изменений конкурента (версия, рейтинг, фичи).
/// </summary>
[Table("competitor_history")]
public class CompetitorChange
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int CompetitorId { get; set; }

    /// <summary>Тип изменения: version / rating / features / pricing.</summary>
    public string ChangeType { get; set; } = "";

    /// <summary>Старое значение.</summary>
    public string OldValue { get; set; } = "";

    /// <summary>Новое значение.</summary>
    public string NewValue { get; set; } = "";

    /// <summary>Дата обнаружения изменения.</summary>
    [Indexed]
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"[{DetectedAt:yyyy-MM-dd}] {ChangeType}: {OldValue} → {NewValue}";
}
