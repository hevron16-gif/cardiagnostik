using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;

namespace CarDiagnosticApp.Pages;

public partial class HistoryPage : ContentPage
{
    private readonly ApiService? _api;
    private readonly LocalDatabase _db = new();
    private readonly OfflineDatabase _offlineDb = new();
    private readonly OfflineCacheService _offlineCache;
    private readonly SyncService? _sync;
    private List<HistoryItem>? _items;

    public HistoryPage()
    {
        InitializeComponent();
        try
        {
            _api = IPlatformApplication.Current?.Services?.GetService<ApiService>();
        }
        catch
        {
            _api = null;
        }

        _offlineCache = new OfflineCacheService(_offlineDb);
        try
        {
            _sync = _api != null ? new SyncService(_api, _db, _offlineDb) : null;
        }
        catch
        {
            _sync = null;
        }

        _ = _offlineDb.InitAsync();
    }

    /// <summary>
    /// Загружает историю при появлении страницы на экране.
    /// Сначала показывает кеш из локальной БД, затем синхронизируется с сервером.
    /// </summary>
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadHistory();

        // Фоновая синхронизация
        _ = SyncInBackground();
    }

    /// <summary>
    /// Загружает историю: локальная БД → сервер → слияние → локальная БД.
    /// </summary>
    private async Task LoadHistory()
    {
        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        // 1. Показываем кеш из локальной БД мгновенно
        var cached = await _db.GetAllAsItemsAsync();
        if (cached is { Count: > 0 })
        {
            _items = cached;
            HistoryList.ItemsSource = cached;
            LabelCount.Text = $"Всего записей: {cached.Count}";
            UpdateStats();
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
        }

        // 2. Запрашиваем свежие данные с сервера
        List<HistoryItem>? serverItems = null;
        try
        {
            if (_api == null) throw new InvalidOperationException("API недоступен");
            serverItems = await _api.GetHistory();
        }
        catch
        {
            // Нет сети — остаёмся на кеше
            if (_items is not { Count: > 0 })
            {
                _items = null;
                HistoryList.ItemsSource = null;
                LabelCount.Text = "Нет соединения с сервером";
                StatsBar.IsVisible = false;
            }
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            return;
        }

        // 3. Сохраняем серверные данные в локальную БД (статусы не перезаписываем)
        if (serverItems is { Count: > 0 })
        {
            foreach (var item in serverItems)
            {
                await _db.UpsertAsync(HistoryRecord.FromServerItem(item));
            }
        }

        // 4. Загружаем итоговый список из БД (с сохранёнными статусами)
        var merged = await _db.GetAllAsItemsAsync();
        _items = merged;
        HistoryList.ItemsSource = merged;

        if (merged is { Count: > 0 })
        {
            LabelCount.Text = $"Всего записей: {merged.Count}";
            UpdateStats();
        }
        else
        {
            LabelCount.Text = "Записей пока нет";
            StatsBar.IsVisible = false;
        }

        LoadingIndicator.IsRunning = false;
        LoadingIndicator.IsVisible = false;
    }

    /// <summary>
    /// Обновляет строку статистики: всего / решено / в процессе / не решено.
    /// </summary>
    private void UpdateStats()
    {
        if (_items is not { Count: > 0 })
        {
            StatsBar.IsVisible = false;
            return;
        }

        var total = _items.Count;
        var solved = _items.Count(i => i.Status == HistoryItem.StatusSolved);
        var inProgress = _items.Count(i => i.Status == HistoryItem.StatusInProgress);
        var unsolved = _items.Count(i => i.Status == HistoryItem.StatusUnsolved);

        StatTotal.Text = $"Всего: {total}";
        StatSolved.Text = $"Решено: {solved}";
        StatInProgress.Text = $"В процессе: {inProgress}";
        StatUnsolved.Text = $"Не решено: {unsolved}";

        StatsBar.IsVisible = true;
    }

    /// <summary>
    /// Переключение статуса по кругу + сохранение в SQLite.
    /// </summary>
    private async void OnStatusTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not HistoryItem item) return;

        item.CycleStatus();
        await _db.UpdateStatusAsync(item.DbId, item.Status);
        UpdateStats();
    }

    /// <summary>
    /// Очищает историю на сервере и в локальной БД, после подтверждения.
    /// </summary>
    private async void OnClearHistoryClicked(object? sender, TappedEventArgs e)
    {
        var confirm = await DisplayAlert(
            "Очистить историю",
            "Все записи диагностик будут удалены безвозвратно. Продолжить?",
            "Да, очистить",
            "Отмена");

        if (!confirm) return;

        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        if (_api == null)
        {
            await DisplayAlert("Ошибка", "API недоступен (офлайн).", "OK");
            LoadingIndicator.IsRunning = false;
            LoadingIndicator.IsVisible = false;
            return;
        }
        var ok = await _api.ClearHistory();

        if (ok)
        {
            await _db.DeleteAllAsync();
            _items = null;
            HistoryList.ItemsSource = null;
            LabelCount.Text = "Записей пока нет";
            StatsBar.IsVisible = false;
        }
        else
        {
            await DisplayAlert("Ошибка", "Не удалось очистить историю.", "OK");
        }

        LoadingIndicator.IsRunning = false;
        LoadingIndicator.IsVisible = false;
    }

    /// <summary>
    /// Повторная диагностика — заново запрашивает AI-ответ
    /// и открывает ResultPage.
    /// </summary>
    private async void OnRetryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not HistoryItem item) return;
        if (string.IsNullOrWhiteSpace(item.error_code)) return;

        LoadingIndicator.IsVisible = true;
        LoadingIndicator.IsRunning = true;

        string? result = null;

        try
        {
            if (_api != null)
            {
                result = await _api.Diagnose(
                    item.error_code,
                    item.car_brand ?? "",
                    item.car_model ?? "");
            }
        }
        catch
        {
            // Сервер недоступен — ищем офлайн
        }

        string diagnosisText;

        if (result != null)
        {
            diagnosisText = result;
        }
        else
        {
            // Каскадный офлайн-поиск
            var offlineResult = await _offlineCache.OfflineDiagnoseAsync(
                item.error_code,
                item.car_brand ?? "",
                item.car_model ?? "");

            if (offlineResult == null)
            {
                LoadingIndicator.IsRunning = false;
                LoadingIndicator.IsVisible = false;
                await DisplayAlert(
                    "Нет соединения",
                    $"Сервер недоступен, и локальных данных по {item.error_code} не найдено.",
                    "OK");
                return;
            }

            diagnosisText = offlineResult.Value.Diagnosis;
        }

        LoadingIndicator.IsRunning = false;
        LoadingIndicator.IsVisible = false;

        await Navigation.PushAsync(new ResultPage(diagnosisText));
    }

    /// <summary>
    /// Фоновая синхронизация с сервером: новые диагнозы, отзывы.
    /// </summary>
    private async Task SyncInBackground()
    {
        try
        {
            if (_sync == null) return;
            var newCount = await _sync.SyncAsync();
            if (newCount > 0)
            {
                // Обновляем список после синхронизации
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    await LoadHistory();
                });
            }
        }
        catch
        {
            // Тихая ошибка
        }
    }
}
