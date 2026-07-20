using CarDiagnosticApp.Models;
using SQLite;
using System.Diagnostics;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис хранения новостей автопрома.
/// База: autoprom.db, таблица auto_industry_news.
/// </summary>
public class AutoIndustryService
{
    private SQLiteAsyncConnection? _db;
    private readonly string _dbPath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AutoIndustryService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "autoprom.db");
    }

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null)
            return _db;

        await _lock.WaitAsync();
        try
        {
            if (_db != null)
                return _db;

            _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache));
            await _db.CreateTableAsync<AutoIndustryNews>();
            return _db;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ── CRUD ──

    public async Task<int> InsertAsync(AutoIndustryNews news)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(news);
        return news.Id;
    }

    public async Task<int> InsertAllAsync(IEnumerable<AutoIndustryNews> items)
    {
        var db = await GetDbAsync();
        return await db.InsertAllAsync(items);
    }

    public async Task<List<AutoIndustryNews>> GetAllAsync(int limit = 200)
    {
        var db = await GetDbAsync();
        return await db.Table<AutoIndustryNews>()
            .OrderByDescending(n => n.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<AutoIndustryNews>> GetByCategoryAsync(string category, int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<AutoIndustryNews>()
            .Where(n => n.Category == category)
            .OrderByDescending(n => n.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<AutoIndustryNews>> GetUnprocessedAsync(int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<AutoIndustryNews>()
            .Where(n => !n.IsProcessed)
            .OrderByDescending(n => n.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<AutoIndustryNews>> GetHighRelevanceAsync(int limit = 30)
    {
        var db = await GetDbAsync();
        return await db.Table<AutoIndustryNews>()
            .Where(n => n.Relevance == "critical" || n.Relevance == "high")
            .OrderByDescending(n => n.DetectedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<int> MarkProcessedAsync(int id)
    {
        var db = await GetDbAsync();
        var news = await db.Table<AutoIndustryNews>().Where(n => n.Id == id).FirstOrDefaultAsync();
        if (news == null) return 0;
        news.IsProcessed = true;
        return await db.UpdateAsync(news);
    }

    public async Task<int> UpdateAsync(AutoIndustryNews news)
    {
        var db = await GetDbAsync();
        return await db.UpdateAsync(news);
    }

    public async Task<int> DeleteAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.DeleteAsync<AutoIndustryNews>(id);
    }

    public async Task<int> CountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<AutoIndustryNews>().CountAsync();
    }

    public async Task<int> CountByCategoryAsync(string category)
    {
        var db = await GetDbAsync();
        return await db.Table<AutoIndustryNews>().Where(n => n.Category == category).CountAsync();
    }

    /// <summary>
    /// Проверяет, есть ли уже новость с таким URL (дедупликация).
    /// </summary>
    public async Task<bool> ExistsByUrlAsync(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;

        var db = await GetDbAsync();
        var count = await db.Table<AutoIndustryNews>()
            .Where(n => n.SourceUrl == url)
            .CountAsync();
        return count > 0;
    }

    /// <summary>
    /// Генерирует текстовый отчёт по мониторингу автопрома.
    /// </summary>
    public async Task<string> GenerateReportAsync()
    {
        var db = await GetDbAsync();
        var all = await GetAllAsync(300);
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine("  ОТЧЁТ МОНИТОРИНГА АВТОПРОМА");
        sb.AppendLine($"  {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("═══════════════════════════════════════════════");
        sb.AppendLine();

        // Статистика
        sb.AppendLine("── Статистика базы ──");
        sb.AppendLine($"  Всего записей: {all.Count}");
        sb.AppendLine($"  Новых моделей: {all.Count(n => n.Category == "new_model")}");
        sb.AppendLine($"  Отзывных кампаний: {all.Count(n => n.Category == "recall")}");
        sb.AppendLine($"  Изменений стандартов: {all.Count(n => n.Category == "standard")}");
        sb.AppendLine($"  Новых протоколов: {all.Count(n => n.Category == "protocol")}");
        sb.AppendLine($"  Новых ЭБУ: {all.Count(n => n.Category == "ecu")}");
        sb.AppendLine($"  Кодов ошибок: {all.Count(n => n.Category == "error_codes")}");
        sb.AppendLine($"  Не обработано: {all.Count(n => !n.IsProcessed)}");
        sb.AppendLine();

        // Критические и важные
        var high = all.Where(n => n.Relevance == "critical" || n.Relevance == "high")
            .OrderBy(n => n.Relevance).ThenByDescending(n => n.DetectedAt)
            .ToList();

        if (high.Count > 0)
        {
            sb.AppendLine("── ⚠ Критические и важные события ──");
            foreach (var n in high)
            {
                var icon = n.Relevance == "critical" ? "🔴" : "🟡";
                sb.AppendLine($"  {icon} [{n.Category}] {n.Title}");
                if (!string.IsNullOrEmpty(n.Summary))
                    sb.AppendLine($"     {n.Summary[..Math.Min(n.Summary.Length, 120)]}…");
                sb.AppendLine($"     Источник: {n.Source} | Обнаружено: {n.DetectedAt:yyyy-MM-dd}");
                sb.AppendLine();
            }
        }

        // Последние 20 новостей
        sb.AppendLine("── Последние новости ──");
        foreach (var n in all.Take(20))
        {
            var status = n.IsProcessed ? "✓" : "○";
            sb.AppendLine($"  {status} [{n.Category}] {n.Title} ({n.Relevance})");
            sb.AppendLine($"     {n.Source} | {n.DetectedAt:yyyy-MM-dd}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Удаляет записи старше N дней (по умолчанию 365).
    /// </summary>
    public async Task<int> CleanOldAsync(int olderThanDays = 365)
    {
        var db = await GetDbAsync();
        var cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
        var old = await db.Table<AutoIndustryNews>()
            .Where(n => n.DetectedAt < cutoff && n.IsProcessed)
            .ToListAsync();

        if (old.Count == 0)
            return 0;

        foreach (var item in old)
            await db.DeleteAsync(item);

        Debug.WriteLine($"[AutoIndustry] Cleaned {old.Count} old records");
        return old.Count;
    }
}
