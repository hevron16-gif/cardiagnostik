using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Модель российского автомобиля (марка + модель).
/// Сохраняется в russian_auto_models таблице.
/// </summary>
public class RussianAutoModel
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка (LADA, УАЗ, ГАЗ, Москвич, Evolute, Xcite, AmberAuto…).</summary>
    public string Brand { get; set; } = "";

    /// <summary>Название модели (Vesta, Patriot, Соболь NN…).</summary>
    public string ModelName { get; set; } = "";

    /// <summary>Поколение: "I", "II", "III", "рестайлинг", "фейслифт", "новое поколение".</summary>
    public string Generation { get; set; } = "";

    /// <summary>Год начала выпуска.</summary>
    public int? YearStart { get; set; }

    /// <summary>Год окончания выпуска (null = выпускается).</summary>
    public int? YearEnd { get; set; }

    /// <summary>Тип кузова: седан, хэтчбек, универсал, кроссовер, внедорожник, пикап, лифтбек, фургон.</summary>
    public string BodyType { get; set; } = "";

    /// <summary>Типы двигателей через запятую (бензин 1.6, дизель 2.0, электро, гибрид…).</summary>
    public string EngineTypes { get; set; } = "";

    /// <summary>Диагностический протокол (OBD2, EOBD, CAN, UDS…).</summary>
    public string OBDProtocol { get; set; } = "";

    /// <summary>Источник информации (домен).</summary>
    public string Source { get; set; } = "";

    /// <summary>URL источника.</summary>
    public string SourceUrl { get; set; } = "";

    /// <summary>Новинка (да = только анонсирована/недавно вышла).</summary>
    public bool IsNew { get; set; }

    /// <summary>Статус: "announced", "in_production", "discontinued", "rumored".</summary>
    public string Status { get; set; } = "announced";

    /// <summary>Завод-производитель: АвтоВАЗ, УАЗ, ГАЗ, Москвич, Автотор, Моторинвест…</summary>
    public string Factory { get; set; } = "";

    /// <summary>Дата обнаружения.</summary>
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Заметки.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Обработана ли запись (добавлена в основную базу марок).</summary>
    public bool IsProcessed { get; set; }

    public override string ToString() =>
        $"{Brand} {ModelName} ({Generation}) [{YearStart}{(YearEnd.HasValue ? $"-{YearEnd}" : "+")}] — {Status}";
}
