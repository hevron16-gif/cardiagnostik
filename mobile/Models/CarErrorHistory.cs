using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Запись истории ошибок по VIN автомобиля.
/// </summary>
[Table("car_error_history")]
public class CarErrorHistory
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>VIN автомобиля (индексирован).</summary>
    [Indexed]
    public string VIN { get; set; } = "";

    /// <summary>Марка (из ручного ввода или VIN-декодирования).</summary>
    public string Brand { get; set; } = "";

    /// <summary>Модель.</summary>
    public string Model { get; set; } = "";

    /// <summary>Код ошибки, например P0171.</summary>
    public string ErrorCode { get; set; } = "";

    /// <summary>Тип ошибки: Current, Pending, Permanent.</summary>
    public string ErrorType { get; set; } = "";

    /// <summary>Дата и время обнаружения (последнее).</summary>
    public DateTime DetectedAt { get; set; } = DateTime.Now;

    /// <summary>Дата и время первого появления этой ошибки на этом авто.</summary>
        public DateTime FirstSeenAt { get; set; } = DateTime.Now;
        public DateTime LastSeenAt { get; set; } = DateTime.Now;

    /// <summary>Была ли запущена AI-диагностика.</summary>
    public bool Diagnosed { get; set; }

    /// <summary>Результат AI-диагностики (первые 500 символов).</summary>
    public string? DiagnosisSnippet { get; set; }

    /// <summary>Сколько раз ошибку сбрасывали (Mode 04).</summary>
    public int ClearCount { get; set; }

    /// <summary>Дата последнего сброса.</summary>
    public DateTime? LastClearedAt { get; set; }

    /// <summary>Сколько раз ошибка появлялась (после сброса или повторно).</summary>
    public int AppearanceCount { get; set; } = 1;

    /// <summary>Ошибка повторяется (сбрасывали, но появляется снова).</summary>
    public bool IsRecurring { get; set; }

    /// <summary>Оценка риска по шкале 1–10.</summary>
    public int RiskScore { get; set; }

    /// <summary>ID сессии сканирования (для выявления связок ошибок).</summary>
    public string? ScanSessionId { get; set; }

    /// <summary>Метка риска: 🟢 Низкий / 🟡 Средний / 🟠 Высокий / 🔴 Критический.</summary>
    public string RiskLabel => RiskScore switch
    {
        <= 3 => "🟢 Низкий",
        <= 5 => "🟡 Средний",
        <= 7 => "🟠 Высокий",
        _ => "🔴 Критический"
    };
}
