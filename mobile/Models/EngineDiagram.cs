namespace CarDiagnosticApp.Models
{
    /// <summary>
    /// Компонент на 2D-схеме двигателя/узла.
    /// </summary>
    public class DiagramComponent
    {
        /// <summary>Уникальный ID (например "throttle_body")</summary>
        public string Id { get; set; } = "";

        /// <summary>Человеческое название</summary>
        public string Name { get; set; } = "";

        /// <summary>Точки полигона в нормализованных координатах (0..1)</summary>
        public List<PointF> Outline { get; set; } = new();

        /// <summary>Категория: engine, fuel, ignition, cooling, exhaust, intake, sensor, evap, electrical</summary>
        public string Category { get; set; } = "engine";

        /// <summary>Связанные OBD2 коды</summary>
        public List<string> ErrorCodes { get; set; } = new();

        /// <summary>Выделен ли компонент (ошибка связана)</summary>
        public bool IsHighlighted { get; set; }

        /// <summary>Уровень подсветки: 0=нет, 1=связан, 2=проверить, 3=неисправность</summary>
        public int HighlightLevel { get; set; }

        /// <summary>Цвет заливки по умолчанию (hex)</summary>
        public string DefaultColor { get; set; } = "#B0BEC5";
    }

    /// <summary>
    /// Один вид схемы (сверху, сбоку, подсистема).
    /// </summary>
    public class DiagramView
    {
        public string ViewName { get; set; } = "";
        public string ViewId { get; set; } = "";   // top, side, fuel, ignition, cooling, exhaust, evap, sensors
        public List<DiagramComponent> Components { get; set; } = new();
        public string BackgroundLabel { get; set; } = "";
    }

    /// <summary>
    /// Полная схема для конкретного типа двигателя.
    /// </summary>
    public class EngineDiagram
    {
        public string Id { get; set; } = "";
        public string ErrorCode { get; set; } = "";
        public string CarBrand { get; set; } = "";
        public string CarModel { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string? ImageUrl { get; set; }
        public string? ImagePath { get; set; }
        public string EngineName { get; set; } = "";     // "ВАЗ 8-кл. 1.6"
        public string EngineType { get; set; } = "";      // "inline4", "v6", "v8"
        public List<string> Checklist { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<DiagramView> Views { get; set; } = new();
    }
}
