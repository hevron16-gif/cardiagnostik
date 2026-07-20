using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Сессия кодирования: запись об активации/деактивации скрытой функции.
/// Хранится в таблице coding_sessions.
/// </summary>
public class CodingSession
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>ID функции из hidden_features.</summary>
    [Indexed]
    public int FeatureId { get; set; }

    /// <summary>Название функции (для быстрого отображения без JOIN).</summary>
    public string FeatureName { get; set; } = "";

    /// <summary>Марка авто.</summary>
    public string Brand { get; set; } = "";

    /// <summary>Модель авто.</summary>
    public string? ModelName { get; set; }

    /// <summary>Действие: activate / deactivate.</summary>
    public string Action { get; set; } = "activate";

    /// <summary>Успешно ли выполнено.</summary>
    public bool Success { get; set; }

    /// <summary>Код ответа от блока (или сообщение об ошибке).</summary>
    public string? ResponseData { get; set; }

    /// <summary>Примечания пользователя.</summary>
    public string? Notes { get; set; }

    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;
}


/// <summary>
/// Текущее состояние скрытой функции автомобиля — результат чтения настроек.
/// </summary>
public class CurrentSetting
{
    /// <summary>Название функции.</summary>
    public string FeatureName { get; set; } = "";

    /// <summary>Категория (lighting, comfort, safety, engine, etc.).</summary>
    public string Category { get; set; } = "";

    /// <summary>Иконка категории.</summary>
    public string Icon { get; set; } = "";

    /// <summary>Адрес модуля (7E0, 7E1, и т.д.).</summary>
    public string ModuleAddress { get; set; } = "";

    /// <summary>Смещение байта в памяти модуля.</summary>
    public int ByteOffset { get; set; }

    /// <summary>Битовая маска для проверки активности.</summary>
    public int BitMask { get; set; }

    /// <summary>Активна ли функция в данный момент.</summary>
    public bool IsActive { get; set; }

    /// <summary>Сырое hex-значение прочитанного байта.</summary>
    public string? RawHex { get; set; }

    /// <summary>Описание функции.</summary>
    public string Description { get; set; } = "";

    /// <summary>Можно ли активировать через ELM327.</summary>
    public bool IsCodable { get; set; } = true;

    public override string ToString() => $"{Icon} {FeatureName} = {(IsActive ? "Активна" : "Неактивна")}";
}


/// <summary>
/// Дамп байта кодировок модуля — сырой срез памяти.
/// </summary>
public class ModuleCodingDump
{
    /// <summary>Адрес модуля.</summary>
    public string ModuleAddress { get; set; } = "";

    /// <summary>Смещение байта (hex).</summary>
    public string OffsetHex { get; set; } = "";

    /// <summary>Hex-значение.</summary>
    public string HexValue { get; set; } = "";

    /// <summary>Десятичное значение.</summary>
    public int DecValue { get; set; }

    /// <summary>Бинарное представление (8 бит).</summary>
    public string Binary { get; set; } = "";

    /// <summary>ASCII-символ (если печатный).</summary>
    public string? Ascii { get; set; }

    public override string ToString() => $"[{ModuleAddress}] 0x{OffsetHex}: {HexValue} ({Binary})";
}


/// <summary>
/// Резервная копия настроек — сохраняет оригинальное значение байта до кодирования.
/// Хранится в таблице coding_backups.
/// </summary>
[Table("coding_backups")]
public class CodingBackup
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка авто.</summary>
    [Indexed]
    public string Brand { get; set; } = "";

    /// <summary>Модель авто.</summary>
    public string? ModelName { get; set; }

    /// <summary>VIN (если известен).</summary>
    public string? Vin { get; set; }

    /// <summary>Адрес модуля.</summary>
    public string ModuleAddress { get; set; } = "";

    /// <summary>Смещение байта в модуле.</summary>
    public int ByteOffset { get; set; }

    /// <summary>Исходное hex-значение до изменения.</summary>
    public string OriginalHex { get; set; } = "";

    /// <summary>Новое hex-значение после изменения.</summary>
    public string? NewHex { get; set; }

    /// <summary>ID функции из hidden_features (если применимо).</summary>
    [Indexed]
    public int? FeatureId { get; set; }

    /// <summary>Название функции для быстрого отображения.</summary>
    public string? FeatureName { get; set; }

    /// <summary>Название резервной копии (задаётся пользователем или авто).</summary>
    public string? Label { get; set; }

    /// <summary>Метка сессии (объединяет несколько байт в один бэкап).</summary>
    [Indexed]
    public string? SessionTag { get; set; }

    /// <summary>Восстановлена ли копия.</summary>
    public bool IsRestored { get; set; }

    /// <summary>Когда восстановлено.</summary>
    public DateTime? RestoredAt { get; set; }

    /// <summary>Примечания.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public override string ToString() =>
        $"[{(IsRestored ? "✅" : "💾")}] {Brand} {ModuleAddress} 0x{ByteOffset:X2}: {OriginalHex} → {NewHex ?? "—"} ({CreatedAt:dd.MM HH:mm})";
}
