using Microsoft.Maui.Networking;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Проверка доступа в интернет при запуске и во время работы.
/// Thread-safe, не блокирует UI, толерантен к кратковременным сбоям.
/// </summary>
public class ConnectivityService
{
    private readonly HttpClient _http;
    private static readonly string[] PingUrls = {
        "https://kitdiag.ru/",
        "https://car-diagnostic-ai.onrender.com/", // fallback
    };

    // Таймауты
    private const int TimeoutMs = 15_000;
    private const int StartupTimeoutMs = 35_000;

    // Состояние — thread-safe через lock
    private bool _isOnline;
    private bool _checked;
    private bool _isChecking;
    private int _consecutiveFailures = 0;
    private DateTime _lastCheckTime = DateTime.MinValue;
    private readonly object _stateLock = new();
    private readonly TimeSpan _minCheckInterval = TimeSpan.FromSeconds(15);

    public bool IsOnline
    {
        get { lock (_stateLock) return _isOnline; }
    }

    public bool HasChecked
    {
        get { lock (_stateLock) return _checked; }
    }

    public event Action<bool>? ConnectivityChanged;

    public ConnectivityService()
    {
        _http = new HttpClient();
    }

    private void SetOnline(bool value)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _isOnline != value;
            _isOnline = value;
        }
        if (changed)
        {
            MainThread.BeginInvokeOnMainThread(() =>
                ConnectivityChanged?.Invoke(value));
        }
    }

    /// <summary>
    /// Первичная проверка при запуске. Не блокирует вызывающий поток.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        await Task.Delay(500); // Даём UI отрисоваться
        await DoCheckAsync(startup: true);
        lock (_stateLock) { _checked = true; }
    }

    /// <summary>
    /// Публичная проверка с rate limiting.
    /// </summary>
    public async Task<bool> CheckNowAsync(bool startup = false)
    {
        lock (_stateLock)
        {
            if (!startup && DateTime.Now - _lastCheckTime < _minCheckInterval)
                return _isOnline;
            if (_isChecking)
                return _isOnline; // Уже проверяем — вернём текущее состояние
        }
        return await DoCheckAsync(startup);
    }

    private async Task<bool> DoCheckAsync(bool startup)
    {
        lock (_stateLock)
        {
            if (_isChecking) return _isOnline;
            _isChecking = true;
            _lastCheckTime = DateTime.Now;
        }

        try
        {
            // Быстрая проверка NetworkAccess
            var netAccess = Connectivity.Current.NetworkAccess;
            if (netAccess != NetworkAccess.Internet)
            {
                IncrementFailures();
                return IsOnline;
            }

            // Пинг сервера
            var timeout = startup ? StartupTimeoutMs : TimeoutMs;

            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(1500);

                foreach (var url in PingUrls)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
                        var request = new HttpRequestMessage(HttpMethod.Head, url);
                        var response = await _http.SendAsync(request, cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            ResetFailures();
                            SetOnline(true);
                            return true;
                        }
                    }
                    catch (OperationCanceledException) { }
                    catch (HttpRequestException) { }
                    catch { }
                }
            }

            // Не удалось достучаться
            IncrementFailures();
            return IsOnline;
        }
        finally
        {
            lock (_stateLock) { _isChecking = false; }
        }
    }

    private void IncrementFailures()
    {
        lock (_stateLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
                _isOnline = false;
        }
    }

    private void ResetFailures()
    {
        lock (_stateLock) { _consecutiveFailures = 0; }
    }

    // ═══════════════════════════════════════════════
    // Системные события сети
    // ═══════════════════════════════════════════════

    public void StartListening()
    {
        Connectivity.ConnectivityChanged += OnSystemConnectivityChanged;
    }

    public void StopListening()
    {
        Connectivity.ConnectivityChanged -= OnSystemConnectivityChanged;
    }

    private void OnSystemConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        // Не запускаем async void — просто ставим флаг и запускаем фоновую проверку
        _ = HandleNetworkChangeAsync(e.NetworkAccess);
    }

    private async Task HandleNetworkChangeAsync(NetworkAccess access)
    {
        try
        {
            if (access == NetworkAccess.Internet)
            {
                await CheckNowAsync();
            }
            else
            {
                // Не переходим в офлайн мгновенно
                IncrementFailures();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Connectivity] HandleNetworkChange error: {ex.Message}");
        }
    }
}
