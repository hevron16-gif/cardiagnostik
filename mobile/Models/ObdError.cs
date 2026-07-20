namespace CarDiagnosticApp.Models;

/// <summary>
/// Тип кода ошибки OBD2.
/// </summary>
public enum ObdErrorType
{
    /// <summary>Текущая — активна сейчас (Mode 03).</summary>
    Current,
    /// <summary>Ожидающая — зафиксирована, но CEL ещё не горит (Mode 07).</summary>
    Pending,
    /// <summary>Постоянная — не стирается сканером, снимается ЭБУ после ремонта (Mode 0A).</summary>
    Permanent
}

/// <summary>
/// Одна ошибка OBD2 с метаданными.
/// </summary>
public class ObdError
{
    /// <summary>Код, например P0171.</summary>
    public string Code { get; set; } = "";

    /// <summary>Тип ошибки.</summary>
    public ObdErrorType Type { get; set; }

    /// <summary>Данные freeze frame (ключ PID → значение).</summary>
    public Dictionary<string, string> FreezeFrame { get; set; } = new();

    /// <summary>Человекочитаемый тип.</summary>
    public string TypeLabel => Type switch
    {
        ObdErrorType.Current => "🔴 Текущая",
        ObdErrorType.Pending => "🟡 Ожидающая",
        ObdErrorType.Permanent => "🔵 Постоянная",
        _ => ""
    };
}
