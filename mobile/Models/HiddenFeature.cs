using SQLite;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Скрытая функция автомобиля, доступная для кодирования/активации через OBD2.
/// Хранится в таблице hidden_features.
/// </summary>
public class HiddenFeature
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Марка авто (LADA, УАЗ, ГАЗ…). null = универсальная.</summary>
    [Indexed]
    public string? Brand { get; set; }

    /// <summary>Модель. null = для всей марки.</summary>
    public string? ModelName { get; set; }

    /// <summary>Год выпуска (с). null = любой.</summary>
    public int? YearFrom { get; set; }

    /// <summary>Год выпуска (по). null = любой.</summary>
    public int? YearTo { get; set; }

    /// <summary>Категория: lighting, comfort, safety, instrument, drivetrain, multimedia, other.</summary>
    public string Category { get; set; } = "other";

    /// <summary>Название функции (для отображения).</summary>
    public string FeatureName { get; set; } = "";

    /// <summary>Описание: что делает, зачем нужно.</summary>
    public string Description { get; set; } = "";

    /// <summary>Команда ELM327/AT для активации (или CAN-кадр в hex).</summary>
    public string? ActivationCommand { get; set; }

    /// <summary>Команда для деактивации.</summary>
    public string? DeactivationCommand { get; set; }

    /// <summary>Адрес блока (hex, напр. "7E0" для ЭБУ двигателя).</summary>
    public string? ModuleAddress { get; set; }

    /// <summary>Байт, который нужно изменить в конфигурации блока.</summary>
    public int? EncodedByte { get; set; }

    /// <summary>Битовая маска для изменения конкретного бита.</summary>
    public int? BitMask { get; set; }

    /// <summary>Требуется ли доступ к защищённой зоне (SecurityAccess).</summary>
    public bool RequiresSecurity { get; set; }

    /// <summary>Уровень доступа (SecurityAccess level), если требуется.</summary>
    public int SecurityLevel { get; set; } = 1;

    /// <summary>Ключ доступа (seed/key), если известен.</summary>
    public string? SecurityKey { get; set; }

    /// <summary>Доступна ли функция на данном авто (определяется при сканировании).</summary>
    [Ignore]
    public bool IsAvailable { get; set; }

    /// <summary>Активна ли в данный момент (определяется при сканировании).</summary>
    [Ignore]
    public bool IsActive { get; set; }

    /// <summary>Количество успешных активаций этой функции.</summary>
    public int ActivationCount { get; set; }

    /// <summary>Иконка (эмодзи).</summary>
    public string Icon { get; set; } = "⚙️";

    /// <summary>Источник: factory, community, generated.</summary>
    public string Source { get; set; } = "factory";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
