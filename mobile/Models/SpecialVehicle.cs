using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Модель единицы спецтехники (трактор, комбайн, грузовик, автобус и т.д.).
/// Хранится в таблице special_vehicles.
/// </summary>
[Table("special_vehicles")]
public class SpecialVehicle
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка/производитель (Кировец, Беларус, ДСТ-Урал, КАМАЗ, ...).</summary>
    [Indexed]
    public string Brand { get; set; } = "";

    /// <summary>Модель (К-744Р4, МТЗ-82.1, ДТ-75, ...).</summary>
    public string Model { get; set; } = "";

    /// <summary>Тип техники: tractor, combine, truck, bus, construction.</summary>
    [Indexed]
    public string VehicleType { get; set; } = "tractor";

    /// <summary>Семейство двигателей (ЯМЗ-658, ММЗ Д-245, ТМЗ-8481, ...).</summary>
    public string? EngineFamily { get; set; }

    /// <summary>Протокол диагностики: J1939, K-Line, CAN, ISO15765.</summary>
    public string Protocol { get; set; } = "J1939";

    /// <summary>Скорость шины в кбит/с (250, 500, ...).</summary>
    public int BusSpeedKbps { get; set; } = 250;

    /// <summary>Годы выпуска.</summary>
    public string? Years { get; set; }

    /// <summary>Краткое описание.</summary>
    public string? Description { get; set; }

    /// <summary>Иконка/эмодзи для UI.</summary>
    public string Icon { get; set; } = "🚜";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() => $"{Icon} {Brand} {Model} [{Protocol}]";
}


/// <summary>
/// Код ошибки спецтехники — может использовать J1939 SPN/FMI или OBD2-формат.
/// Хранится в таблице special_error_codes.
/// </summary>
[Table("special_error_codes")]
public class SpecialErrorCode
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>ID транспортного средства из special_vehicles.</summary>
    [Indexed]
    public int VehicleId { get; set; }

    /// <summary>Код ошибки (P-код для OBD2, или SPN/FMI для J1939, или код лампы).</summary>
    public string Code { get; set; } = "";

    /// <summary>Тип кода: obd2, j1939, blink, manufacturer.</summary>
    public string CodeType { get; set; } = "j1939";

    /// <summary>SPN (Suspect Parameter Number) — для J1939. 0 для OBD2-кодов.</summary>
    public int SPN { get; set; }

    /// <summary>FMI (Failure Mode Identifier) — для J1939.</summary>
    public int FMI { get; set; }

    /// <summary>SA (Source Address) блока, выдавшего ошибку.</summary>
    public int SourceAddress { get; set; }

    /// <summary>Лампа: MIL, RedStop, AmberWarning, Protect.</summary>
    public string? Lamp { get; set; }

    /// <summary>Система: engine, transmission, hydraulics, brakes, electrical, body.</summary>
    public string System { get; set; } = "engine";

    /// <summary>Описание ошибки на русском.</summary>
    public string Description { get; set; } = "";

    /// <summary>Возможные причины.</summary>
    public string? Causes { get; set; }

    /// <summary>Рекомендации по устранению.</summary>
    public string? FixRecommendation { get; set; }

    /// <summary>Уровень критичности: low, medium, high, critical.</summary>
    public string Severity { get; set; } = "medium";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        CodeType == "j1939"
            ? $"SPN {SPN} FMI {FMI}: {Description}"
            : $"{Code}: {Description}";
}


/// <summary>
/// Электронный блок управления спецтехники.
/// Хранится в таблице special_ecus.
/// </summary>
[Table("special_ecus")]
public class SpecialVehicleECU
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>ID транспортного средства из special_vehicles.</summary>
    [Indexed]
    public int VehicleId { get; set; }

    /// <summary>Название ЭБУ (ЭБУ ЯМЗ-658, Контроллер ММЗ Д-245, ...).</summary>
    public string ECUName { get; set; } = "";

    /// <summary>Source Address в J1939-сети (0-255).</summary>
    public int SourceAddress { get; set; }

    /// <summary>Протокол общения: J1939, K-Line, CAN, LIN.</summary>
    public string Protocol { get; set; } = "J1939";

    /// <summary>Производитель ЭБУ: BOSCH, ЯЗДА, ИТЭЛМА, Continental, ...</summary>
    public string? Manufacturer { get; set; }

    /// <summary>Назначение: engine, transmission, abs, body, hydraulic, instrument.</summary>
    public string Function { get; set; } = "engine";

    /// <summary>Описание.</summary>
    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() => $"{ECUName} (SA={SourceAddress}, {Protocol})";
}
