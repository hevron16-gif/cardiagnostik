using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Заявка на схему, которая ещё не найдена.
/// Приложение будет повторять поиск при запуске или по запросу.
/// </summary>
[Table("PendingDiagramRequests")]
public class PendingDiagramRequest
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Код ошибки</summary>
    [Indexed]
    public string ErrorCode { get; set; } = "";

    /// <summary>Марка авто</summary>
    public string CarBrand { get; set; } = "";

    /// <summary>Модель авто</summary>
    public string CarModel { get; set; } = "";

    /// <summary>Поисковый запрос, который использовался</summary>
    public string SearchQuery { get; set; } = "";

    /// <summary>Дата первого запроса</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Дата последней попытки</summary>
    public DateTime LastRetryAt { get; set; } = DateTime.UtcNow;

    /// <summary>Количество попыток поиска</summary>
    public int RetryCount { get; set; } = 1;

    /// <summary>Статус: pending (ждёт), found (найдена), abandoned (отменена)</summary>
    public string Status { get; set; } = "pending";
}
