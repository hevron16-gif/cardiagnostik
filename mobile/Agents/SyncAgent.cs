using CarDiagnosticApp.Services;

namespace CarDiagnosticApp.Agents;

/// <summary>
/// Агент облачной синхронизации (Этап 7).
/// Периодически загружает локальные данные на сервер и
/// получает общие данные от других пользователей.
/// </summary>
public class SyncAgent
{
    private readonly SyncService _syncService;
    private CancellationTokenSource? _cts;
    private Timer? _timer;

    /// <summary>
    /// Статистика последней синхронизации.
    /// </summary>
    public SyncService.SyncSummary? LastSummary { get; private set; }

    /// <summary>
    /// Время последней синхронизации.
    /// </summary>
    public DateTime? LastSyncAt { get; private set; }

    /// <summary>
    /// True если синхронизация выполняется в данный момент.
    /// </summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Событие завершения синхронизации.
    /// </summary>
    public event EventHandler<SyncService.SyncSummary>? SyncCompleted;

    public SyncAgent(SyncService syncService)
    {
        _syncService = syncService;
    }

    /// <summary>
    /// Запускает периодическую синхронизацию.
    /// Период берётся из SettingsService.SyncPeriod (по умолчанию 24 часа).
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();

        var period = SettingsService.SyncPeriod;

        // Первая синхронизация через 30 секунд после запуска
        _timer = new Timer(async _ => await RunSyncAsync(), null,
            TimeSpan.FromSeconds(30),
            period);
    }

    /// <summary>
    /// Перезапускает таймер с новым периодом (вызывается после изменения настройки).
    /// </summary>
    public void Reconfigure()
    {
        if (_timer == null)
            return;

        var period = SettingsService.SyncPeriod;
        _timer.Change(TimeSpan.FromSeconds(10), period);
    }

    /// <summary>
    /// Останавливает периодическую синхронизацию.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Принудительный запуск синхронизации.
    /// </summary>
    public async Task<SyncService.SyncSummary> ForceSyncAsync()
    {
        return await RunSyncAsync();
    }

    private async Task<SyncService.SyncSummary> RunSyncAsync()
    {
        if (IsRunning)
            return LastSummary ?? new SyncService.SyncSummary();

        IsRunning = true;
        try
        {
            var summary = await _syncService.FullSyncAsync();
            LastSummary = summary;
            LastSyncAt = DateTime.UtcNow;
            SyncCompleted?.Invoke(this, summary);
            return summary;
        }
        catch (Exception ex)
        {
            var errorSummary = new SyncService.SyncSummary
            {
                Errors = new List<string> { $"SyncAgent critical: {ex.Message}" }
            };
            LastSummary = errorSummary;
            return errorSummary;
        }
        finally
        {
            IsRunning = false;
        }
    }
}
