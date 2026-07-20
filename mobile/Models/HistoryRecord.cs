using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Запись в локальной SQLite-базе истории диагностик.
/// Хранит все поля с сервера + локальный статус.
/// </summary>
[Table("history")]
public class HistoryRecord
{
    // ----- Статусы (константы) -----
    public const string StatusUnsolved = "Не решено";
    public const string StatusInProgress = "В процессе";
    public const string StatusSolved = "Решено";

    // ----- Поля -----

    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public string ErrorCode { get; set; } = "";

    public string CarBrand { get; set; } = "";

    public string CarModel { get; set; } = "";

    /// <summary>Краткий сниппет (первые строки ответа AI).</summary>
    public string Snippet { get; set; } = "";

    /// <summary>Полный текст диагноза (для офлайн-повтора).</summary>
    public string Diagnosis { get; set; } = "";

    /// <summary>ISO-метка времени с сервера.</summary>
    public string Timestamp { get; set; } = "";

    /// <summary>Локальный статус: Не решено / В процессе / Решено.</summary>
    public string Status { get; set; } = StatusUnsolved;

    // ----- Вспомогательные методы -----

    public void CycleStatus()
    {
        Status = Status switch
        {
            StatusUnsolved => StatusInProgress,
            StatusInProgress => StatusSolved,
            _ => StatusUnsolved
        };
    }

    /// <summary>
    /// Преобразует запись БД в HistoryItem для UI-привязок.
    /// </summary>
    public HistoryItem ToHistoryItem()
    {
        return new HistoryItem
        {
            error_code = ErrorCode,
            car_brand = CarBrand,
            car_model = CarModel,
            snippet = Snippet,
            timestamp = Timestamp,
            Status = Status,
            DbId = Id
        };
    }

    /// <summary>
    /// Создаёт запись БД из HistoryItem (серверная модель).
    /// </summary>
    public static HistoryRecord FromServerItem(HistoryItem item)
    {
        return new HistoryRecord
        {
            ErrorCode = item.error_code ?? "",
            CarBrand = item.car_brand ?? "",
            CarModel = item.car_model ?? "",
            Snippet = item.snippet ?? "",
            Timestamp = item.timestamp ?? "",
            Status = StatusUnsolved
        };
    }
}
