using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Запись пользователя в локальной БД — настройки, VIN-ы, предпочтения.
/// Таблица: user_profile
/// </summary>
[Table("user_profile")]
public class UserRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Ключ настройки (language, theme, default_brand, saved_vin, …).</summary>
    [Column("key"), Indexed]
    public string Key { get; set; } = "";

    /// <summary>Значение.</summary>
    [Column("value")]
    public string Value { get; set; } = "";

    /// <summary>Тип: string, int, bool, json.</summary>
    public string ValueType { get; set; } = "string";

    /// <summary>Когда создано/обновлено (UTC).</summary>
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Тег группировки: profile, vin, license, device.</summary>
    public string? Tag { get; set; }
}
