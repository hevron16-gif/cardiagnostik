using CarDiagnosticApp.Models;
using System.Text;
using System.Text.Json;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис автообновления: периодическая синхронизация
/// локальной БД с сервером, отправка офлайн-отзывов.
/// Использует единую OfflineDatabase для всего офлайн-хранения.
/// </summary>
public class SyncService
{

    private static SyncService? _instance;
public static SyncService Instance => 
    _instance ??= IPlatformApplication.Current!.Services.GetRequiredService<SyncService>();
    private readonly ApiService _api;
    private readonly LocalDatabase _db;
    private readonly OfflineDatabase _offlineDb;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Вызывается при завершении синхронизации.
    /// Параметр: количество новых записей (0 — ничего нового).
    /// </summary>
    public event Action<int>? SyncCompleted;

    private const int MAX_RETRIES = 3;
    private const int RETRY_DELAY_MS = 1000;
    private const int CONNECTION_TIMEOUT_SEC = 45;

    public SyncService(ApiService api, LocalDatabase db, OfflineDatabase offlineDb)
    {
        _api = api;
        _db = db;
        _offlineDb = offlineDb;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(CONNECTION_TIMEOUT_SEC)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "CarDiagnosticApp-Sync/1.0");
    }

    /// <summary>
    /// Конструктор по умолчанию — создаёт зависимости автоматически.
    /// </summary>
    public SyncService() : this(IPlatformApplication.Current!.Services.GetRequiredService<ApiService>(), new LocalDatabase(), new OfflineDatabase())
    {
    }

    /// <summary>
    /// Выполняет полный цикл синхронизации:
    /// 1. Отправляет pending-отзывы
    /// 2. Загружает дельту диагнозов с сервера
    /// 3. Загружает дельту БЗ с сервера
    /// 4. Обновляет локальную БД и офлайн-кеш
    /// </summary>
    public async Task<int> SyncAsync(string? carBrand = null)
    {
        await _offlineDb.InitAsync();
        int totalNew = 0;

        // 1. Отправляем офлайн-отзывы
        await FlushFeedbackAsync();

        // 2. Отправляем офлайн-диагнозы на сервер
        await FlushUploadsAsync();

        // 3. Загружаем дельту с сервера
        var lastSync = await _offlineDb.SyncMeta.GetLastSyncTimeAsync();
        var sinceParam = lastSync.HasValue
            ? lastSync.Value.ToString("o")
            : "";

        return await FetchAndApplyAsync(sinceParam, carBrand);
    }

    /// <summary>
    /// Проверяет сводку сервера — сколько новых записей доступно,
    /// без скачивания самих данных. Быстрый предварительный запрос.
    /// Возвращает (totalNew, serverTime).
    /// </summary>
    public async Task<(int NewCount, DateTime ServerTime)> GetServerSummaryAsync()
    {
        await _offlineDb.InitAsync();
        var url = $"{_api.BaseUrl}/sync/summary";

        var response = await SendWithRetryAsync(client => client.GetAsync(url));
        if (response == null)
            return (0, DateTime.MinValue);

        try
        {
            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var json = Encoding.UTF8.GetString(responseBytes);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            DateTime serverTime = DateTime.MinValue;
            if (root.TryGetProperty("server_time", out var st) &&
                DateTime.TryParse(st.GetString(), out var parsed))
                serverTime = parsed;

            var totalDiagnoses = root.TryGetProperty("total_diagnoses", out var td)
                ? td.GetInt32() : 0;
            var totalKnowledge = root.TryGetProperty("total_knowledge", out var tk)
                ? tk.GetInt32() : 0;

            var localDownloaded = await _offlineDb.SyncMeta.GetTotalDownloadedAsync();
            int serverTotal = totalDiagnoses + totalKnowledge;
            int estimatedNew = Math.Max(0, serverTotal - localDownloaded);

            return (estimatedNew, serverTime);
        }
        catch
        {
            return (0, DateTime.MinValue);
        }
    }

    /// <summary>
    /// Скачивает и применяет дельту с сервера.
    /// </summary>
    private async Task<int> FetchAndApplyAsync(string sinceParam, string? carBrand = null)
    {
        int totalNew = 0;

        var url = $"{_api.BaseUrl}/sync?since={Uri.EscapeDataString(sinceParam)}&limit=100";
        if (!string.IsNullOrEmpty(carBrand))
            url += $"&car_brand={Uri.EscapeDataString(carBrand)}";

        try
        {
            var response = await SendWithRetryAsync(client => client.GetAsync(url));
            if (response == null)
                return totalNew;

            var responseBytes = await response.Content.ReadAsByteArrayAsync();
            var json = Encoding.UTF8.GetString(responseBytes);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Сохраняем server_time для следующего since
            if (root.TryGetProperty("server_time", out var serverTime))
            {
                if (DateTime.TryParse(serverTime.GetString(), out var parsed))
                    await _offlineDb.SyncMeta.SetLastSyncTimeAsync(parsed);
            }

            // 3. Сохраняем диагнозы в LocalDatabase + офлайн-кеш
            if (root.TryGetProperty("diagnoses", out var diagnoses) && diagnoses.ValueKind == JsonValueKind.Array)
            {
                foreach (var diag in diagnoses.EnumerateArray())
                {
                    var errorCode = diag.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "";
                    var brand = diag.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "";
                    var model = diag.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "";
                    var diagText = diag.TryGetProperty("diagnosis", out var dd) ? dd.GetString() ?? "" : "";
                    var timestamp = diag.TryGetProperty("timestamp", out var ts) ? ts.GetString() ?? "" : "";
                    var snippet = diagText.Length > 200 ? diagText[..200] + "…" : diagText;

                    // История (сохраняем статус)
                    var record = new HistoryRecord
                    {
                        ErrorCode = errorCode,
                        CarBrand = brand,
                        CarModel = model,
                        Snippet = snippet,
                        Diagnosis = diagText,
                        Timestamp = timestamp,
                    };
                    await _db.UpsertAsync(record);

                    // Офлайн-кеш
                    await _offlineDb.Cache.UpsertAsync(
                        errorCode, brand, model, diagText,
                        source: diag.TryGetProperty("source", out var src) ? src.GetString() ?? "online" : "online");

                    totalNew++;
                }
            }

            // 4. Сохраняем БЗ в офлайн-кеш знаний
            if (root.TryGetProperty("knowledge", out var knowledge) && knowledge.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in knowledge.EnumerateArray())
                {
                    var errorCode = entry.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "";
                    var brand = entry.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "";
                    var model = entry.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "";
                    var diagText = entry.TryGetProperty("diagnosis", out var dd) ? dd.GetString() ?? "" : "";
                    var source = entry.TryGetProperty("source", out var src) ? src.GetString() ?? "verified" : "verified";

                    await _offlineDb.Knowledge.UpsertAsync(
                        errorCode, brand, model, diagText, source);

                    totalNew++;
                }
            }

            // Обновляем счётчики
            await _offlineDb.SyncMeta.AddToDownloadedAsync(totalNew);
        }
        catch
        {
            // Нет сети — без паники
        }

        SyncCompleted?.Invoke(totalNew);
        return totalNew;
    }

    /// <summary>
    /// Отправляет все ожидающие отзывы на сервер.
    /// </summary>
    public async Task<int> FlushFeedbackAsync()
    {
        await _offlineDb.InitAsync();
        var pending = await _offlineDb.Feedback.GetAllAsync();

        if (pending.Count == 0)
            return 0;

        int sent = 0;
        foreach (var item in pending)
        {
            try
            {
                await _api.SendFeedback(
                    item.ErrorCode, item.Helpful,
                    item.CarBrand, item.CarModel,
                    item.Diagnosis, item.Comment);

                await _offlineDb.Feedback.RemoveAsync(item);
                sent++;
            }
            catch
            {
                await _offlineDb.Feedback.IncrementRetryAsync(item);
            }
        }

        return sent;
    }

    /// <summary>
    /// Отправляет все ожидающие офлайн-диагнозы на сервер.
    /// </summary>
    public async Task<int> FlushUploadsAsync()
    {
        await _offlineDb.InitAsync();
        var pending = await _offlineDb.Uploads.GetAllAsync();

        if (pending.Count == 0)
            return 0;

        int sent = 0;
        foreach (var item in pending)
        {
            var ok = await _api.UploadDiagnosisAsync(
                item.ErrorCode, item.CarBrand, item.CarModel,
                item.Diagnosis, item.Source);

            if (ok)
            {
                await _offlineDb.Uploads.RemoveAsync(item);
                sent++;
            }
            else
            {
                await _offlineDb.Uploads.IncrementRetryAsync(item);
            }
        }

        return sent;
    }

    /// <summary>
    /// Принудительно сбрасывает метку времени последней синхронизации,
    /// чтобы следующий SyncAsync загрузил всё заново.
    /// </summary>
    public async Task ResetSyncTimestampAsync()
    {
        await _offlineDb.SyncMeta.SetLastSyncTimeAsync(DateTime.MinValue);
    }

    // ═══════════════════════════════════════════════════════════
    // Этап 7: Облачная синхронизация между пользователями
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Загружает локальную базу знаний на сервер (bulk-загрузка).
    /// </summary>
    public async Task<(int uploaded, int skipped)> UploadKnowledgeAsync()
    {
        var learningDb = new LearningDbService();
        var all = await learningDb.GetStaleKnowledgeAsync(0, 3650);

        if (all.Count == 0)
            return (0, 0);

        var entries = all.Select(k => new
        {
            error_code = k.ErrorCode,
            car_brand = k.CarBrand,
            car_model = k.CarModel,
            diagnosis = k.LastDiagnosisText ?? "",
            source = "client_sync",
            confidence = k.Confidence,
            first_seen_at = k.FirstSeenAt.ToString("o"),
            last_seen_at = k.LastSeenAt.ToString("o"),
        }).ToList();

        var payload = new
        {
            entries,
            client_id = GetClientId()
        };

        var json = JsonSerializer.Serialize(payload);
        using var http = CreateHttpClient();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/sync/upload-knowledge", content);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Upload knowledge failed: {resp.StatusCode}");

        var resultJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(resultJson);
        int added = doc.RootElement.GetProperty("added").GetInt32();
        int skipped = doc.RootElement.GetProperty("skipped").GetInt32();
        return (added, skipped);
    }

    /// <summary>
    /// Загружает локальную историю диагностик на сервер.
    /// </summary>
    public async Task<int> UploadDiagnosticsAsync()
    {
        var historyService = new ErrorHistoryService();
        var all = await historyService.GetAllErrorsAsync();

        if (all.Count == 0)
            return 0;

        var entries = all.Select(h => new
        {
            error_code = h.ErrorCode,
            car_brand = h.Brand,
            car_model = h.Model,
            error_type = h.ErrorType,
            diagnosis_snippet = h.DiagnosisSnippet ?? "",
            risk_score = h.RiskScore,
            is_recurring = h.IsRecurring,
            detected_at = h.DetectedAt.ToString("o"),
        }).ToList();

        var payload = new
        {
            entries,
            client_id = GetClientId()
        };

        var json = JsonSerializer.Serialize(payload);
        using var http = CreateHttpClient();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/sync/upload-diagnostics", content);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Upload diagnostics failed: {resp.StatusCode}");

        var resultJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(resultJson);
        return doc.RootElement.GetProperty("added").GetInt32();
    }

    /// <summary>
    /// Загружает метаданные локальных схем на сервер.
    /// </summary>
    public async Task<(int uploaded, int skipped)> UploadDiagramsAsync()
    {
        var diagramDb = new DiagramDbService();
        // Используем GetPendingRequests как прокси — загружаем все схемы
        var diagrams = await diagramDb.GetPendingRequestsAsync();
        var totalDiagrams = await diagramDb.GetDiagramCountAsync();

        // GetPendingRequests возвращает PendingDiagramRequest, а не DiagramRecord.
        // Загружаем метаданные из DiagramDbService...
        // Упростим: перебираем все сохранённые запросы (включая found)
        if (diagrams.Count == 0)
            return (0, 0);

        var entries = diagrams.Select(d => new
        {
            error_code = d.ErrorCode,
            car_brand = d.CarBrand,
            car_model = d.CarModel,
            title = "",
            description = $"Запрос: {d.SearchQuery}",
            source_url = d.SearchQuery,
            created_at = d.CreatedAt.ToString("o"),
        }).ToList();

        var payload = new
        {
            entries,
            client_id = GetClientId()
        };

        var json = JsonSerializer.Serialize(payload);
        using var http = CreateHttpClient();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/sync/upload-diagrams", content);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Upload diagrams failed: {resp.StatusCode}");

        var resultJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(resultJson);
        int added = doc.RootElement.GetProperty("added").GetInt32();
        int skipped = doc.RootElement.GetProperty("skipped").GetInt32();
        return (added, skipped);
    }

    /// <summary>
    /// Загружает базу знаний с сервера и вливает в локальную БД.
    /// </summary>
    public async Task<int> DownloadAndMergeKnowledgeAsync()
    {
        var learningDb = new LearningDbService();
        var lastSync = await _offlineDb.SyncMeta.GetLastKnowledgeSyncTimeAsync() ?? DateTime.MinValue;
        var since = lastSync > DateTime.MinValue ? lastSync.ToString("o") : "";

        using var http = CreateHttpClient();
        var url = $"{_api.BaseUrl}/sync/knowledge?since={Uri.EscapeDataString(since)}&limit=200";
        var resp = await http.GetAsync(url);

        if (!resp.IsSuccessStatusCode)
            return 0;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries");

        int merged = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var errorCode = entry.GetProperty("error_code").GetString() ?? "";
            var carBrand = entry.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "";
            var carModel = entry.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "";
            var diagnosis = entry.TryGetProperty("diagnosis", out var d) ? d.GetString() ?? "" : "";
            var source = entry.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "";
            var confidence = entry.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5;

            await learningDb.UpsertSyncKnowledgeAsync(errorCode, carBrand, carModel, diagnosis, source, confidence);
            merged++;
        }

        // Обновляем метку времени
        await _offlineDb.SyncMeta.SetLastKnowledgeSyncTimeAsync(DateTime.UtcNow);

        return merged;
    }

    /// <summary>
    /// Загружает метаданные схем с сервера и вливает в локальную БД.
    /// </summary>
    public async Task<int> DownloadAndMergeDiagramsAsync()
    {
        var diagramDb = new DiagramDbService();
        var lastSync = await _offlineDb.SyncMeta.GetLastDiagramSyncTimeAsync() ?? DateTime.MinValue;
        var since = lastSync > DateTime.MinValue ? lastSync.ToString("o") : "";

        using var http = CreateHttpClient();
        var url = $"{_api.BaseUrl}/sync/diagrams?since={Uri.EscapeDataString(since)}&limit=200";
        var resp = await http.GetAsync(url);

        if (!resp.IsSuccessStatusCode)
            return 0;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries");

        int merged = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            var errorCode = entry.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "";
            var carBrand = entry.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "";
            var carModel = entry.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "";
            var title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            var description = entry.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
            var sourceUrl = entry.TryGetProperty("source_url", out var su) ? su.GetString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(sourceUrl))
                continue;

            await diagramDb.SavePendingRequestAsync(carBrand, carModel, errorCode, sourceUrl);
            merged++;
        }

        await _offlineDb.SyncMeta.SetLastDiagramSyncTimeAsync(DateTime.UtcNow);

        return merged;
    }

    /// <summary>
    /// Загружает историю диагностик с сервера и вливает в локальную БД.
    /// </summary>
    public async Task<int> DownloadAndMergeDiagnosticsAsync()
    {
        var historyService = new ErrorHistoryService();
        var lastSync = await _offlineDb.SyncMeta.GetLastDiagnosticSyncTimeAsync() ?? DateTime.MinValue;
        var since = lastSync > DateTime.MinValue ? lastSync.ToString("o") : "";

        using var http = CreateHttpClient();
        var url = $"{_api.BaseUrl}/sync?since={Uri.EscapeDataString(since)}&limit=200";
        var resp = await http.GetAsync(url);

        if (!resp.IsSuccessStatusCode)
            return 0;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        int merged = 0;

        // Обрабатываем диагнозы
        if (doc.RootElement.TryGetProperty("diagnoses", out var diagnoses))
        {
            foreach (var entry in diagnoses.EnumerateArray())
            {
                var errorCode = entry.GetProperty("error_code").GetString() ?? "";
                var carBrand = entry.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "";
                var carModel = entry.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "";
                var snippet = entry.TryGetProperty("snippet", out var sn) ? sn.GetString() ?? "" : "";

                await historyService.SaveErrorAsync(errorCode, carBrand, carModel, "Synced", snippet);
                merged++;
            }
        }

        await _offlineDb.SyncMeta.SetLastDiagnosticSyncTimeAsync(DateTime.UtcNow);

        return merged;
    }

    /// <summary>
    /// Полная синхронизация: загрузка локальных данных на сервер и получение общих данных.
    /// Возвращает сводку.
    /// </summary>
    public async Task<SyncSummary> FullSyncAsync()
    {
        var summary = new SyncSummary();

        // 1. Загружаем локальные данные
        try
        {
            var (kAdded, kSkipped) = await UploadKnowledgeAsync();
            summary.KnowledgeUploaded = kAdded;
            summary.KnowledgeSkipped = kSkipped;
        }
        catch (Exception ex) { summary.Errors.Add($"UploadKnowledge: {ex.Message}"); }

        try
        {
            summary.DiagnosticsUploaded = await UploadDiagnosticsAsync();
        }
        catch (Exception ex) { summary.Errors.Add($"UploadDiagnostics: {ex.Message}"); }

        try
        {
            var (dAdded, dSkipped) = await UploadDiagramsAsync();
            summary.DiagramsUploaded = dAdded;
            summary.DiagramsSkipped = dSkipped;
        }
        catch (Exception ex) { summary.Errors.Add($"UploadDiagrams: {ex.Message}"); }

        // 2. Загружаем общие данные с сервера
        try
        {
            summary.KnowledgeDownloaded = await DownloadAndMergeKnowledgeAsync();
        }
        catch (Exception ex) { summary.Errors.Add($"DownloadKnowledge: {ex.Message}"); }

        try
        {
            summary.DiagramsDownloaded = await DownloadAndMergeDiagramsAsync();
        }
        catch (Exception ex) { summary.Errors.Add($"DownloadDiagrams: {ex.Message}"); }

        try
        {
            summary.DiagnosticsDownloaded = await DownloadAndMergeDiagnosticsAsync();
        }
        catch (Exception ex) { summary.Errors.Add($"DownloadDiagnostics: {ex.Message}"); }

        summary.SyncedAt = DateTime.UtcNow;
        return summary;
    }

    private static string GetClientId()
    {
        try
        {
            var prefs = Preferences.Default;
            var id = prefs.Get("client_id", "");
            if (string.IsNullOrEmpty(id))
            {
                id = Guid.NewGuid().ToString("N")[..12];
                prefs.Set("client_id", id);
            }
            return id;
        }
        catch
        {
            return "unknown";
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(CONNECTION_TIMEOUT_SEC) };
        client.DefaultRequestHeaders.Add("User-Agent", "CarDiagnosticApp-Sync/1.0");
        return client;
    }

    /// <summary>
    /// Выполняет HTTP-запрос с ретраями при connection reset, timeout, 502/503.
    /// Возвращает null если все попытки провалились.
    /// </summary>
    private static async Task<HttpResponseMessage?> SendWithRetryAsync(
        Func<HttpClient, Task<HttpResponseMessage>> request,
        HttpClient? client = null)
    {
        var http = client ?? CreateHttpClient();

        for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
        {
            try
            {
                var response = await request(http);
                if (response.IsSuccessStatusCode)
                    return response;

                // Ретраим только на серверные ошибки (502, 503) и 429
                if (response.StatusCode is
                    System.Net.HttpStatusCode.BadGateway or
                    System.Net.HttpStatusCode.ServiceUnavailable or
                    System.Net.HttpStatusCode.GatewayTimeout or
                    System.Net.HttpStatusCode.TooManyRequests)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Sync] {response.StatusCode} on attempt {attempt}, retrying...");
                    response.Dispose();
                    await Task.Delay(RETRY_DELAY_MS * attempt);
                    continue;
                }

                response.Dispose();
                return null; // Клиентская ошибка — не ретраим
            }
            catch (HttpRequestException ex) when (
                ex.Message.Contains("connection was reset", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Sync] Connection reset on attempt {attempt}/{MAX_RETRIES}: {ex.Message}");
                if (attempt < MAX_RETRIES)
                    await Task.Delay(RETRY_DELAY_MS * attempt);
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Sync] Timeout on attempt {attempt}/{MAX_RETRIES}");
                if (attempt < MAX_RETRIES)
                    await Task.Delay(RETRY_DELAY_MS * attempt);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Sync] HTTP error on attempt {attempt}/{MAX_RETRIES}: {ex.Message}");
                if (attempt < MAX_RETRIES)
                    await Task.Delay(RETRY_DELAY_MS * attempt);
            }
        }

        return null;
    }

    public class SyncSummary
    {
        public int KnowledgeUploaded { get; set; }
        public int KnowledgeSkipped { get; set; }
        public int KnowledgeDownloaded { get; set; }
        public int DiagnosticsUploaded { get; set; }
        public int DiagnosticsDownloaded { get; set; }
        public int DiagramsUploaded { get; set; }
        public int DiagramsSkipped { get; set; }
        public int DiagramsDownloaded { get; set; }
        public DateTime SyncedAt { get; set; }
        public List<string> Errors { get; set; } = new();

        public string SummaryText => string.Join("\n",
            $"📤 Загружено: знаний {KnowledgeUploaded} ({KnowledgeSkipped} проп.), " +
            $"диагнозов {DiagnosticsUploaded}, схем {DiagramsUploaded}",
            $"📥 Получено: знаний {KnowledgeDownloaded}, " +
            $"схем {DiagramsDownloaded}, диагнозов {DiagnosticsDownloaded}",
            Errors.Count > 0 ? $"⚠️ Ошибки: {Errors.Count}" : "✅ Без ошибок"
        );
    }

    // ═══════════════════════════════════════════════════════════
    // Этап 7.1: Новые эндпоинты — submit_solution, get_updates, sync_status
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Отправляет решение пользователя на сервер (POST /submit_solution).
    /// </summary>
    public async Task<bool> SubmitSolutionAsync(
        string errorCode,
        string diagnosis,
        string carBrand = "",
        string carModel = "",
        string source = "user_submit")
    {
        try
        {
            var payload = new
            {
                error_code = errorCode,
                car_brand = carBrand,
                car_model = carModel,
                diagnosis = diagnosis,
                source = source,
                user_id = GetClientId()
            };

            var json = JsonSerializer.Serialize(payload);
            using var http = CreateHttpClient();
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"{_api.BaseUrl}/submit_solution", content);

            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Получает дельту обновлений (решения + схемы) с сервера.
    /// Использует GET /get_updates?since=...&limit=...&type=...
    /// </summary>
    public async Task<UpdateResponse> GetUpdatesAsync(
        DateTime? since = null,
        int limit = 50,
        string? type = null)
    {
        var result = new UpdateResponse();

        try
        {
            var sinceParam = since.HasValue && since.Value > DateTime.MinValue
                ? since.Value.ToString("o")
                : "";

            var url = $"{_api.BaseUrl}/get_updates" +
                      $"?since={Uri.EscapeDataString(sinceParam)}" +
                      $"&limit={limit}";

            if (!string.IsNullOrWhiteSpace(type))
                url += $"&type={Uri.EscapeDataString(type)}";

            using var http = CreateHttpClient();
            var resp = await http.GetAsync(url);

            if (!resp.IsSuccessStatusCode)
                return result;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            // server_time
            if (doc.RootElement.TryGetProperty("server_time", out var st))
            {
                if (DateTime.TryParse(st.GetString(), out var parsed))
                    result.ServerTime = parsed;
            }

            // solutions
            if (doc.RootElement.TryGetProperty("solutions", out var sols))
            {
                foreach (var s in sols.EnumerateArray())
                {
                    result.Solutions.Add(new SyncedSolution
                    {
                        ErrorCode = s.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "",
                        CarBrand = s.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "",
                        CarModel = s.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "",
                        Diagnosis = s.TryGetProperty("diagnosis", out var d) ? d.GetString() ?? "" : "",
                        Source = s.TryGetProperty("source", out var src) ? src.GetString() ?? "" : "",
                        Confidence = s.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0.5,
                        HelpfulCount = s.TryGetProperty("helpful_count", out var hc) ? hc.GetInt32() : 0,
                        NotHelpfulCount = s.TryGetProperty("not_helpful_count", out var nhc) ? nhc.GetInt32() : 0,
                        UpdatedAt = s.TryGetProperty("updated_at", out var ua) ? ua.GetString() ?? "" : "",
                    });
                }
            }

            result.SolutionsCount = doc.RootElement.TryGetProperty("solutions_count", out var sc)
                ? sc.GetInt32() : result.Solutions.Count;

            // diagrams
            if (doc.RootElement.TryGetProperty("diagrams", out var diags))
            {
                foreach (var d in diags.EnumerateArray())
                {
                    result.Diagrams.Add(new SyncedDiagram
                    {
                        ErrorCode = d.TryGetProperty("error_code", out var ec) ? ec.GetString() ?? "" : "",
                        CarBrand = d.TryGetProperty("car_brand", out var cb) ? cb.GetString() ?? "" : "",
                        CarModel = d.TryGetProperty("car_model", out var cm) ? cm.GetString() ?? "" : "",
                        Title = d.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Description = d.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        SourceUrl = d.TryGetProperty("source_url", out var su) ? su.GetString() ?? "" : "",
                        CreatedAt = d.TryGetProperty("created_at", out var ca) ? ca.GetString() ?? "" : "",
                    });
                }
            }

            result.DiagramsCount = doc.RootElement.TryGetProperty("diagrams_count", out var dc)
                ? dc.GetInt32() : result.Diagrams.Count;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Загружает дельту с сервера через /get_updates и применяет к локальным БД.
    /// </summary>
    public async Task<int> FetchAndApplyUpdatesAsync(string? carBrand = null)
    {
        await _offlineDb.InitAsync();

        var lastSync = await _offlineDb.SyncMeta.GetLastFullSyncTimeAsync() ?? DateTime.MinValue;
        var since = lastSync > DateTime.MinValue ? lastSync : (DateTime?)null;

        var updates = await GetUpdatesAsync(since, limit: 100);

        if (!string.IsNullOrEmpty(updates.Error))
            return 0;

        int merged = 0;
        var learningDb = new LearningDbService();
        var diagramDb = new DiagramDbService();

        // Вливаем решения
        foreach (var sol in updates.Solutions)
        {
            if (string.IsNullOrWhiteSpace(sol.ErrorCode) || string.IsNullOrWhiteSpace(sol.Diagnosis))
                continue;

            try
            {
                await learningDb.UpsertSyncKnowledgeAsync(
                    sol.ErrorCode, sol.CarBrand, sol.CarModel,
                    sol.Diagnosis, sol.Source, sol.Confidence);
                merged++;
            }
            catch { /* skip malformed */ }
        }

        // Вливаем схемы
        foreach (var diag in updates.Diagrams)
        {
            if (string.IsNullOrWhiteSpace(diag.SourceUrl))
                continue;

            try
            {
                await diagramDb.SavePendingRequestAsync(
                    diag.CarBrand, diag.CarModel, diag.ErrorCode, diag.SourceUrl);
                merged++;
            }
            catch { /* skip malformed */ }
        }

        // Обновляем метку
        if (updates.ServerTime > DateTime.MinValue)
            await _offlineDb.SyncMeta.SetLastFullSyncTimeAsync(updates.ServerTime);

        SyncCompleted?.Invoke(merged);
        return merged;
    }

    /// <summary>
    /// Получает полный статус синхронизации сервера (GET /sync_status).
    /// </summary>
    public async Task<SyncStatusResponse?> GetSyncStatusAsync()
    {
        try
        {
            using var http = CreateHttpClient();
            var resp = await http.GetAsync($"{_api.BaseUrl}/sync_status");

            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var status = new SyncStatusResponse();

            if (doc.RootElement.TryGetProperty("server_time", out var st) &&
                DateTime.TryParse(st.GetString(), out var parsed))
                status.ServerTime = parsed;

            status.UserSubmitted = doc.RootElement.TryGetProperty("user_submitted", out var us)
                ? us.GetInt32() : 0;
            status.TotalRecords = doc.RootElement.TryGetProperty("total_records", out var tr)
                ? tr.GetInt32() : 0;
            status.ChromaDb = doc.RootElement.TryGetProperty("chromadb", out var ch)
                ? ch.GetString() ?? "unknown" : "unknown";
            status.CachedSolutions = doc.RootElement.TryGetProperty("cached_solutions", out var cs)
                ? cs.GetInt32() : 0;

            // databases
            if (doc.RootElement.TryGetProperty("databases", out var dbs))
            {
                foreach (var db in dbs.EnumerateObject())
                {
                    var info = new DbStatus
                    {
                        Name = db.Name,
                        Records = db.Value.TryGetProperty("records", out var rec) ? rec.GetInt32() : 0,
                    };
                    if (db.Value.TryGetProperty("last_updated", out var lu) &&
                        lu.ValueKind != JsonValueKind.Null &&
                        !string.IsNullOrEmpty(lu.GetString()))
                    {
                        if (DateTime.TryParse(lu.GetString(), out var luDt))
                            info.LastUpdated = luDt;
                    }
                    status.Databases.Add(info);
                }
            }

            return status;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Быстрая проверка: сколько новых решений доступно (через /sync_status).
    /// </summary>
    public async Task<int> GetAvailableUpdatesCountAsync()
    {
        var status = await GetSyncStatusAsync();
        if (status == null)
            return 0;

        // Сравниваем общее кол-во записей на сервере с локально загруженным
        var localDownloaded = await _offlineDb.SyncMeta.GetTotalDownloadedAsync();
        return Math.Max(0, status.TotalRecords - localDownloaded);
    }

    // ─── Модели для новых эндпоинтов ───

    public class SyncedSolution
    {
        public string ErrorCode { get; set; } = "";
        public string CarBrand { get; set; } = "";
        public string CarModel { get; set; } = "";
        public string Diagnosis { get; set; } = "";
        public string Source { get; set; } = "";
        public double Confidence { get; set; }
        public int HelpfulCount { get; set; }
        public int NotHelpfulCount { get; set; }
        public string UpdatedAt { get; set; } = "";
    }

    public class SyncedDiagram
    {
        public string ErrorCode { get; set; } = "";
        public string CarBrand { get; set; } = "";
        public string CarModel { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string SourceUrl { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }

    public class UpdateResponse
    {
        public DateTime ServerTime { get; set; }
        public List<SyncedSolution> Solutions { get; set; } = new();
        public List<SyncedDiagram> Diagrams { get; set; } = new();
        public int SolutionsCount { get; set; }
        public int DiagramsCount { get; set; }
        public string? Error { get; set; }

        public string SummaryText => Error != null
            ? $"❌ {Error}"
            : $"📥 Решений: {SolutionsCount}, схем: {DiagramsCount} | 🕒 {ServerTime:HH:mm}";
    }

    public class SyncStatusResponse
    {
        public DateTime ServerTime { get; set; }
        public List<DbStatus> Databases { get; set; } = new();
        public int UserSubmitted { get; set; }
        public int TotalRecords { get; set; }
        public string ChromaDb { get; set; } = "unknown";
        public int CachedSolutions { get; set; }

        public string SummaryText => string.Join("\n",
            $"🕒 Сервер: {ServerTime:HH:mm}",
            $"📊 Всего записей: {TotalRecords} (польз.: {UserSubmitted})",
            $"🧠 ChromaDB: {ChromaDb}",
            $"📦 Кэш: {CachedSolutions} решений"
        );
    }

    public class DbStatus
    {
        public string Name { get; set; } = "";
        public int Records { get; set; }
        public DateTime? LastUpdated { get; set; }
    }
}
