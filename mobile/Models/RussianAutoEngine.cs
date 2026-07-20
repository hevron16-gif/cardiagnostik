using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Новый двигатель, трансмиссия или электронная система российского авто.
/// Сохраняется в russian_auto_engines таблице.
/// </summary>
public class RussianAutoEngine
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка (LADA, УАЗ, ГАЗ… или "универсальный").</summary>
    public string Brand { get; set; } = "";

    /// <summary>Модель авто (если привязан к конкретной).</summary>
    public string ModelName { get; set; } = "";

    // ══════════ Двигатель ══════════

    /// <summary>Код/индекс двигателя (ВАЗ-21179, ЗМЗ-40906, G4FG…).</summary>
    public string EngineCode { get; set; } = "";

    /// <summary>Название (1.8 Evo, 2.7 Turbo, АМТ 2.0…).</summary>
    public string EngineName { get; set; } = "";

    /// <summary>Топливо: бензин, дизель, электро, гибрид, газ.</summary>
    public string FuelType { get; set; } = "";

    /// <summary>Рабочий объём (л).</summary>
    public double? Displacement { get; set; }

    /// <summary>Мощность (л.с.).</summary>
    public int? PowerHP { get; set; }

    /// <summary>Крутящий момент (Н·м).</summary>
    public int? TorqueNM { get; set; }

    /// <summary>Система питания: распределённый впрыск, прямой впрыск, Common Rail, карбюратор.</summary>
    public string FuelSystem { get; set; } = "";

    /// <summary>Наддув: атмосферный, турбо, битурбо, компрессор.</summary>
    public string Turbo { get; set; } = "";

    /// <summary>Экологический класс: Евро-2..Евро-6, China-VI.</summary>
    public string EmissionClass { get; set; } = "";

    // ══════════ Трансмиссия ══════════

    /// <summary>Тип трансмиссии: МКПП, АКПП (гидротрансформатор), CVT (вариатор), РКПП (робот), DCT.</summary>
    public string Transmission { get; set; } = "";

    /// <summary>Производитель трансмиссии: АвтоВАЗ, Jatco, Aisin, Chery…</summary>
    public string TransmissionVendor { get; set; } = "";

    /// <summary>Число передач (5, 6, 7, 8…).</summary>
    public int? Gears { get; set; }

    // ══════════ Электроника / ЭБУ ══════════

    /// <summary>Тип ЭБУ: Bosch ME17.9.7, Микас 12.3, Delphi MT92…</summary>
    public string ECUType { get; set; } = "";

    /// <summary>Производитель ЭБУ: Bosch, Siemens, Delphi, Итэлма, АвтоВАЗ.</summary>
    public string ECUVendor { get; set; } = "";

    /// <summary>Диагностический протокол: OBD2, EOBD, CAN, UDS, K-Line, KWP2000.</summary>
    public string OBDProtocol { get; set; } = "";

    // ══════════ Системы безопасности / ADAS ══════════

    /// <summary>Системы помощи водителю через запятую (ABS, ESP, ADAS, круиз-контроль…).</summary>
    public string DriverAssist { get; set; } = "";

    // ══════════ Мета ══════════

    /// <summary>Тип записи: engine, transmission, ecu, hybrid, electric.</summary>
    public string RecordType { get; set; } = "engine";

    /// <summary>Новинка.</summary>
    public bool IsNew { get; set; }

    /// <summary>Статус: announced, in_production, testing, certified.</summary>
    public string Status { get; set; } = "announced";

    /// <summary>Завод-производитель двигателя.</summary>
    public string Factory { get; set; } = "";

    /// <summary>Источник.</summary>
    public string Source { get; set; } = "";

    /// <summary>URL источника.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Заметки.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Обработана ли запись.</summary>
    public bool IsProcessed { get; set; }

    /// <summary>Дата обнаружения.</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"[{RecordType}] {Brand} {(string.IsNullOrEmpty(ModelName) ? "" : $"{ModelName} ")}{EngineName} ({FuelType}{(Displacement.HasValue ? $" {Displacement}L" : "")}{(PowerHP.HasValue ? $" {PowerHP}лс" : "")}) — {Status}";
}
