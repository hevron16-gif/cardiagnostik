using System.Text;
using System.Text.Json;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис анализа графиков: сравнение с эталонными значениями + AI-диагностика отклонений.
/// </summary>
public class GraphAnalysisService
{
    private readonly ApiService _api;
    private ReferencePidDatabase? _referenceDb;

    public GraphAnalysisService(ApiService api)
    {
        _api = api;
    }

    /// <summary>
    /// Загружает базу эталонных значений из встроенного ресурса.
    /// </summary>
    public async Task LoadReferenceDatabaseAsync()
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("reference_pids.json");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var json = await reader.ReadToEndAsync();
            _referenceDb = JsonSerializer.Deserialize<ReferencePidDatabase>(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphAnalysis] Failed to load reference DB: {ex.Message}");
            _referenceDb = new ReferencePidDatabase();
        }
    }

    /// <summary>
    /// Определяет режим работы двигателя по текущим значениям PID.
    /// </summary>
    public string DetectMode(Dictionary<string, double> currentValues)
    {
        if (_referenceDb?.Modes == null) return "warm_idle";

        // Пытаемся найти подходящий режим по условиям
        foreach (var mode in _referenceDb.Modes)
        {
            var cond = mode.Value.Conditions;
            bool match = true;

            if (cond.RpmMin.HasValue && (!currentValues.TryGetValue("0C", out var rpm) || rpm < cond.RpmMin)) match = false;
            if (cond.RpmMax.HasValue && (!currentValues.TryGetValue("0C", out var rpm2) || rpm2 > cond.RpmMax)) match = false;
            if (cond.ThrottleMax.HasValue && (!currentValues.TryGetValue("11", out var thr) || thr > cond.ThrottleMax)) match = false;
            if (cond.ThrottleMin.HasValue && (!currentValues.TryGetValue("11", out var thr2) || thr2 < cond.ThrottleMin)) match = false;
            if (cond.SpeedMax.HasValue && (!currentValues.TryGetValue("0D", out var spd) || spd > cond.SpeedMax)) match = false;
            if (cond.CoolantMin.HasValue && (!currentValues.TryGetValue("05", out var cool) || cool < cond.CoolantMin)) match = false;

            if (match) return mode.Key;
        }

        return "warm_idle"; // fallback
    }

    /// <summary>
    /// Сравнивает текущие значения PID с эталонными для указанного авто.
    /// Возвращает список отклонений.
    /// </summary>
    public List<PidDeviation> CompareWithReference(
        string brand,
        string model,
        Dictionary<string, double> currentValues,
        string? modeOverride = null)
    {
        var deviations = new List<PidDeviation>();
        if (_referenceDb == null) return deviations;

        var mode = modeOverride ?? DetectMode(currentValues);

        // Ищем эталоны: сначала по марке+модели, потом generic
        Dictionary<string, ReferencePidValue>? refs = null;

        if (_referenceDb.Vehicles.TryGetValue(brand, out var brandData) &&
            brandData.Models.TryGetValue(model, out var modelData))
        {
            modelData.Modes.TryGetValue(mode, out refs);
        }

        refs ??= _referenceDb.Generic.Modes.GetValueOrDefault(mode);
        if (refs == null) return deviations;

        foreach (var (pidHex, reference) in refs)
        {
            if (!currentValues.TryGetValue(pidHex, out var actual)) continue;
            if (double.IsNaN(actual)) continue;

            var status = GetDeviationStatus(actual, reference.Min, reference.Max);
            if (status != DeviationStatus.Normal)
            {
                var pidInfo = LiveDataService.AllPids.FirstOrDefault(p =>
                    p.PidHex.Equals(pidHex, StringComparison.OrdinalIgnoreCase));

                deviations.Add(new PidDeviation
                {
                    PidHex = pidHex,
                    PidName = pidInfo?.Name ?? $"PID {pidHex}",
                    Unit = reference.Unit,
                    ActualValue = actual,
                    ReferenceMin = reference.Min,
                    ReferenceMax = reference.Max,
                    Status = status,
                    Mode = mode,
                    DeviationPercent = CalculateDeviationPercent(actual, reference.Min, reference.Max),
                });
            }
        }

        return deviations.OrderByDescending(d => Math.Abs(d.DeviationPercent)).ToList();
    }

    /// <summary>
    /// Сравнивает историю графика (средние/мин/макс) с эталоном.
    /// </summary>
    public List<PidDeviation> CompareHistoryWithReference(
        string brand,
        string model,
        Dictionary<string, PidStatistics> historyStats,
        string? modeOverride = null)
    {
        // Берём средние значения для сравнения
        var averages = historyStats.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Average);
        return CompareWithReference(brand, model, averages, modeOverride);
    }

    /// <summary>
    /// Отправляет отклонения на сервер для AI-анализа.
    /// </summary>
    public async Task<GraphAiAnalysis?> AnalyzeDeviationsWithAiAsync(
        string brand,
        string model,
        string? vin,
        List<PidDeviation> deviations)
    {
        if (deviations.Count == 0) return null;

        try
        {
            return await _api.AnalyzeGraphDeviationsAsync(brand, model, vin, deviations);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GraphAnalysis] AI analysis failed: {ex.Message}");
            return null;
        }
    }

    // ─── Helpers ───────────────────────────────────────────────

    private static DeviationStatus GetDeviationStatus(double value, double min, double max)
    {
        if (value >= min && value <= max) return DeviationStatus.Normal;

        // Отклонение от ближайшей границы в процентах от допуска
        var tolerance = (max - min) / 2;
        if (tolerance <= 0) return DeviationStatus.Critical;

        var center = (min + max) / 2;
        var deviation = Math.Abs(value - center);
        var percent = deviation / tolerance;  // 1.0 = на границе, >1 = за границей

        // До 30% за границей — Warning, больше — Critical
        if (percent <= 1.3) return DeviationStatus.Warning;
        return DeviationStatus.Critical;
    }

    private static double CalculateDeviationPercent(double value, double min, double max)
    {
        var center = (min + max) / 2;
        var tolerance = (max - min) / 2;
        if (tolerance <= 0) return 0;
        return (value - center) / tolerance * 100;
    }
}

// ═══════════════════════════════════════════════════════════════
//  Модели данных
// ═══════════════════════════════════════════════════════════════

public enum DeviationStatus
{
    Normal,
    Warning,
    Critical,
}

public class PidDeviation
{
    public string PidHex { get; set; } = "";
    public string PidName { get; set; } = "";
    public string Unit { get; set; } = "";
    public double ActualValue { get; set; }
    public double ReferenceMin { get; set; }
    public double ReferenceMax { get; set; }
    public DeviationStatus Status { get; set; }
    public string Mode { get; set; } = "";
    public double DeviationPercent { get; set; }

    public string StatusIcon => Status switch
    {
        DeviationStatus.Normal => "✅",
        DeviationStatus.Warning => "⚠️",
        DeviationStatus.Critical => "🔴",
        _ => "❓"
    };

    public string StatusText => Status switch
    {
        DeviationStatus.Normal => "Норма",
        DeviationStatus.Warning => "Отклонение",
        DeviationStatus.Critical => "Критично",
        _ => "Неизвестно"
    };

    public string FormattedActual => $"{ActualValue:0.##} {Unit}";
    public string FormattedReference => $"{ReferenceMin:0.##}–{ReferenceMax:0.##} {Unit}";
}

public class PidStatistics
{
    public double Min { get; set; }
    public double Max { get; set; }
    public double Average { get; set; }
    public double StdDev { get; set; }
    public int Count { get; set; }
}

public class GraphAiAnalysis
{
    public string Summary { get; set; } = "";
    public List<string> PossibleCauses { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public string Severity { get; set; } = "СРЕДНЯЯ";
    public string CanDrive { get; set; } = "Осторожно";
    public string Source { get; set; } = "";
}

// ─── JSON модели для десериализации reference_pids.json ──────

public class ReferencePidDatabase
{
    public string Version { get; set; } = "";
    public Dictionary<string, ReferenceMode> Modes { get; set; } = new();
    public Dictionary<string, ReferenceBrand> Vehicles { get; set; } = new();
    public ReferenceGeneric Generic { get; set; } = new();
}

public class ReferenceMode
{
    public string Label { get; set; } = "";
    public ReferenceConditions Conditions { get; set; } = new();
}

public class ReferenceConditions
{
    public double? RpmMin { get; set; }
    public double? RpmMax { get; set; }
    public double? ThrottleMin { get; set; }
    public double? ThrottleMax { get; set; }
    public double? SpeedMax { get; set; }
    public double? CoolantMin { get; set; }
}

public class ReferenceBrand
{
    public Dictionary<string, ReferenceModel> Models { get; set; } = new();
}

public class ReferenceModel
{
    public string Engine { get; set; } = "";
    public Dictionary<string, Dictionary<string, ReferencePidValue>> Modes { get; set; } = new();
}

public class ReferencePidValue
{
    public double Min { get; set; }
    public double Max { get; set; }
    public string Unit { get; set; } = "";
}

public class ReferenceGeneric
{
    public string Description { get; set; } = "";
    public Dictionary<string, Dictionary<string, ReferencePidValue>> Modes { get; set; } = new();
}
