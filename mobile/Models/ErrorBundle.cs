namespace CarDiagnosticApp.Models;

/// <summary>
/// Связка ошибок — два кода, которые появляются одновременно.
/// </summary>
public class ErrorBundle
{
    /// <summary>Первый код ошибки.</summary>
    public string CodeA { get; set; } = "";

    /// <summary>Второй код ошибки.</summary>
    public string CodeB { get; set; } = "";

    /// <summary>Сколько раз оба кода появились в одной сессии.</summary>
    public int TogetherCount { get; set; }

    /// <summary>Сколько раз A был без B.</summary>
    public int OnlyACount { get; set; }

    /// <summary>Сколько раз B был без A.</summary>
    public int OnlyBCount { get; set; }

    /// <summary>Сила связи (0.0–1.0). 1.0 = всегда вместе.</summary>
    public double Strength { get; set; }

    /// <summary>VIN автомобиля.</summary>
    public string Vin { get; set; } = "";

    /// <summary>Человекочитаемое описание связки.</summary>
    public string Summary =>
        Strength switch
        {
            >= 0.9 => $"🔗 Всегда вместе ({TogetherCount}×)",
            >= 0.7 => $"🔗 Часто вместе ({TogetherCount}× из {TogetherCount + OnlyACount + OnlyBCount})",
            _ => $"🔗 Бывает вместе ({TogetherCount}× из {TogetherCount + OnlyACount + OnlyBCount})"
        };
}
