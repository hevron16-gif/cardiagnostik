using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Накопленные знания по связке Ошибка + Авто.
/// Самообучающаяся модель: обновляется при каждом диагнозе и фидбеке.
/// </summary>
[Table("LearnedKnowledge")]
public class LearnedKnowledge
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки</summary>
    [Indexed]
    public string ErrorCode { get; set; } = "";

    /// <summary>Марка авто</summary>
    [Indexed]
    public string CarBrand { get; set; } = "";

    /// <summary>Модель авто</summary>
    public string CarModel { get; set; } = "";

    /// <summary>Последний полный ответ AI</summary>
    public string LastDiagnosisText { get; set; } = "";

    /// <summary>Сжатая выжимка диагноза (ключевые моменты)</summary>
    public string DiagnosisSummary { get; set; } = "";

    /// <summary>Наиболее вероятная причина</summary>
    public string LikelyCause { get; set; } = "";

    /// <summary>Типичные решения (через ;)</summary>
    public string KnownSolutions { get; set; } = "";

    /// <summary>Оценка уверенности 0.0–1.0 на основе фидбека</summary>
    public double Confidence { get; set; } = 0.5;

    /// <summary>Количество 👍 оценок</summary>
    public int PositiveFeedback { get; set; }

    /// <summary>Количество 👎 оценок</summary>
    public int NegativeFeedback { get; set; }

    /// <summary>Общее количество диагнозов по этой связке</summary>
    public int OccurrenceCount { get; set; } = 1;

    /// <summary>Связанные ошибки (которые часто появляются вместе), через ;</summary>
    public string RelatedErrors { get; set; } = "";

    /// <summary>Первое появление</summary>
    public DateTime FirstSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Последнее обновление</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Пересчитывает Confidence на основе фидбека.
    /// Формула: (1 + PositiveFeedback) / (2 + PositiveFeedback + NegativeFeedback)
    /// </summary>
    public void RecalculateConfidence()
    {
        Confidence = (1.0 + PositiveFeedback) / (2.0 + PositiveFeedback + NegativeFeedback);
        Confidence = Math.Round(Confidence, 2);
    }

    /// <summary>
    /// Формирует enrichment-текст для добавления в AI-промпт.
    /// </summary>
    public string ToEnrichmentText()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(LikelyCause))
            parts.Add($"Ранее диагностировалось как: {LikelyCause}");

        if (!string.IsNullOrWhiteSpace(KnownSolutions))
            parts.Add($"Проверенные решения: {KnownSolutions.Replace(";", ", ")}");

        if (Confidence > 0.6)
            parts.Add($"Достоверность: {Confidence:P0} (на основе {PositiveFeedback + NegativeFeedback} отзывов)");

        if (OccurrenceCount > 1)
            parts.Add($"Частота: {OccurrenceCount} диагноз(ов)");

        return parts.Count > 0 ? string.Join(" | ", parts) : "";
    }
}
