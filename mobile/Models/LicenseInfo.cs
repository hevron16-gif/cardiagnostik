namespace CarDiagnosticApp.Models;

/// <summary>
/// Информация о лицензии, полученная с сервера.
/// </summary>
public class LicenseInfo
{
    public string Tier { get; set; } = "free";         // free / pro / enterprise
    public bool IsPaid { get; set; }
    public bool IsExpired { get; set; }
    public string? ValidUntil { get; set; }
    public List<string> Features { get; set; } = new();
    public List<string> LockedFeatures { get; set; } = new();
    public string? Message { get; set; }
    public string? UpgradeUrl { get; set; }
}

/// <summary>
/// Ответ сервера на активацию ключа.
/// </summary>
public class LicenseActivationResult
{
    public bool Success { get; set; }
    public string? Tier { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public List<string> Features { get; set; } = new();
}

/// <summary>
/// DTO для запроса активации.
/// </summary>
public class LicenseActivateRequest
{
    public string Key { get; set; } = "";
    public string DeviceId { get; set; } = "";
}

/// <summary>
/// Возможности (feature flags) — зеркало серверных ключей.
/// </summary>
public static class FeatureFlags
{
    public const string Ai = "ai";
    public const string Schemas = "schemas";
    public const string Sync = "sync";
    public const string SelfLearning = "self_learning";
    public const string LiveGraphs = "live_graphs";
    public const string FullHistory = "full_history";
    public const string Admin = "admin";
    public const string AutoUpdate = "auto_update";

    public static readonly string[] FreeFeatures = { "offline", "elm327", "basic_history" };
    public static readonly string[] PaidFeatures = { Ai, Schemas, LiveGraphs, SelfLearning, Sync };
    public static readonly string[] EnterpriseOnly = { Admin, AutoUpdate, "full_history", "basic_simulator" };
}
