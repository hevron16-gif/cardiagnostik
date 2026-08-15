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
    // Fallback: если kitdiag.ru не работает, пробуем старый URL
    private static readonly string[] PingUrls = {
        "https://car-diagnostic-ai.onrender.com/",
        "https://api.kitdiag.ru/",
    };
    // Увеличенный таймаут: Render free tier просыпается 30-60с
    private const int TimeoutMs = 15_000;
    private const int StartupTimeoutMs = 30_000;

    private bool _isOnline;
    private bool _checked;

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
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
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
    /// </summary>
    public async Task<bool> CheckNowAsync(bool startup = false)
    {
        // Быстрая проверка: NetworkAccess
        var netAccess = Connectivity.Current.NetworkAccess;
        if (netAccess != NetworkAccess.Internet)
        {
            IsOnline = false;
            return false;
        }

        // Настоящий пинг сервера (пробуем все URL с retry)
        var timeout = startup ? StartupTimeoutMs : TimeoutMs;
        var attempts = startup ? 3 : 1;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(2000); // Пауза между попытками

            foreach (var url in PingUrls)
            {
                try
                {
                    var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeout));
                    var response = await _http.GetAsync(url, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
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

        IsOnline = false;
        return false;
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
            // Сеть появилась — проверяем сервер
            await CheckNowAsync();
        }
        else
        {
            IsOnline = false;
        }
    }
}
