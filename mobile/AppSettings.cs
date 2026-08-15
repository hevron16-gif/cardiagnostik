namespace CarDiagnosticApp;

/// <summary>
/// Глобальные настройки приложения (tier, подписка и т.д.).
/// </summary>
public static class AppSettings
{
    private static string _userTier = "free";

    /// <summary>
    /// Текущий tier пользователя: free | pro | enterprise
    /// </summary>
    public static string UserTier
    {
        get => _userTier;
        set
        {
            _userTier = value?.ToLowerInvariant() switch
            {
                "pro" => "pro",
                "enterprise" => "enterprise",
                _ => "free"
            };
        }
    }

    /// <summary>AI-диагностика доступна только в Pro/Enterprise.</summary>
    public static bool IsAiAvailable => UserTier is "pro" or "enterprise";

    /// <summary>Схемы узлов доступны только в Pro/Enterprise.</summary>
    public static bool IsSchemasAvailable => UserTier is "pro" or "enterprise";

    /// <summary>Облачная синхронизация доступна только в Pro/Enterprise.</summary>
    public static bool IsSyncAvailable => UserTier is "pro" or "enterprise";

    /// <summary>Полная история доступна только в Enterprise.</summary>
    public static bool IsFullHistoryAvailable => UserTier is "enterprise";

    /// <summary>Админ-панель доступна только в Enterprise.</summary>
    public static bool IsAdminAvailable => UserTier is "enterprise";
}
