using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Генерирует текстовые отчёты админа и сохраняет их на рабочий стол.
/// </summary>
public static class ReportService
{
    /// <summary>
    /// Формирует полный отчёт и сохраняет в report_YYYY-MM-DD_HH-mm.txt на рабочем столе.
    /// Возвращает путь к файлу или null при ошибке.
    /// </summary>
    public static async Task<string?> GenerateAndSaveAsync(DateTime? newCodesSince = null)
    {
        var text = await GenerateReportTextAsync(newCodesSince);
        if (text == null) return null;

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string fileName = $"report_{DateTime.Now:yyyy-MM-dd_HH-mm}.txt";
            string filePath = Path.Combine(desktop, fileName);
            await File.WriteAllTextAsync(filePath, text);

            System.Diagnostics.Debug.WriteLine($"[ReportService] Saved: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportService] Save error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Формирует текст отчёта (без сохранения в файл).
    /// </summary>
    public static async Task<string?> GenerateReportTextAsync(DateTime? newCodesSince = null)
    {
        try
        {
            var errorHistory = new ErrorHistoryService();
            var learningDb = new LearningDbService();
            var diagramDb = new DiagramDbService();
            var offlineDb = new OfflineDatabase();
            await offlineDb.InitAsync();

            var now = DateTime.Now;
            var report = new System.Text.StringBuilder();
            report.AppendLine("═══════════════════════════════════");
            report.AppendLine("  АВТОДИАГНОСТИКА AI — ОТЧЁТ АДМИНА");
            report.AppendLine($"  Дата: {now:dd.MM.yyyy HH:mm}");
            report.AppendLine("═══════════════════════════════════");
            report.AppendLine();

            // ── 1. Диагностика ──
            report.AppendLine("── 1. ДИАГНОСТИКА ──");
            var history = await errorHistory.GetHistorySinceAsync(DateTime.MinValue);
            report.AppendLine($"  Всего диагнозов:     {history.Count}");
            report.AppendLine($"  С диагнозом от AI:   {history.Count(h => !string.IsNullOrWhiteSpace(h.DiagnosisSnippet))}");
            var weekAgo = now.AddDays(-7);
            report.AppendLine($"  За последние 7 дней: {history.Count(h => h.DetectedAt >= weekAgo)}");
            report.AppendLine();

            // ── 2. База знаний ──
            report.AppendLine("── 2. БАЗА ЗНАНИЙ ──");
            var knowledge = await learningDb.GetStaleKnowledgeAsync(1.0, 3650);
            int uniqueCodes = await learningDb.GetUniqueErrorCodeCountAsync();
            report.AppendLine($"  Всего записей:       {knowledge.Count}");
            report.AppendLine($"  Уникальных кодов:    {uniqueCodes}");
            if (knowledge.Count > 0)
            {
                double avgConf = knowledge.Average(k => k.Confidence);
                report.AppendLine($"  Средняя уверенность: {avgConf:P0}");
                report.AppendLine($"  Высокая (≥70%):      {knowledge.Count(k => k.Confidence >= 0.7)}");
            }
            if (newCodesSince.HasValue)
            {
                int newCodes = await learningDb.GetNewSinceAsync(newCodesSince.Value);
                int updated = await learningDb.GetUpdatedSinceAsync(newCodesSince.Value);
                report.AppendLine($"  Новых за этот цикл:  {newCodes}");
                report.AppendLine($"  Решений обновлено:   {updated}");
            }
            report.AppendLine();

            // ── 3. Схемы ──
            report.AppendLine("── 3. СХЕМЫ ──");
            var diagrams = await diagramDb.GetPendingRequestsAsync();
            int totalDiagrams = await diagramDb.GetDiagramCountAsync();
            report.AppendLine($"  Всего схем в базе:    {totalDiagrams}");
            report.AppendLine($"  Запросов не найдено:  {diagrams.Count}");
            report.AppendLine($"  Найдено:              {diagrams.Count(d => d.Status == "found")}");
            report.AppendLine($"  Ожидают:              {diagrams.Count(d => d.Status == "pending")}");
            if (newCodesSince.HasValue)
            {
                int newDiagrams = await diagramDb.GetNewDiagramsSinceAsync(newCodesSince.Value);
                report.AppendLine($"  Новых за этот цикл:   {newDiagrams}");
            }
            report.AppendLine();

            // ── 4. Фидбек ──
            report.AppendLine("── 4. ФИДБЕК ПОЛЬЗОВАТЕЛЕЙ ──");
            var feedback = await offlineDb.Feedback.GetAllAsync();
            int helpful = feedback.Count(f => f.Helpful);
            int notHelpful = feedback.Count(f => !f.Helpful);
            int totalFb = feedback.Count;
            report.AppendLine($"  Всего оценок:        {totalFb}");
            report.AppendLine($"  👍 Полезно:           {helpful}");
            report.AppendLine($"  👎 Бесполезно:        {notHelpful}");
            report.AppendLine($"  % полезных:           {(totalFb > 0 ? (double)helpful / totalFb : 0):P0}");
            report.AppendLine();

            // ── 5. Топ-10 ошибок ──
            report.AppendLine("── 5. ТОП-10 ОШИБОК (за всё время) ──");
            var topErrors = history
                .GroupBy(h => h.ErrorCode ?? "—")
                .OrderByDescending(g => g.Count())
                .Take(10);
            int rank = 1;
            foreach (var g in topErrors)
                report.AppendLine($"  {rank++,2}. {g.Key,-10} — {g.Count()} раз(а)");
            report.AppendLine();

            // ── 6. Топ марок ──
            report.AppendLine("── 6. ТОП-10 МАРОК ──");
            var topBrands = history
                .GroupBy(h => string.IsNullOrWhiteSpace(h.Brand) ? "(не указана)" : h.Brand)
                .OrderByDescending(g => g.Count())
                .Take(10);
            rank = 1;
            foreach (var g in topBrands)
                report.AppendLine($"  {rank++,2}. {g.Key,-20} — {g.Count()} раз(а)");
            report.AppendLine();

            // ── 7. Активность по дням ──
            report.AppendLine("── 7. АКТИВНОСТЬ ПО ДНЯМ (последние 30 дней) ──");
            var monthAgo = now.AddDays(-30);
            var daily = history
                .Where(h => h.DetectedAt >= monthAgo)
                .GroupBy(h => h.DetectedAt.Date)
                .OrderBy(g => g.Key);
            foreach (var g in daily)
                report.AppendLine($"  {g.Key:dd.MM} : {new string('█', Math.Min(g.Count(), 50))} ({g.Count()})");

            // ── Общий итог ──
            int totalRecords = history.Count + knowledge.Count + totalDiagrams + diagrams.Count + totalFb;
            report.AppendLine();
            report.AppendLine("── ОБЩИЙ ИТОГ ──");
            report.AppendLine($"  Диагнозов:           {history.Count}");
            report.AppendLine($"  Знаний:              {knowledge.Count}");
            report.AppendLine($"  Схем:                {totalDiagrams}");
            report.AppendLine($"  Запросов схем:       {diagrams.Count}");
            report.AppendLine($"  Оценок:              {totalFb}");
            report.AppendLine($"  ─────────────────────────");
            report.AppendLine($"  ВСЕГО записей в БД:  {totalRecords}");
            report.AppendLine();

            report.AppendLine();
            report.AppendLine("═══════════════════════════════════");
            report.AppendLine("  Конец отчёта");
            report.AppendLine("═══════════════════════════════════");

            return report.ToString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportService] Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Возвращает путь к самому свежему файлу отчёта на рабочем столе, или null.
    /// </summary>
    public static string? GetLatestReportPath()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var files = Directory.GetFiles(desktop, "report_*.txt");
            return files.OrderByDescending(f => f).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Загружает содержимое последнего отчёта с рабочего стола.
    /// </summary>
    public static async Task<string?> LoadLatestReportContentAsync()
    {
        var path = GetLatestReportPath();
        if (path == null) return null;
        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Возвращает список файлов отчётов на рабочем столе (от новых к старым).
    /// </summary>
    public static List<ReportFileInfo> GetReportFiles()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Directory.GetFiles(desktop, "report_*.txt")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => new ReportFileInfo
                {
                    FilePath = f.FullName,
                    FileName = f.Name,
                    Size = f.Length,
                    CreatedAt = f.LastWriteTime
                })
                .ToList();
        }
        catch
        {
            return new List<ReportFileInfo>();
        }
    }
}

public class ReportFileInfo
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }

    public string DisplayDate => CreatedAt.ToString("dd.MM.yyyy HH:mm");
    public string DisplaySize => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:F1} KB",
        _ => $"{Size / (1024.0 * 1024.0):F1} MB"
    };
}
