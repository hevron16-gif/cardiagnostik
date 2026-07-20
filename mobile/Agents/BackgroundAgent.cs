using CarDiagnosticApp.Models;
using CarDiagnosticApp.Services;
using System.Diagnostics;

namespace CarDiagnosticApp.Agents;

/// <summary>
/// Фоновый агент самообучения.
/// Запускается при старте приложения и периодически обрабатывает:
/// - Анализ истории ошибок для поиска корреляций
/// - Повтор неудачных поисков схем
/// - Обновление статистики и знаний
/// - Очистка устаревших данных
/// </summary>
public class BackgroundAgent
{
    private static BackgroundAgent? _instance;
    public static BackgroundAgent Instance => _instance ??= new BackgroundAgent();

    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private DateTime _lastProcessedHistoryAt = DateTime.MinValue;

    private BackgroundAgent() { }

    /// <summary>
    /// Запускает фонового агента. Безопасно для многократного вызова.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _cts = new CancellationTokenSource();
        _isRunning = true;

        _ = RunLoopAsync(_cts.Token);
        Debug.WriteLine("[BackgroundAgent] Started");
    }

    /// <summary>
    /// Останавливает агента.
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();
        _isRunning = false;
        Debug.WriteLine("[BackgroundAgent] Stopped");
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            // Первый запуск — сразу обрабатываем
            await ProcessAllAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BackgroundAgent] Initial run error: {ex.Message}");
        }

        // Периодический цикл: каждые 30 минут
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(30), ct);
                await ProcessAllAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundAgent] Loop error: {ex.Message}");
            }
        }

        _isRunning = false;
    }

    private async Task ProcessAllAsync()
    {
        Debug.WriteLine("[BackgroundAgent] Processing...");

        try
        {
            await AnalyzeErrorHistoryForCorrelations();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BackgroundAgent] Correlation error: {ex.Message}");
        }

        try
        {
            await RetryPendingDiagramRequests();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BackgroundAgent] Retry error: {ex.Message}");
        }

        // Этап 6.1 — отчёт после обработки
        try
        {
            var since = DateTime.Now.AddMinutes(-30);
            var reportPath = await Services.ReportService.GenerateAndSaveAsync(newCodesSince: since);
            if (reportPath != null)
                Debug.WriteLine($"[BackgroundAgent] Report saved: {reportPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[BackgroundAgent] Report error: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════════════════
    //  АНАЛИЗ КОРРЕЛЯЦИЙ ОШИБОК
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Анализирует историю ошибок и находит корреляции:
    /// какие ошибки часто появляются вместе на конкретных авто.
    /// </summary>
    private async Task AnalyzeErrorHistoryForCorrelations()
    {
        var historyService = new ErrorHistoryService();
        var learningService = new LearningDbService();

        // Берём только новые записи с последнего анализа
        var history = await historyService.GetHistorySinceAsync(_lastProcessedHistoryAt);

        if (history.Count == 0) return;

        Debug.WriteLine($"[BackgroundAgent] Analyzing {history.Count} new history entries");

        // Группируем по марке+модели, чтобы найти ошибки, появляющиеся вместе
        var groupedByCar = history
            .GroupBy(h => (brand: h.Brand ?? "", model: h.Model ?? ""))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key.brand));

        foreach (var group in groupedByCar)
        {
            var errorCodesInGroup = group
                .Select(h => h.ErrorCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct()
                .ToList();

            if (errorCodesInGroup.Count < 2) continue;

            // Для каждой ошибки записываем остальные как связанные
            foreach (var errorCode in errorCodesInGroup)
            {
                var others = errorCodesInGroup
                    .Where(c => c != errorCode)
                    .ToList();

                try
                {
                    await learningService.RecordErrorCorrelationAsync(
                        errorCode, group.Key.brand, group.Key.model, others);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackgroundAgent] Correlation record error: {ex.Message}");
                }
            }
        }

        _lastProcessedHistoryAt = DateTime.UtcNow;
    }

    // ═══════════════════════════════════════════════════
    //  ПОВТОР НЕУДАЧНЫХ ПОИСКОВ СХЕМ
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// Периодически повторяет поиск схем для запросов, которые ранее не дали результатов.
    /// </summary>
    private async Task RetryPendingDiagramRequests()
    {
        var diagramDb = new DiagramDbService();
        var pending = await diagramDb.GetPendingRequestsAsync();

        if (pending.Count == 0) return;

        Debug.WriteLine($"[BackgroundAgent] Retrying {pending.Count} pending diagram requests");

        // Повторяем только запросы с RetryCount <= 5 (чтобы не зациклиться)
        foreach (var request in pending.Where(p => p.RetryCount <= 5).Take(3))
        {
            try
            {
                var service = IPlatformApplication.Current!.Services.GetRequiredService<Services.ApiService>();
                var result = await service.SearchSchemesAsync(
                    request.ErrorCode, request.CarBrand, request.CarModel, maxResults: 4);

                if (result != null && result.results.Count > 0)
                {
                    // Нашли! Пытаемся скачать первую картинку
                    var imageResult = result.results.FirstOrDefault(r =>
                        r.source == "google_cse" && !string.IsNullOrWhiteSpace(r.full_image_url));

                    if (imageResult != null)
                    {
                        var localPath = await diagramDb.DownloadAndSaveImageDiagramAsync(
                            request.CarBrand, request.CarModel, request.ErrorCode,
                            imageResult.full_image_url,
                            sourceUrl: imageResult.page_url ?? imageResult.url,
                            source: "internet");

                        if (localPath != null)
                        {
                            await diagramDb.MarkRequestAsFoundAsync(
                                request.CarBrand, request.CarModel, request.ErrorCode);
                            Debug.WriteLine($"[BackgroundAgent] Found diagram for {request.ErrorCode} {request.CarBrand}");
                            continue;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackgroundAgent] Retry failed for {request.ErrorCode}: {ex.Message}");
            }
        }
    }
}
