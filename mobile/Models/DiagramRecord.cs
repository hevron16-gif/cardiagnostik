using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Запись локальной схемы в SQLite.
/// Одна запись = одна схема для конкретной комбинации марка+модель+код ошибки.
/// </summary>
[Table("Diagrams")]
public class DiagramRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка автомобиля (например "ВАЗ")</summary>
    [Indexed]
    public string CarBrand { get; set; } = "";

    /// <summary>Модель (например "2114")</summary>
    [Indexed]
    public string CarModel { get; set; } = "";

    /// <summary>Код ошибки (например "P0340")</summary>
    [Indexed]
    public string ErrorCode { get; set; } = "";

    /// <summary>Сериализованный EngineDiagram в JSON (векторная схема)</summary>
    public string DiagramJson { get; set; } = "";

    /// <summary>Локальный путь к скачанной картинке-схеме (если это растровая схема)</summary>
    public string ImagePath { get; set; } = "";

    /// <summary>URL источника (откуда скачали картинку)</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Источник: local (из JSON), internet (скачано из поиска), manual</summary>
    public string Source { get; set; } = "local";

    /// <summary>Версия схемы (для обновлений)</summary>
    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
