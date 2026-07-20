namespace CarDiagnosticApp.Services;

/// <summary>
/// Очередь отзывов для офлайн-режима.
/// Если сервер недоступен, отзыв сохраняется локально через OfflineDatabase
/// и отправляется при следующей синхронизации.
/// </summary>
public class FeedbackQueueService
{
    private readonly ApiService _api;
    private readonly OfflineDatabase _db;

    public FeedbackQueueService(ApiService api, OfflineDatabase db)
    {
        _api = api;
        _db = db;
    }

    /// <summary>
    /// Добавляет отзыв в очередь (для офлайн-отправки позже).
    /// </summary>
    public async Task EnqueueAsync(string errorCode, bool helpful,
        string? carBrand = null, string? carModel = null,
        string? diagnosis = null, string? comment = null)
    {
        await _db.InitAsync();
        await _db.Feedback.EnqueueAsync(errorCode, helpful,
            carBrand, carModel, diagnosis, comment);
    }

    /// <summary>
    /// Пытается отправить все ожидающие отзывы на сервер.
    /// Возвращает количество успешно отправленных.
    /// </summary>
    public async Task<int> FlushAsync()
    {
        await _db.InitAsync();
        var pending = await _db.Feedback.GetAllAsync();

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

                await _db.Feedback.RemoveAsync(item);
                sent++;
            }
            catch
            {
                await _db.Feedback.IncrementRetryAsync(item);
            }
        }

        return sent;
    }

    /// <summary>
    /// Количество отзывов в очереди.
    /// </summary>
    public async Task<int> GetPendingCountAsync()
    {
        await _db.InitAsync();
        return await _db.Feedback.CountAsync();
    }
}
