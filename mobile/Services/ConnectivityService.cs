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
    // Увеличенный таймаут: Render free tier просыпается 30-60с
    private const int TimeoutMs = 45_000;
    private const int Attempts = 3;

    private bool _isOnline;
    private bool _checked;
    private bool _isChecking;
    private int _consecutiveFailures = 0;
    private const int MaxFailuresBeforeOffline = 2; // Не переходить в офлайн сразу

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
        // Не запускаем параллельно
        if (_isChecking)
        {
            // Ждём завершения текущей проверки (до 60 сек)
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
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(3000); // Пауза между попытками

                foreach (var url in PingUrls)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs));
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

    private async void OnSystemConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            // Сеть появилась — проверяем сервер, но не чаще чем раз в 10 сек
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
}
