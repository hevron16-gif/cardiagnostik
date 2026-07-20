using Microsoft.Maui.Storage;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис настроек приложения.
/// Хранит настройки через MAUI Preferences (persistent key-value store).
///
/// Ключи:
///   sync_period_hours  — период синхронизации в часах (по умолчанию 24)
///   sync_enabled       — включена ли авто-синхронизация (по умолчанию true)
///   offline_mode       — офлайн-режим (не отправлять ничего)
/// </summary>
public static class SettingsService
{
    private const string KeySyncPeriod = "sync_period_hours";
    private const string KeySyncEnabled = "sync_enabled";
    private const string KeyOfflineMode = "offline_mode";

    // ─── Период синхронизации (часы) ───

    /// <summary>Период синхронизации в часах (1–168, по умолчанию 24).</summary>
    public static int SyncPeriodHours
    {
        get => Preferences.Get(KeySyncPeriod, 24);
        set
        {
            var clamped = Math.Clamp(value, 1, 168); // от 1 часа до 1 недели
            Preferences.Set(KeySyncPeriod, clamped);
            PeriodChanged?.Invoke(null, clamped);
        }
    }

    /// <summary>Период синхронизации как TimeSpan.</summary>
    public static TimeSpan SyncPeriod => TimeSpan.FromHours(SyncPeriodHours);

    /// <summary>Человекочитаемое описание периода.</summary>
    public static string SyncPeriodLabel => SyncPeriodHours switch
    {
        < 2 => $"{SyncPeriodHours} час",
        < 24 => $"{SyncPeriodHours} часа",
        < 48 => "1 день",
        < 72 => $"{SyncPeriodHours / 24} дня",
        < 120 => $"{SyncPeriodHours / 24} дней",
        _ => "1 неделя",
    };

    // ─── Авто-синхронизация ───

    /// <summary>Включена ли автоматическая синхронизация.</summary>
    public static bool SyncEnabled
    {
        get => Preferences.Get(KeySyncEnabled, true);
        set => Preferences.Set(KeySyncEnabled, value);
    }

    // ─── Офлайн-режим ───

    /// <summary>
    /// Офлайн-режим: ничего не отправляется, всё копится в PendingQueue.
    /// </summary>
    public static bool OfflineMode
    {
        get => Preferences.Get(KeyOfflineMode, false);
        set => Preferences.Set(KeyOfflineMode, value);
    }

    // ─── События ───

    /// <summary>Вызывается при изменении периода синхронизации.</summary>
    public static event EventHandler<int>? PeriodChanged;

    // ─── Сброс ───

    /// <summary>Сброс всех настроек на значения по умолчанию.</summary>
    public static void ResetAll()
    {
        Preferences.Remove(KeySyncPeriod);
        Preferences.Remove(KeySyncEnabled);
        Preferences.Remove(KeyOfflineMode);
    }

    /// <summary>Сводка всех настроек для UI.</summary>
    public static string Summary => string.Join("\n",
        $"🔄 Период синхронизации: {SyncPeriodLabel} ({SyncPeriodHours} ч)",
        $"📡 Авто-синхронизация: {(SyncEnabled ? "✅ вкл" : "⛔ выкл")}",
        $"📴 Офлайн-режим: {(OfflineMode ? "⛔ вкл" : "✅ выкл")}"
    );
}
