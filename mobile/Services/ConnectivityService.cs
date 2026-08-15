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
    private const string PingUrl = "https://api.kitdiag.ru/";
    private const int TimeoutMs = 5_000;

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
        // Ждём 500 мс чтобы дать системе инициализироваться
        await Task.Delay(500);
        await CheckNowAsync();
        _checked = true;
    }

    /// <summary>
    /// Принудительная проверка прямо сейчас.
    /// </summary>
    public async Task<bool> CheckNowAsync()
    {
        // Быстрая проверка: NetworkAccess
        var netAccess = Connectivity.Current.NetworkAccess;
        if (netAccess != NetworkAccess.Internet)
        {
            IsOnline = false;
            return false;
        }

        // Настоящий пинг сервера
        try
        {
            var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs));
            var response = await _http.GetAsync(PingUrl, cts.Token);
            IsOnline = response.IsSuccessStatusCode;
        }
        catch
        {
            IsOnline = false;
        }

        return IsOnline;
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
