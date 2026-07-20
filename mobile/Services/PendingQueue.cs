using System.Text;
using System.Text.Json;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Универсальная очередь для офлайн-отправки.
/// Все неподтверждённые данные (решения, диагнозы, отзывы, схемы)
/// складываются в локальный JSONL-файл и отправляются на сервер
/// при восстановлении связи.
///
/// Использование:
///   var q = new PendingQueue(apiService);
///   await q.EnqueueSolutionAsync("P0171", "Toyota", "Camry", "Заменить датчик...");
///   int sent = await q.FlushAsync();
///   Console.WriteLine($"Отправлено: {sent}");
/// </summary>
public class PendingQueue
{
    private readonly ApiService _api;
    private readonly SyncService? _sync;
    private readonly string _queuePath;

    // Максимум попыток отправки одного элемента
    private const int MaxRetries = 5;

    // Файл-флаг: flush уже запущен (избегаем параллельных отправок)
    private bool _flushing;

    public PendingQueue(ApiService api, SyncService? sync = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _sync = sync;
        _queuePath = Path.Combine(FileSystem.AppDataDirectory, "pending_queue.jsonl");
    }

    // ──────────────────────────────────────────────────────
    // ENQUEUE: добавить в очередь
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// Поставить решение пользователя в очередь (→ /submit_solution).
    /// </summary>
    public async Task EnqueueSolutionAsync(
        string errorCode, string carBrand, string carModel, string diagnosis)
    {
        await AppendAsync(new PendingItem
        {
            Type = "solution",
            JsonPayload = JsonSerializer.Serialize(new
            {
                error_code = errorCode,
                car_brand = carBrand,
                car_model = carModel,
                diagnosis = diagnosis,
                source = "user_submit",
                user_id = "",
            }),
            DisplayInfo = $"Решение для {errorCode} ({carBrand} {carModel})",
        });
    }

    /// <summary>
    /// Поставить диагноз в очередь (→ /sync/upload-diagnostics).
    /// </summary>
    public async Task EnqueueDiagnosticAsync(
        string errorCode, string carBrand, string carModel, string diagnosis)
    {
        await AppendAsync(new PendingItem
        {
            Type = "diagnostic",
            JsonPayload = JsonSerializer.Serialize(new
            {
                error_code = errorCode,
                car_brand = carBrand,
                car_model = carModel,
                diagnosis = diagnosis,
                source = "client_offline",
            }),
            DisplayInfo = $"Диагноз {errorCode} ({carBrand} {carModel})",
        });
    }

    /// <summary>
    /// Поставить отзыв в очередь (→ /send-feedback).
    /// </summary>
    public async Task EnqueueFeedbackAsync(
        string errorCode, bool helpful,
        string? carBrand = null, string? carModel = null,
        string? diagnosis = null, string? comment = null)
    {
        await AppendAsync(new PendingItem
        {
            Type = "feedback",
            JsonPayload = JsonSerializer.Serialize(new
            {
                error_code = errorCode,
                helpful = helpful,
                car_brand = carBrand ?? "",
                car_model = carModel ?? "",
                diagnosis = diagnosis ?? "",
                comment = comment ?? "",
            }),
            DisplayInfo = $"Отзыв на {errorCode}: {(helpful ? "👍" : "👎")}",
        });
    }

    /// <summary>
    /// Поставить запрос схемы в очередь (→ /sync/upload-diagrams).
    /// </summary>
    public async Task EnqueueDiagramAsync(
        string brand, string model, string errorCode, string sourceUrl)
    {
        await AppendAsync(new PendingItem
        {
            Type = "diagram",
            JsonPayload = JsonSerializer.Serialize(new
            {
                car_brand = brand,
                car_model = model,
                error_code = errorCode,
                source_url = sourceUrl,
            }),
            DisplayInfo = $"Схема: {errorCode} ({brand} {model})",
        });
    }

    // ──────────────────────────────────────────────────────
    // FLUSH: отправить всё из очереди
    // ──────────────────────────────────────────────────────

    /// <summary>
    /// Пытается отправить все ожидающие элементы на сервер.
    /// Возвращает количество успешно отправленных.
    /// </summary>
    public async Task<int> FlushAsync()
    {
        if (_flushing)
            return 0;

        _flushing = true;

        try
        {
            var items = await LoadAllAsync();
            if (items.Count == 0)
                return 0;

            int sent = 0;
            var toRemove = new List<PendingItem>();

            foreach (var item in items.OrderBy(i => i.CreatedAt))
            {
                bool ok = await SendOneAsync(item);
                if (ok)
                {
                    toRemove.Add(item);
                    sent++;
                }
                else
                {
                    item.RetryCount++;
                    item.LastError = $"Attempt {item.RetryCount}: server unreachable or error";
                    item.LastAttemptAt = DateTime.UtcNow;
                    await UpdateOneAsync(item);
                }
            }

            // Удаляем успешные
            await RemoveManyAsync(toRemove);
            return sent;
        }
        finally
        {
            _flushing = false;
        }
    }

    // ──────────────────────────────────────────────────────
    // СТАТУС
    // ──────────────────────────────────────────────────────

    /// <summary>Всего элементов в очереди.</summary>
    public async Task<int> GetPendingCountAsync()
    {
        var items = await LoadAllAsync();
        return items.Count(i => i.RetryCount < MaxRetries);
    }

    /// <summary>Элементов по типу.</summary>
    public async Task<int> GetPendingByTypeAsync(string type)
    {
        var items = await LoadAllAsync();
        return items.Count(i => i.Type == type && i.RetryCount < MaxRetries);
    }

    /// <summary>Сводка очереди для UI.</summary>
    public async Task<string> GetSummaryAsync()
    {
        var items = await LoadAllAsync();
        var active = items.Where(i => i.RetryCount < MaxRetries).ToList();
        var failed = items.Where(i => i.RetryCount >= MaxRetries).ToList();

        var parts = new List<string>();
        foreach (var g in active.GroupBy(i => i.Type))
            parts.Add($"{g.Key}: {g.Count()}");

        if (failed.Count > 0)
            parts.Add($"❌ безнадёжных: {failed.Count}");

        return parts.Count > 0
            ? $"📤 Очередь [{string.Join(", ", parts)}]"
            : "📤 Очередь пуста";
    }

    // ──────────────────────────────────────────────────────
    // ВНУТРЕННИЕ МЕТОДЫ
    // ──────────────────────────────────────────────────────

    private async Task AppendAsync(PendingItem item)
    {
        item.CreatedAt = DateTime.UtcNow;

        string json = JsonSerializer.Serialize(item, new JsonSerializerOptions
        {
            WriteIndented = false,
        });

        await Task.Run(() => File.AppendAllText(_queuePath, json + "\n", Encoding.UTF8));
    }

    private async Task<List<PendingItem>> LoadAllAsync()
    {
        if (!File.Exists(_queuePath))
            return new List<PendingItem>();

        string content = await Task.Run(() => File.ReadAllText(_queuePath, Encoding.UTF8));
        var items = new List<PendingItem>();

        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var item = JsonSerializer.Deserialize<PendingItem>(line);
                if (item != null)
                    items.Add(item);
            }
            catch { /* skip corrupted lines */ }
        }

        return items;
    }

    private async Task UpdateOneAsync(PendingItem item)
    {
        var all = await LoadAllAsync();
        var index = all.FindIndex(i =>
            i.CreatedAt == item.CreatedAt && i.Type == item.Type && i.JsonPayload == item.JsonPayload);

        if (index >= 0)
            all[index] = item;

        await RewriteAllAsync(all);
    }

    private async Task RemoveManyAsync(List<PendingItem> toRemove)
    {
        if (toRemove.Count == 0)
            return;

        var all = await LoadAllAsync();

        var removeKeys = new HashSet<string>(
            toRemove.Select(i => $"{i.CreatedAt}|{i.Type}|{i.JsonPayload}"));

        var kept = all.Where(i =>
            !removeKeys.Contains($"{i.CreatedAt}|{i.Type}|{i.JsonPayload}")).ToList();

        if (kept.Count < all.Count)
            await RewriteAllAsync(kept);
    }

    private async Task RewriteAllAsync(List<PendingItem> items)
    {
        var lines = items.Select(i =>
            JsonSerializer.Serialize(i, new JsonSerializerOptions { WriteIndented = false }));

        string content = string.Join("\n", lines) + (lines.Any() ? "\n" : "");
        await Task.Run(() => File.WriteAllText(_queuePath, content, Encoding.UTF8));
    }

    /// <summary>
    /// Отправляет один элемент на нужный эндпоинт.
    /// </summary>
    private async Task<bool> SendOneAsync(PendingItem item)
    {
        if (item.RetryCount >= MaxRetries)
            return false; // безнадёжный

        try
        {
            return item.Type switch
            {
                "solution" => await SendSolutionAsync(item),
                "diagnostic" => await SendDiagnosticAsync(item),
                "feedback" => await SendFeedbackAsync(item),
                "diagram" => await SendDiagramAsync(item),
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SendSolutionAsync(PendingItem item)
    {
        using var doc = JsonDocument.Parse(item.JsonPayload);
        var root = doc.RootElement;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var json = item.JsonPayload;
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/submit_solution", content);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> SendDiagnosticAsync(PendingItem item)
    {
        using var doc = JsonDocument.Parse(item.JsonPayload);
        var root = doc.RootElement;

        var payload = new[]
        {
            new
            {
                error_code = root.GetProperty("error_code").GetString() ?? "",
                car_brand = root.GetProperty("car_brand").GetString() ?? "",
                car_model = root.GetProperty("car_model").GetString() ?? "",
                diagnosis = root.GetProperty("diagnosis").GetString() ?? "",
                source = root.GetProperty("source").GetString() ?? "client_offline",
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/sync/upload-diagnostics", content);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> SendFeedbackAsync(PendingItem item)
    {
        using var doc = JsonDocument.Parse(item.JsonPayload);
        var root = doc.RootElement;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var json = item.JsonPayload;
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/send-feedback", content);
        return resp.IsSuccessStatusCode;
    }

    private async Task<bool> SendDiagramAsync(PendingItem item)
    {
        using var doc = JsonDocument.Parse(item.JsonPayload);
        var root = doc.RootElement;

        var payload = new[]
        {
            new
            {
                car_brand = root.GetProperty("car_brand").GetString() ?? "",
                car_model = root.GetProperty("car_model").GetString() ?? "",
                error_code = root.GetProperty("error_code").GetString() ?? "",
                source_url = root.GetProperty("source_url").GetString() ?? "",
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync($"{_api.BaseUrl}/sync/upload-diagrams", content);
        return resp.IsSuccessStatusCode;
    }
}

// ═══════════════════════════════════════════════════════
// МОДЕЛЬ ЭЛЕМЕНТА ОЧЕРЕДИ
// ═══════════════════════════════════════════════════════

public class PendingItem
{
    public string Type { get; set; } = "";          // solution | diagnostic | feedback | diagram
    public string JsonPayload { get; set; } = "";   // JSON тела запроса
    public DateTime CreatedAt { get; set; }
    public int RetryCount { get; set; }
    public string DisplayInfo { get; set; } = "";   // Человекочитаемое описание
    public string LastError { get; set; } = "";     // Последняя ошибка
    public DateTime LastAttemptAt { get; set; }

    public bool IsExhausted => RetryCount >= 5;
    public string StatusText => IsExhausted
        ? $"❌ безнадёжно ({RetryCount} попыток)"
        : RetryCount > 0
            ? $"⏳ попытка {RetryCount}"
            : "🆕 ожидает";
}
