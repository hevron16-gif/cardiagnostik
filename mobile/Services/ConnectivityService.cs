using Microsoft.Maui.Networking;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Проверка доступа в интернет при запуске и во время работы.
/// Отличается от простого Connectivity.NetworkAccess тем, что
/// реально пингует сервер (HTTP HEAD), а не только смотрит на
/// наличие Wi‑Fi/мобильной сети.
/// </summary>
public class ConnectivityService
{
    private readonly HttpClient _http;
    // Free Render tier — основной URL (kitdiag.ru отключён до апгрейда плана)
    // TODO: переключить при переходе на paid plan
    private static readonly string[] PingUrls = {
        "https://car-diagnostic-ai.onrender.com/",
        // "https://api.kitdiag.ru/",  // включается при апгрейде Render
    };
    // Таймауты: Render free tier просыпается 30-60с, но не блокируем UI
    private const int TimeoutMs = 20_000;        // Быстрый таймаут — не ждём вечно
    private const int StartupTimeoutMs = 45_000; // При старте даём больше времени
    private const int Attempts = 2;              // 2 попытки — не тратим батарею

    private bool _isOnline;
    private bool _checked;
    private bool _isChecking;
    private readonly object _checkLock = new();
    private int _consecutiveFailures = 0;
    private const int MaxFailuresBeforeOffline = 3; // Больше терпимости
    private DateTime _lastCheckTime = DateTime.MinValue;
    private readonly TimeSpan _minCheckInterval = TimeSpan.FromSeconds(10); // Не чаще 1 раз в 10 сек

    /// <summary>
    /// Реальное состояние: true только если сервер отвечает.
    /// </summary>
    public bool IsOnline
    {
        get => _isOnline;
        private set
        {
            if (_isOnline == value) return;
            _isOnline = value;
            MainThread.BeginInvokeOnMainThread(() =>
                ConnectivityChanged?.Invoke(value));
        }
    }

    /// <summary>
    /// Флаг: первичная проверка уже выполнена.
    /// </summary>
    public bool HasChecked => _checked;

    /// <summary>
    /// Вызывается при изменении состояния (true = онлайн, false = офлайн).
    /// Всегда на главном потоке.
    /// </summary>
    public event Action<bool>? ConnectivityChanged;

    public ConnectivityService()
    {
        // Не задаём таймаут здесь — управляем через CancellationToken
        _http = new HttpClient();
    }

    /// <summary>
    /// Выполняет первичную проверку при запуске.
    /// Вызывать из App.OnStart или MainPage конструктора.
    /// </summary>
    public async Task CheckOnStartupAsync()
    {
        // Ждём 1с чтобы дать системе инициализироваться
        await Task.Delay(1000);
        await CheckNowAsync(startup: true);
        _checked = true;
    }

    /// <summary>
    /// Принудительная проверка прямо сейчас.
    /// Не запускает параллельные проверки — если проверка уже идёт, ждёт её.
    /// </summary>
    public async Task<bool> CheckNowAsync(bool startup = false)
    {
        // Rate limiting: не чаще раз в 10 секунд (кроме старта)
        if (!startup)
        {
            lock (_checkLock)
            {
                if (DateTime.Now - _lastCheckTime < _minCheckInterval)
                    return IsOnline;
            }
        }

        // Не запускаем параллельно
        if (_isChecking)
        {
            // Ждём завершения текущей проверки (макс 60 сек)
            for (int i = 0; i < 120; i++)
            {
                if (!_isChecking) break;
                await Task.Delay(500);
            }
            return IsOnline;
        }

        _isChecking = true;
        try
        {
            lock (_checkLock)
            {
                _lastCheckTime = DateTime.Now;
            }

            // Быстрая проверка: NetworkAccess
            var netAccess = Connectivity.Current.NetworkAccess;
            if (netAccess != NetworkAccess.Internet)
            {
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxFailuresBeforeOffline)
                {
                    IsOnline = false;
                }
                return IsOnline;
            }

            // Настоящий пинг сервера (HEAD — быстрее чем GET)
            var timeout = startup ? StartupTimeoutMs : TimeoutMs;

            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(2000); // Пауза между попытками

                foreach (var url in PingUrls)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
                        var request = new HttpRequestMessage(HttpMethod.Head, url);
                        var response = await _http.SendAsync(request, cts.Token);
                        if (response.IsSuccessStatusCode)
                        {
                            _consecutiveFailures = 0;
                            IsOnline = true;
                            return true;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Таймаут — пробуем следующий URL
                    }
                    catch (HttpRequestException)
                    {
                        // Нет соединения — пробуем следующий URL
                    }
                    catch
                    {
                        // Другая ошибка — пробуем следующий URL
                    }
                }
            }

            // Все попытки исчерпаны
            _consecutiveFailures++;
            if (_consecutiveFailures >= MaxFailuresBeforeOffline)
            {
                IsOnline = false;
            }
            return IsOnline;
        }
        finally
        {
            _isChecking = false;
        }
    }

    /// <summary>
    /// Подписаться на системные события Connectivity.
    /// Вызывать один раз при старте.
    /// </summary>
    public void StartListening()
    {
        Connectivity.ConnectivityChanged += OnSystemConnectivityChanged;
    }

    /// <summary>
    /// Отписаться от системных событий (при уничтожении).
    /// </summary>
    public void StopListening()
    {
        Connectivity.ConnectivityChanged -= OnSystemConnectivityChanged;
    }

    private async void OnSystemConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        try
        {
            // Rate limiting: не реагируем на каждое мелкое изменение
            lock (_checkLock)
            {
                if (DateTime.Now - _lastCheckTime < _minCheckInterval)
                    return;
            }

            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                // Сеть появилась — проверяем сервер
                await CheckNowAsync();
            }
            else
            {
                // Не переходим в офлайн мгновенно — возможно, кратковременный сбой
                _consecutiveFailures++;
                if (_consecutiveFailures >= MaxFailuresBeforeOffline)
                {
                    IsOnline = false;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ConnectivityService] OnSystemConnectivityChanged error: {ex.Message}");
        }
    }
}
