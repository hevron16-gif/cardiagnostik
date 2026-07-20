using System.Text;
using System.Text.Json;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис чтения живых данных OBD2 через ELM327.
/// Загружает базу PID из обновляемого JSON, периодически опрашивает и кэширует.
/// </summary>
public class LiveDataService
{
    private static PidDatabase? _pidDb;
    private static readonly object _dbLock = new();

    /// <summary>
    /// Текущая база PID (загружается из obd2_pids.json).
    /// </summary>
    public static PidDatabase PidDatabase => _pidDb ??= LoadPidDatabase();

    /// <summary>
    /// Список PID для опроса (фильтруется по priority ≤ 2, исключая meta).
    /// </summary>
    public static IReadOnlyList<LiveDataPid> AvailablePids => PidDatabase.Pids
        .Where(p => p.Priority <= 2 && p.Category != "meta")
        .OrderBy(p => p.Priority)
        .ThenBy(p => p.PidHex)
        .ToList();

    /// <summary>
    /// Все PID из базы (включая meta).
    /// </summary>
    public static IReadOnlyList<LiveDataPid> AllPids => PidDatabase.Pids;

    private readonly BluetoothService _bt;
    private CancellationTokenSource? _cts;
    private bool _isPolling;
    private double _lastCycleMs;

    public event Action<LiveDataPid, double>? OnValueUpdated;
    public event Action<int, double>? OnCycleCompleted;
    public event Action<string>? OnError;

    /// <summary>
    /// Длительность последнего полного цикла опроса в мс.
    /// </summary>
    public double LastCycleMs => _lastCycleMs;

    /// <summary>
    /// Частота обновления: число полных циклов в секунду.
    /// </summary>
    public double RefreshRateHz => _lastCycleMs > 0 ? 1000.0 / _lastCycleMs : 0;

    public Dictionary<string, (LiveDataPid Pid, double Value)> Cache { get; } = new();

    public LiveDataService(BluetoothService bt)
    {
        _bt = bt;
        foreach (var pid in AvailablePids)
            Cache[pid.PidHex] = (pid, double.NaN);
    }

    /// <summary>
    /// Загружает базу PID из встроенного JSON-ресурса (Resources/Raw/obd2_pids.json).
    /// </summary>
    private static PidDatabase LoadPidDatabase()
    {
        lock (_dbLock)
        {
            if (_pidDb != null) return _pidDb;

            try
            {
                // MAUI Raw assets доступны через FileSystem.OpenAppPackageFileAsync
                using var stream = FileSystem.OpenAppPackageFileAsync("obd2_pids.json")
                    .GetAwaiter().GetResult();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = reader.ReadToEnd();
                _pidDb = JsonSerializer.Deserialize<PidDatabase>(json) ?? new PidDatabase();
                return _pidDb;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiveData] Failed to load PID DB: {ex.Message}");
            }

            // Fallback: пустая база
            _pidDb = new PidDatabase();
            return _pidDb;
        }
    }

    /// <summary>
    /// Перезагружает базу из обновлённого JSON по пути.
    /// Используется при автообновлении знаний.
    /// </summary>
    public static async Task ReloadFromFileAsync(string jsonPath)
    {
        var result = await Task.Run(() =>
        {
            var json = File.ReadAllText(jsonPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<PidDatabase>(json) ?? new PidDatabase();
        });
        lock (_dbLock)
        {
            _pidDb = result;
        }
    }

    /// <summary>
    /// Автоматически определяет поддерживаемые PID через команды 0100–0160.
    /// Возвращает количество найденных поддерживаемых PID.
    /// Событие OnPidSupportProgress — для обновления UI в процессе.
    /// </summary>
    public event Action<int, int, string>? OnPidSupportProgress;

    public async Task<int> DetectSupportedPidsAsync()
    {
        OnPidSupportProgress?.Invoke(0, 4, "Запрос 0100...");

        // Сбрасываем все PID как неподдерживаемые
        foreach (var pid in AvailablePids)
            pid.IsSupported = false;

        var supportedSet = await _bt.ReadPidSupportAsync();

        OnPidSupportProgress?.Invoke(1, 4, $"Анализ {supportedSet.Count} PID...");

        // Отмечаем поддерживаемые PID из нашей базы
        int count = 0;
        foreach (var pid in AvailablePids)
        {
            if (int.TryParse(pid.PidHex, System.Globalization.NumberStyles.HexNumber,
                    null, out var pidNum) && supportedSet.Contains(pidNum))
            {
                pid.IsSupported = true;
                count++;
            }
            // PID 00 и 01 всегда считаем поддерживаемыми если ответили
            if (pid.PidHex == "00" && supportedSet.Count > 0)
                pid.IsSupported = true;
        }

        OnPidSupportProgress?.Invoke(4, 4, $"Готово: {count} / {AvailablePids.Count} PID");

        return count;
    }
    /// <summary>
    /// Запускает циклический опрос PID в реальном времени.
    /// Полный цикл: опрос ВСЕХ поддерживаемых PID подряд с паузой 15 мс между
    /// командами, затем ожидание остатка до targetCycleMs (по умолчанию 500 мс).
    /// События: OnValueUpdated (при каждом значении), OnCycleCompleted (после цикла).
    /// </summary>
    public async Task StartPollingAsync(int targetCycleMs = 500)
    {
        if (_isPolling)
        {
            OnError?.Invoke("Опрос уже запущен.");
            return;
        }

        _isPolling = true;
        _cts = new CancellationTokenSource();

        const int interCmdDelayMs = 15;
        const int maxConsecutiveErrors = 3;
        var errorCounts = new Dictionary<string, int>();

        try
        {
            while (!_cts.Token.IsCancellationRequested && _bt.IsConnected)
            {
                var cycleStart = System.Diagnostics.Stopwatch.GetTimestamp();

                var supportedPids = AvailablePids.Where(p => p.IsSupported).ToArray();
                if (supportedPids.Length == 0)
                {
                    OnError?.Invoke("Нет поддерживаемых PID");
                    break;
                }

                int updatedCount = 0;

                foreach (var pid in supportedPids)
                {
                    if (_cts.Token.IsCancellationRequested || !_bt.IsConnected)
                        break;

                    try
                    {
                        var value = await _bt.ReadPidValueAsync(pid);
                        Cache[pid.PidHex] = (pid, value);
                        OnValueUpdated?.Invoke(pid, value);
                        updatedCount++;

                        // Сброс счётчика ошибок при успехе
                        if (errorCounts.ContainsKey(pid.PidHex))
                            errorCounts[pid.PidHex] = 0;
                    }
                    catch
                    {
                        errorCounts.TryGetValue(pid.PidHex, out var cnt);
                        cnt++;
                        errorCounts[pid.PidHex] = cnt;

                        // Три подряд ошибки → помечаем PID как неподдерживаемый
                        if (cnt >= maxConsecutiveErrors)
                        {
                            pid.IsSupported = false;
                            OnValueUpdated?.Invoke(pid, double.NaN);
                        }
                    }

                    await Task.Delay(interCmdDelayMs, _cts.Token);
                }

                _lastCycleMs = System.Diagnostics.Stopwatch.GetElapsedTime(cycleStart).TotalMilliseconds;
                OnCycleCompleted?.Invoke(updatedCount, _lastCycleMs);

                // Ждём остаток до целевого интервала
                var remaining = targetCycleMs - _lastCycleMs;
                if (remaining > 0)
                    await Task.Delay((int)remaining, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _isPolling = false;
        }
    }

    /// <summary>
    /// Останавливает опрос PID.
    /// </summary>
    public void StopPolling()
    {
        _cts?.Cancel();
        _isPolling = false;
    }

    /// <summary>
    /// Разовый опрос указанного PID.
    /// </summary>
    public async Task<double> ReadSinglePidAsync(LiveDataPid pid)
    {
        try
        {
            var value = await _bt.ReadPidValueAsync(pid);
            Cache[pid.PidHex] = (pid, value);
            return value;
        }
        catch
        {
            pid.IsSupported = false;
            return double.NaN;
        }
    }
}
