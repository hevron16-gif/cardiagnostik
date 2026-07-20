using System.Text.Json.Serialization;

namespace CarDiagnosticApp.Models;

/// <summary>
/// Универсальный PID живых данных OBD2 — десериализуется из JSON.
/// </summary>
public class LiveDataPid
{
    [JsonPropertyName("pid")]
    public string PidHex { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("formula")]
    public string Formula { get; set; } = "a_identity";

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "";

    [JsonPropertyName("bytes")]
    public int Bytes { get; set; } = 1;

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; } = 9999;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "engine";

    [JsonPropertyName("priority")]
    public int Priority { get; set; } = 1;

    /// <summary>
    /// Поддерживается ли PID текущим ЭБУ.
    /// </summary>
    [JsonIgnore]
    public bool IsSupported { get; set; } = true;

    /// <summary>
    /// Формат отображения: 840 об/мин
    /// </summary>
    [JsonIgnore]
    public string DisplayFormat =>
        "{0:0.#}" + (string.IsNullOrEmpty(Unit) ? "" : " " + Unit);

    /// <summary>
    /// Вычисляет значение по сырым байтам через движок формул.
    /// </summary>
    public double Compute(int a, int b = 0, int c = 0, int d = 0) =>
        PidFormulaEngine.Evaluate(Formula, a, b, c, d);

    public override string ToString() => Name;
}

/// <summary>
/// Десериализованный JSON-корень базы PID.
/// </summary>
public class PidDatabase
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("last_updated")]
    public string LastUpdated { get; set; } = "";

    [JsonPropertyName("pids")]
    public List<LiveDataPid> Pids { get; set; } = new();

    [JsonPropertyName("categories")]
    public Dictionary<string, PidCategory> Categories { get; set; } = new();

    [JsonPropertyName("formulas")]
    public Dictionary<string, PidFormulaMeta> Formulas { get; set; } = new();
}

/// <summary>
/// Метаданные категории PID.
/// </summary>
public class PidCategory
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#1565C0";
}

/// <summary>
/// Метаданные формулы (для справки, не влияют на вычисления).
/// </summary>
public class PidFormulaMeta
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("args")]
    public string Args { get; set; } = "";
}

/// <summary>
/// Движок вычисления значений PID по именованным формулам.
/// </summary>
public static class PidFormulaEngine
{
    /// <summary>
    /// Вычисляет значение PID по имени формулы и байтам A, B, C, D.
    /// </summary>
    public static double Evaluate(string formula, int a, int b, int c, int d)
    {
        return formula switch
        {
            "a_identity"        => a,
            "a_bitmask"         => a,
            "a_hi_b_lo"         => (a << 4) | (b & 0x0F),
            "a_percent"         => a * 100.0 / 255,
            "a_sub_40"          => a - 40.0,
            "a_sub128_percent"  => (a - 128.0) * 100.0 / 128,
            "a_mul_3"           => a * 3.0,
            "a_div2_sub64"      => a / 2.0 - 64,
            "a_div200"          => a / 200.0,
            "a_sub125"          => a - 125.0,

            "ab_identity"       => (a * 256) + b,
            "ab_div_4"          => ((a * 256) + b) / 4.0,
            "ab_div_4_mul_10"   => ((a * 256) + b) * 10.0 / 4,
            "ab_div_10_sub40"   => ((a * 256) + b) / 10.0 - 40,
            "ab_div_20"         => ((a * 256) + b) / 20.0,
            "ab_div_100"        => ((a * 256) + b) / 100.0,
            "ab_div_255"        => ((a * 256) + b) / 255.0,
            "ab_div_256"        => ((a * 256) + b) / 256.0,
            "ab_div_512_sub128" => ((a * 256) + b) / 512.0 - 128,
            "ab_div_1000"       => ((a * 256) + b) / 1000.0,
            "ab_div_32768"      => ((a * 256) + b) / 32768.0,

            "ab_bitmask"        => (a << 8) | b,
            "abcd_identity"     => (a << 24) | (b << 16) | (c << 8) | d,
            "raw_4byte"         => (a << 24) | (b << 16) | (c << 8) | d,

            _ => a  // fallback: raw byte A
        };
    }
}
