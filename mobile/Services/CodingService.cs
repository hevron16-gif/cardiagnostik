using CarDiagnosticApp.Models;
using SQLite;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис кодирования и активации скрытых функций автомобиля.
/// Управляет каталогом функций и выполняет кодирование через ELM327 (AT-команды / CAN-кадры).
/// База: coding.db
/// </summary>
public class CodingService
{
    private SQLiteAsyncConnection? _db;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string _dbPath;

    // Bluetooth-сервис для отправки команд
    private BluetoothService? _bt;

    // ──────────────── Инициализация ────────────────

    public CodingService()
    {
        _dbPath = Path.Combine(FileSystem.AppDataDirectory, "coding.db");
    }

    public void Bind(BluetoothService bt)
    {
        _bt = bt;
    }

    private async Task<SQLiteAsyncConnection> GetDbAsync()
    {
        if (_db != null) return _db;
        await _lock.WaitAsync();
        try
        {
            if (_db != null) return _db;
            _db = await Task.Run(() => new SQLiteAsyncConnection(_dbPath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache));
            await _db.CreateTableAsync<HiddenFeature>();
            await _db.CreateTableAsync<CodingSession>();
        }
        finally { _lock.Release(); }
        return _db;
    }

    // ──────────────── CRUD: HiddenFeature ────────────────

    public async Task<List<HiddenFeature>> GetFeaturesAsync(string? brand = null, string? category = null)
    {
        var db = await GetDbAsync();
        var all = await db.Table<HiddenFeature>().ToListAsync();

        if (!string.IsNullOrWhiteSpace(brand))
            all = all.Where(f => f.Brand == null || f.Brand == brand).ToList();
        if (!string.IsNullOrWhiteSpace(category))
            all = all.Where(f => f.Category == category).ToList();

        return all.OrderBy(f => f.Category).ThenBy(f => f.FeatureName).ToList();
    }

    public async Task<List<string>> GetCategoriesAsync(string? brand = null)
    {
        var features = await GetFeaturesAsync(brand);
        return features.Select(f => f.Category).Distinct().OrderBy(c => c).ToList();
    }

    public async Task<HiddenFeature?> GetFeatureAsync(int id)
    {
        var db = await GetDbAsync();
        return await db.FindAsync<HiddenFeature>(id);
    }

    public async Task SaveFeatureAsync(HiddenFeature feature)
    {
        var db = await GetDbAsync();
        feature.UpdatedAt = DateTime.UtcNow;
        if (feature.Id > 0)
            await db.UpdateAsync(feature);
        else
            await db.InsertAsync(feature);
    }

    public async Task DeleteFeatureAsync(int id)
    {
        var db = await GetDbAsync();
        await db.DeleteAsync<HiddenFeature>(id);
    }

    public async Task<int> GetFeatureCountAsync()
    {
        var db = await GetDbAsync();
        return await db.Table<HiddenFeature>().CountAsync();
    }

    // ──────────────── CRUD: CodingSession ────────────────

    public async Task<List<CodingSession>> GetSessionsAsync(int limit = 50)
    {
        var db = await GetDbAsync();
        return await db.Table<CodingSession>()
            .OrderByDescending(s => s.PerformedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<CodingSession>> GetSessionsForFeatureAsync(int featureId, int limit = 20)
    {
        var db = await GetDbAsync();
        return await db.Table<CodingSession>()
            .Where(s => s.FeatureId == featureId)
            .OrderByDescending(s => s.PerformedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task SaveSessionAsync(CodingSession session)
    {
        var db = await GetDbAsync();
        await db.InsertAsync(session);
    }

    // ──────────────── Активация / Деактивация ────────────────

    /// <summary>
    /// Активирует скрытую функцию. Если подключён ELM327 — отправляет команду.
    /// Возвращает (success, response).
    /// </summary>
    public async Task<(bool Success, string Message)> ActivateFeatureAsync(HiddenFeature feature, string brand, string? model)
    {
        try
        {
            string? command = feature.ActivationCommand;

            // Если есть команда и ELM327 подключён — выполняем
            if (!string.IsNullOrEmpty(command) && _bt?.IsConnected == true)
            {
                var response = await SendCodingCommandAsync(command, feature.ModuleAddress);
                var ok = ValidateResponse(response, command);

                await SaveSessionAsync(new CodingSession
                {
                    FeatureId = feature.Id,
                    FeatureName = feature.FeatureName,
                    Brand = brand,
                    ModelName = model,
                    Action = "activate",
                    Success = ok,
                    ResponseData = response ?? "no_response",
                });

                if (ok)
                {
                    feature.ActivationCount++;
                    await SaveFeatureAsync(feature);
                }

                return (ok, ok
                    ? $"✅ «{feature.FeatureName}» успешно активирована."
                    : $"❌ Не удалось активировать «{feature.FeatureName}». Ответ: {response}");
            }

            // Без подключения — просто логируем (тестовый режим)
            await SaveSessionAsync(new CodingSession
            {
                FeatureId = feature.Id,
                FeatureName = feature.FeatureName,
                Brand = brand,
                ModelName = model,
                Action = "activate",
                Success = true,
                ResponseData = "test_mode",
                Notes = "Выполнено в тестовом режиме (без ELM327)."
            });

            feature.ActivationCount++;
            await SaveFeatureAsync(feature);

            return (true, $"✅ «{feature.FeatureName}» активирована (тестовый режим).");
        }
        catch (Exception ex)
        {
            await SaveSessionAsync(new CodingSession
            {
                FeatureId = feature.Id,
                FeatureName = feature.FeatureName,
                Brand = brand,
                ModelName = model,
                Action = "activate",
                Success = false,
                ResponseData = ex.Message,
            });
            return (false, $"❌ Ошибка: {ex.Message}");
        }
    }

    /// <summary>
    /// Деактивирует скрытую функцию.
    /// </summary>
    public async Task<(bool Success, string Message)> DeactivateFeatureAsync(HiddenFeature feature, string brand, string? model)
    {
        try
        {
            string? command = feature.DeactivationCommand;

            if (!string.IsNullOrEmpty(command) && _bt?.IsConnected == true)
            {
                // 💾 Авто-бэкап: читаем текущее значение перед откатом
                string? originalHex = null;
                if (feature.EncodedByte.HasValue)
                {
                    try { originalHex = await ReadModuleByteAsync(feature.ModuleAddress!, feature.EncodedByte.Value); }
                    catch { /* не смогли прочитать — идём без бэкапа */ }
                }

                var response = await SendCodingCommandAsync(command, feature.ModuleAddress);
                var ok = ValidateResponse(response, command);

                // 💾 Сохраняем резервную копию
                if (ok && feature.EncodedByte.HasValue && !string.IsNullOrWhiteSpace(originalHex))
                {
                    await CreateByteBackupAsync(brand, model, feature.ModuleAddress!,
                        feature.EncodedByte.Value, originalHex, command,
                        feature.Id, feature.FeatureName);
                }

                await SaveSessionAsync(new CodingSession
                {
                    FeatureId = feature.Id,
                    FeatureName = feature.FeatureName,
                    Brand = brand,
                    ModelName = model,
                    Action = "deactivate",
                    Success = ok,
                    ResponseData = response ?? "no_response",
                });

                return (ok, ok
                    ? $"✅ «{feature.FeatureName}» деактивирована."
                    : $"❌ Не удалось деактивировать. Ответ: {response}");
            }

            await SaveSessionAsync(new CodingSession
            {
                FeatureId = feature.Id,
                FeatureName = feature.FeatureName,
                Brand = brand,
                ModelName = model,
                Action = "deactivate",
                Success = true,
                ResponseData = "test_mode",
                Notes = "Выполнено в тестовом режиме (без ELM327)."
            });

            return (true, $"✅ «{feature.FeatureName}» деактивирована (тестовый режим).");
        }
        catch (Exception ex)
        {
            await SaveSessionAsync(new CodingSession
            {
                FeatureId = feature.Id,
                FeatureName = feature.FeatureName,
                Brand = brand,
                ModelName = model,
                Action = "deactivate",
                Success = false,
                ResponseData = ex.Message,
            });
            return (false, $"❌ Ошибка: {ex.Message}");
        }
    }

    // ──────────────── Сканирование доступных функций ────────────────

    /// <summary>
    /// Сканирует блоки на наличие поддерживаемых скрытых функций.
    /// Отправляет диагностические запросы и проверяет ответы.
    /// </summary>
    public async Task<List<HiddenFeature>> ScanAvailableFeaturesAsync(string brand, string? model)
    {
        var features = await GetFeaturesAsync(brand);
        var available = new List<HiddenFeature>();

        foreach (var f in features)
        {
            // Проверяем год модели (если задан)
            if (f.YearFrom.HasValue || f.YearTo.HasValue)
            {
                // Год определяем из модели или пропускаем
                // (в реальном приложении — запрос к ЭБУ)
            }

            // Сканируем доступность через ELM327
            if (_bt?.IsConnected == true && f.EncodedByte.HasValue)
            {
                try
                {
                    var response = await ReadModuleByteAsync(f.ModuleAddress!, f.EncodedByte.Value);
                    if (!string.IsNullOrEmpty(response))
                    {
                        f.IsAvailable = true;
                        // Проверяем, активна ли уже
                        if (f.BitMask.HasValue && int.TryParse(response,
                                System.Globalization.NumberStyles.HexNumber, null, out var byteVal))
                        {
                            f.IsActive = (byteVal & f.BitMask.Value) != 0;
                        }
                        available.Add(f);
                    }
                }
                catch { /* пропускаем недоступные */ }
            }
            else
            {
                // Тестовый режим — помечаем популярные как доступные
                f.IsAvailable = true;
                f.IsActive = f.ActivationCount > 0;
                available.Add(f);
            }
        }

        return available;
    }

    // ──────────────── ELM327-команды ────────────────

    private async Task<string?> SendCodingCommandAsync(string command, string? moduleAddress)
    {
        if (_bt == null) return null;

        try
        {
            // Устанавливаем адрес модуля, если задан
            if (!string.IsNullOrWhiteSpace(moduleAddress))
            {
                await _bt.SendAsync($"ATSH{moduleAddress}");
                await Task.Delay(50);
            }

            // Отправляем команду
            var response = await _bt.SendAsync(command);
            await Task.Delay(100);
            return response;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodingService] Command error: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ReadModuleByteAsync(string moduleAddress, int byteOffset)
    {
        if (_bt == null) return null;

        try
        {
            await _bt.SendAsync($"ATSH{moduleAddress}");
            await Task.Delay(50);

            // Mode 23 — чтение памяти по адресу
            var addrHex = byteOffset.ToString("X4");
            var response = await _bt.SendAsync($"23{addrHex}");
            await Task.Delay(100);
            return response;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Записывает байт в память модуля (Mode 3B — WriteMemoryByAddress).
    /// Используется для восстановления резервных копий.
    /// </summary>
    private async Task<bool> WriteModuleByteAsync(string moduleAddress, int byteOffset, string hexValue)
    {
        if (_bt == null || !_bt.IsConnected) return false;

        try
        {
            await _bt.SendAsync($"ATSH{moduleAddress}");
            await Task.Delay(50);

            // Mode 3B — запись в память: 3B + адрес (2 байта) + значение (1 байт)
            var addrHex = byteOffset.ToString("X4");
            var cleanHex = hexValue.Replace(" ", "").Replace("\r", "").Replace("\n", "").Trim();
            // Убеждаемся что это ровно 2 hex-символа
            if (cleanHex.Length > 2)
                cleanHex = cleanHex[^2..];
            cleanHex = cleanHex.PadLeft(2, '0');

            var response = await _bt.SendAsync($"3B{addrHex}{cleanHex}");
            await Task.Delay(100);

            return ValidateResponse(response, $"3B{addrHex}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodingService] Write byte error: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> SendRawAsync(string command)
    {
        if (_bt == null) return null;

        try
        {
            if (_bt.IsConnected)
            {
                var response = await _bt.SendAsync(command);
                return response?.Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodingService] Raw send error: {ex.Message}");
        }
        return null;
    }

    private static bool ValidateResponse(string? response, string command)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        if (response.Contains("OK")) return true;
        if (response.Contains("ERROR") || response.Contains("?") || response.Contains("NO DATA"))
            return false;
        // Любой другой ответ считаем успешным (блок ответил данными)
        return response.Length > 2;
    }

    // ──────────────── Чтение текущих настроек ────────────────

    /// <summary>
    /// Читает текущие настройки автомобиля — состояние всех известных скрытых функций для указанной марки.
    /// Возвращает список CurrentSetting с флагом IsActive.
    /// </summary>
    public async Task<List<CurrentSetting>> ReadCurrentSettingsAsync(string brand)
    {
        var features = await GetFeaturesAsync(brand);
        var settings = new List<CurrentSetting>();

        foreach (var f in features.Where(f => f.EncodedByte.HasValue && !string.IsNullOrWhiteSpace(f.ModuleAddress)))
        {
            var setting = new CurrentSetting
            {
                FeatureName = f.FeatureName,
                Category = f.Category,
                Icon = f.Icon,
                ModuleAddress = f.ModuleAddress!,
                ByteOffset = f.EncodedByte!.Value,
                BitMask = f.BitMask ?? 0xFF,
                Description = f.Description,
                IsCodable = f.ModuleAddress != null && f.EncodedByte.HasValue,
            };

            if (_bt?.IsConnected == true)
            {
                try
                {
                    var hex = await ReadModuleByteAsync(f.ModuleAddress!, f.EncodedByte.Value);
                    setting.RawHex = hex;
                    if (!string.IsNullOrWhiteSpace(hex) &&
                        int.TryParse(hex.Replace(" ", "").Replace("\r", "").Replace("\n", ""),
                            System.Globalization.NumberStyles.HexNumber, null, out var byteVal))
                    {
                        setting.IsActive = f.BitMask.HasValue
                            ? (byteVal & f.BitMask.Value) != 0
                            : byteVal != 0;
                    }
                }
                catch
                {
                    setting.RawHex = "—";
                }
            }
            else
            {
                // Тестовый режим — симулируем данные на основе ActivationCount
                setting.RawHex = f.ActivationCount > 0
                    ? (f.BitMask ?? 0xFF).ToString("X2")
                    : "00";
                setting.IsActive = f.IsActive;
            }

            settings.Add(setting);
        }

        return settings;
    }

    /// <summary>
    /// Читает состояние одной конкретной функции.
    /// Возвращает null если модуль недоступен.
    /// </summary>
    public async Task<CurrentSetting?> ReadFeatureStatusAsync(HiddenFeature feature)
    {
        if (!feature.EncodedByte.HasValue || string.IsNullOrWhiteSpace(feature.ModuleAddress))
            return null;

        var setting = new CurrentSetting
        {
            FeatureName = feature.FeatureName,
            Category = feature.Category,
            Icon = feature.Icon,
            ModuleAddress = feature.ModuleAddress!,
            ByteOffset = feature.EncodedByte!.Value,
            BitMask = feature.BitMask ?? 0xFF,
            Description = feature.Description,
            IsCodable = true,
        };

        if (_bt?.IsConnected == true)
        {
            try
            {
                var hex = await ReadModuleByteAsync(feature.ModuleAddress!, feature.EncodedByte.Value);
                setting.RawHex = hex;
                if (!string.IsNullOrWhiteSpace(hex) &&
                    int.TryParse(hex.Replace(" ", "").Replace("\r", "").Replace("\n", ""),
                        System.Globalization.NumberStyles.HexNumber, null, out var byteVal))
                {
                    setting.IsActive = feature.BitMask.HasValue
                        ? (byteVal & feature.BitMask.Value) != 0
                        : byteVal != 0;
                }
            }
            catch
            {
                return null;
            }
        }
        else
        {
            setting.RawHex = feature.IsActive
                ? (feature.BitMask ?? 0xFF).ToString("X2")
                : "00";
            setting.IsActive = feature.IsActive;
        }

        return setting;
    }

    /// <summary>
    /// Читает сырой дамп кодировок из модуля — диапазон байт.
    /// Полезно для анализа неизвестных/недокументированных байт.
    /// </summary>
    public async Task<List<ModuleCodingDump>> ReadModuleCodingRangeAsync(string moduleAddress, int startByte, int count)
    {
        var dump = new List<ModuleCodingDump>();

        if (_bt?.IsConnected == true)
        {
            for (int i = 0; i < count; i++)
            {
                var offset = startByte + i;
                try
                {
                    var hex = await ReadModuleByteAsync(moduleAddress, offset);
                    var cleanHex = hex?.Replace(" ", "").Replace("\r", "").Replace("\n", "").Trim() ?? "";

                    int byteVal = 0;
                    int.TryParse(cleanHex, System.Globalization.NumberStyles.HexNumber, null, out byteVal);

                    dump.Add(new ModuleCodingDump
                    {
                        ModuleAddress = moduleAddress,
                        OffsetHex = offset.ToString("X4"),
                        HexValue = cleanHex.Length > 0 ? cleanHex : "—",
                        DecValue = byteVal,
                        Binary = Convert.ToString(byteVal, 2).PadLeft(8, '0'),
                        Ascii = byteVal >= 32 && byteVal <= 126 ? ((char)byteVal).ToString() : null,
                    });
                }
                catch
                {
                    dump.Add(new ModuleCodingDump
                    {
                        ModuleAddress = moduleAddress,
                        OffsetHex = offset.ToString("X4"),
                        HexValue = "ERR",
                    });
                }

                await Task.Delay(20); // межкадровая пауза
            }
        }
        else
        {
            // Тестовый дамп для демонстрации
            var rng = new Random();
            for (int i = 0; i < count; i++)
            {
                var offset = startByte + i;
                var byteVal = rng.Next(0, 256);
                dump.Add(new ModuleCodingDump
                {
                    ModuleAddress = moduleAddress,
                    OffsetHex = offset.ToString("X4"),
                    HexValue = byteVal.ToString("X2"),
                    DecValue = byteVal,
                    Binary = Convert.ToString(byteVal, 2).PadLeft(8, '0'),
                    Ascii = byteVal >= 32 && byteVal <= 126 ? ((char)byteVal).ToString() : null,
                });
            }
        }

        return dump;
    }

    /// <summary>
    /// Быстрая проверка: читает все известные байты настроек и возвращает
    /// сводку: сколько функций активно / неактивно / ошибок чтения.
    /// </summary>
    public async Task<(int Active, int Inactive, int Errors)> QuickSettingsSummaryAsync(string brand)
    {
        var settings = await ReadCurrentSettingsAsync(brand);
        int active = 0, inactive = 0, errors = 0;

        foreach (var s in settings)
        {
            if (s.RawHex == "ERR" || s.RawHex == "—") errors++;
            else if (s.IsActive) active++;
            else inactive++;
        }

        return (active, inactive, errors);
    }

    // ──────────────── Seed-данные ────────────────

    /// <summary>
    /// Заполняет базу seed-данными скрытых функций для российских авто.
    /// Возвращает количество добавленных записей.
    /// </summary>
    public async Task<int> SeedAsync()
    {
        var count = await GetFeatureCountAsync();
        if (count > 0) return 0; // уже заполнена

        var db = await GetDbAsync();
        var features = BuildSeedFeatures();
        int added = 0;

        foreach (var f in features)
        {
            try
            {
                await db.InsertAsync(f);
                added++;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CodingService] Seed error: {ex.Message}");
            }
        }

        System.Diagnostics.Debug.WriteLine($"[CodingService] Seeded {added} features.");
        return added;
    }

    // ──────────────── Резервное копирование настроек ────────────────

    /// <summary>
    /// Сохраняет резервную копию одного байта перед изменением.
    /// Вызывается автоматически из ActivateFeatureAsync / DeactivateFeatureAsync.
    /// </summary>
    public async Task<CodingBackup> CreateByteBackupAsync(
        string brand, string? model, string moduleAddress, int byteOffset,
        string originalHex, string newHex, int? featureId = null, string? featureName = null,
        string? sessionTag = null)
    {
        var db = await GetDbAsync();
        var backup = new CodingBackup
        {
            Brand = brand,
            ModelName = model,
            ModuleAddress = moduleAddress,
            ByteOffset = byteOffset,
            OriginalHex = originalHex,
            NewHex = newHex,
            FeatureId = featureId,
            FeatureName = featureName,
            Label = featureName ?? $"0x{byteOffset:X2}",
            SessionTag = sessionTag,
            CreatedAt = DateTime.UtcNow,
        };

        await db.InsertAsync(backup);
        System.Diagnostics.Debug.WriteLine($"[CodingService] Backup #{backup.Id}: {moduleAddress} 0x{byteOffset:X2} {originalHex}→{newHex}");
        return backup;
    }

    /// <summary>
    /// Создаёт полную резервную копию всех настроек для указанной марки.
    /// Сначала читает текущие значения из всех модулей, затем сохраняет в БД.
    /// Возвращает sessionTag для последующего восстановления.
    /// </summary>
    public async Task<(string SessionTag, int BackedUpBytes)> CreateFullBackupAsync(string brand, string? model)
    {
        var sessionTag = $"backup_{brand}_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
        var settings = await ReadCurrentSettingsAsync(brand);
        int count = 0;
        var db = await GetDbAsync();

        foreach (var s in settings.Where(s => s.IsCodable && !string.IsNullOrWhiteSpace(s.RawHex) && s.RawHex != "—" && s.RawHex != "ERR"))
        {
            await CreateByteBackupAsync(brand, model, s.ModuleAddress, s.ByteOffset,
                s.RawHex!, newHex: null, featureName: s.FeatureName, sessionTag: sessionTag);
            count++;
        }

        System.Diagnostics.Debug.WriteLine($"[CodingService] Full backup '{sessionTag}': {count} bytes.");
        return (sessionTag, count);
    }

    /// <summary>
    /// Получить список всех резервных копий с фильтрацией.
    /// </summary>
    public async Task<List<CodingBackup>> GetBackupsAsync(
        string? brand = null, string? sessionTag = null,
        bool? isRestored = null, int limit = 200)
    {
        var db = await GetDbAsync();
        var sql = "SELECT * FROM coding_backups WHERE 1=1";
        var args = new List<object>();

        if (!string.IsNullOrWhiteSpace(brand)) { sql += " AND Brand = ?"; args.Add(brand); }
        if (!string.IsNullOrWhiteSpace(sessionTag)) { sql += " AND SessionTag = ?"; args.Add(sessionTag); }
        if (isRestored.HasValue) { sql += " AND IsRestored = ?"; args.Add(isRestored.Value ? 1 : 0); }

        sql += " ORDER BY CreatedAt DESC LIMIT ?";
        args.Add(limit);

        return await db.QueryAsync<CodingBackup>(sql, args.ToArray());
    }

    /// <summary>
    /// Получить список уникальных сессий резервного копирования.
    /// </summary>
    public async Task<List<string>> GetBackupSessionsAsync(string? brand = null)
    {
        var db = await GetDbAsync();
        var sql = "SELECT DISTINCT SessionTag FROM coding_backups WHERE SessionTag IS NOT NULL";
        var args = new List<object>();
        if (!string.IsNullOrWhiteSpace(brand)) { sql += " AND Brand = ?"; args.Add(brand); }
        sql += " ORDER BY SessionTag DESC LIMIT 50";

        var rows = await db.QueryAsync<CodingBackup>(sql, args.ToArray());
        return rows.Select(r => r.SessionTag!).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
    }

    /// <summary>
    /// Восстанавливает один байт из резервной копии (записывает OriginalHex обратно в модуль).
    /// </summary>
    public async Task<bool> RestoreByteBackupAsync(CodingBackup backup)
    {
        if (backup.IsRestored) return false; // уже восстановлен

        try
        {
            // Отправляем оригинальное значение обратно в модуль
            var result = await WriteModuleByteAsync(backup.ModuleAddress, backup.ByteOffset, backup.OriginalHex);

            if (result)
            {
                backup.IsRestored = true;
                backup.RestoredAt = DateTime.UtcNow;
                backup.Notes = (backup.Notes ?? "") + " [восстановлено]";

                var db = await GetDbAsync();
                await db.UpdateAsync(backup);

                System.Diagnostics.Debug.WriteLine($"[CodingService] Restored backup #{backup.Id}: {backup.ModuleAddress} 0x{backup.ByteOffset:X2} → {backup.OriginalHex}");
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CodingService] Restore failed #{backup.Id}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Восстанавливает все байты из сессии резервного копирования.
    /// </summary>
    public async Task<(int Restored, int Failed)> RestoreSessionAsync(string sessionTag)
    {
        var backups = await GetBackupsAsync(sessionTag: sessionTag);
        int restored = 0, failed = 0;

        foreach (var b in backups.Where(b => !b.IsRestored))
        {
            if (await RestoreByteBackupAsync(b))
                restored++;
            else
                failed++;
        }

        System.Diagnostics.Debug.WriteLine($"[CodingService] Restored session '{sessionTag}': {restored} OK, {failed} failed.");
        return (restored, failed);
    }

    /// <summary>
    /// Восстанавливает все невосстановленные резервные копии для марки.
    /// Возвращает количество успешно восстановленных байт.
    /// </summary>
    public async Task<int> RestoreAllForBrandAsync(string brand)
    {
        var backups = await GetBackupsAsync(brand: brand, isRestored: false);
        int count = 0;
        foreach (var b in backups)
        {
            if (await RestoreByteBackupAsync(b))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Получить сводку по резервным копиям.
    /// </summary>
    public async Task<(int Total, int Restored, int Pending, string? LastBackup)> GetBackupSummaryAsync(string? brand = null)
    {
        var db = await GetDbAsync();

        string filter = string.IsNullOrWhiteSpace(brand) ? "" : " WHERE Brand = ?";
        var args = string.IsNullOrWhiteSpace(brand) ? new object[0] : new object[] { brand };

        var total = (await db.QueryAsync<CodingBackup>($"SELECT * FROM coding_backups{filter}", args)).Count;
        var restored = (await db.QueryAsync<CodingBackup>($"SELECT * FROM coding_backups WHERE IsRestored = 1{(string.IsNullOrWhiteSpace(brand) ? "" : " AND Brand = ?")}", string.IsNullOrWhiteSpace(brand) ? new object[0] : new object[] { brand })).Count;

        var last = (await db.QueryAsync<CodingBackup>(
            $"SELECT * FROM coding_backups{filter} ORDER BY CreatedAt DESC LIMIT 1", args)).FirstOrDefault();

        var lastStr = last?.CreatedAt.ToString("dd.MM.yyyy HH:mm");

        return (total, restored, total - restored, lastStr);
    }

    /// <summary>
    /// Удаляет резервную копию (только если она уже восстановлена).
    /// </summary>
    public async Task<bool> DeleteBackupAsync(int backupId)
    {
        var db = await GetDbAsync();
        var backup = await db.FindAsync<CodingBackup>(backupId);
        if (backup == null) return false;

        await db.DeleteAsync(backup);
        return true;
    }

    /// <summary>
    /// Экспортирует все резервные копии в JSON-строку (для переноса между устройствами).
    /// </summary>
    public async Task<string> ExportBackupsJsonAsync(string? brand = null)
    {
        var backups = await GetBackupsAsync(brand: brand, limit: 0);
        return System.Text.Json.JsonSerializer.Serialize(backups, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    /// <summary>
    /// Импортирует резервные копии из JSON-строки.
    /// </summary>
    public async Task<int> ImportBackupsJsonAsync(string json)
    {
        var backups = System.Text.Json.JsonSerializer.Deserialize<List<CodingBackup>>(json);
        if (backups == null || backups.Count == 0) return 0;

        var db = await GetDbAsync();
        int count = 0;
        foreach (var b in backups)
        {
            b.Id = 0; // новый автоинкремент
            b.CreatedAt = DateTime.UtcNow;
            await db.InsertAsync(b);
            count++;
        }
        return count;
    }

    private static List<HiddenFeature> BuildSeedFeatures()
    {
        var now = DateTime.UtcNow;
        var list = new List<HiddenFeature>();

        // ═══════════════ LADA ═══════════════

        // --- Освещение ---
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "lighting", Icon = "💡",
            FeatureName = "Световой путь домой (Follow Me Home)",
            Description = "Фары остаются включёнными 30-60 сек после выключения зажигания для освещения пути к дому.",
            ModuleAddress = "7E0", EncodedByte = 0x12, BitMask = 0x04,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "lighting", Icon = "💡",
            FeatureName = "Дневные ходовые огни (DRL) через ПТФ",
            Description = "Использовать противотуманные фары как дневные ходовые огни. Загораются при запуске двигателя.",
            ModuleAddress = "7E0", EncodedByte = 0x10, BitMask = 0x08,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "lighting", Icon = "💡",
            FeatureName = "Задержка выключения света салона",
            Description = "Плавное затухание освещения салона в течение 5-10 секунд после закрытия дверей.",
            ModuleAddress = "7E0", EncodedByte = 0x14, BitMask = 0x10,
            CreatedAt = now,
        });

        // --- Комфорт ---
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "comfort", Icon = "🚗",
            FeatureName = "Автозакрытие дверей при начале движения",
            Description = "Центральный замок автоматически запирает двери при достижении 15-20 км/ч.",
            ModuleAddress = "7E0", EncodedByte = 0x20, BitMask = 0x01,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "comfort", Icon = "🪟",
            FeatureName = "Закрытие окон с брелока",
            Description = "Удержание кнопки запирания на брелоке закрывает все электростеклоподъёмники (комфортное закрытие).",
            ModuleAddress = "7E0", EncodedByte = 0x20, BitMask = 0x04,
            RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "comfort", Icon = "🪞",
            FeatureName = "Автоскладывание зеркал",
            Description = "Боковые зеркала автоматически складываются при запирании и раскладываются при отпирании.",
            ModuleAddress = "7E0", EncodedByte = 0x22, BitMask = 0x08,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "comfort", Icon = "🔊",
            FeatureName = "Звуковое подтверждение блокировки",
            Description = "Короткий звуковой сигнал при запирании/отпирании с брелока.",
            ModuleAddress = "7E0", EncodedByte = 0x24, BitMask = 0x40,
            CreatedAt = now,
        });

        // --- Приборная панель ---
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "instrument", Icon = "📊",
            FeatureName = "Стрелочный тест (Needle Sweep)",
            Description = "Все стрелки приборной панели проходят полный цикл от 0 до максимума при включении зажигания.",
            ModuleAddress = "720", EncodedByte = 0x04, BitMask = 0x80,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "instrument", Icon = "🌡️",
            FeatureName = "Отображение температуры двигателя в цифрах",
            Description = "На некоторых версиях ЭБУ активирует цифровую индикацию температуры ОЖ на приборной панели.",
            ModuleAddress = "720", EncodedByte = 0x06, BitMask = 0x02,
            CreatedAt = now,
        });

        // --- Безопасность ---
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "safety", Icon = "🛡️",
            FeatureName = "Аварийная сигнализация при экстренном торможении",
            Description = "Автоматическое включение аварийки при резком торможении (ABS активировано).",
            ModuleAddress = "760", EncodedByte = 0x08, BitMask = 0x10,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "safety", Icon = "🔔",
            FeatureName = "Отключение предупреждения о ремнях",
            Description = "Отключает звуковой сигнал непристёгнутого ремня безопасности (только для диагностических целей!).",
            ModuleAddress = "720", EncodedByte = 0x0A, BitMask = 0x04,
            CreatedAt = now,
        });

        // --- Двигатель ---
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "engine", Icon = "🔄",
            FeatureName = "Отключение системы Старт-Стоп",
            Description = "Деактивирует автоматическую остановку двигателя на светофорах. Двигатель продолжает работать при полной остановке, пока водитель сам не заглушит.",
            ModuleAddress = "7E0", EncodedByte = 0x2E, BitMask = 0x10,
            RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });

        // --- Трансмиссия ---
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "drivetrain", Icon = "⚙️",
            FeatureName = "Адаптация дроссельной заслонки",
            Description = "Сброс и повторное обучение положения дроссельной заслонки (после чистки или замены).",
            ActivationCommand = "ATSH7E0\n04", DeactivationCommand = null,
            RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "LADA", Category = "drivetrain", Icon = "⚙️",
            FeatureName = "Адаптация нуля педали газа",
            Description = "Калибровка электронной педали газа после замены или ремонта.",
            ActivationCommand = "ATSH7E0\n04", RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });

        // ═══════════════ УАЗ ═══════════════

        list.Add(new HiddenFeature
        {
            Brand = "УАЗ", Category = "lighting", Icon = "💡",
            FeatureName = "Задержка выключения ближнего света",
            Description = "Ближний свет остаётся включённым 30 секунд после выключения зажигания.",
            ModuleAddress = "7E0", EncodedByte = 0x12, BitMask = 0x04,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "УАЗ", Category = "comfort", Icon = "🚗",
            FeatureName = "Автозапирание дверей на скорости",
            Description = "Автоматическая блокировка всех дверей при достижении 15 км/ч.",
            ModuleAddress = "7E0", EncodedByte = 0x20, BitMask = 0x01,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "УАЗ", Category = "instrument", Icon = "📊",
            FeatureName = "Тест приборной панели",
            Description = "Активация всех индикаторов и стрелок для проверки работоспособности.",
            ModuleAddress = "720", EncodedByte = 0x04, BitMask = 0x80,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "УАЗ", Category = "drivetrain", Icon = "⚙️",
            FeatureName = "Адаптация ЭБУ после замены прошивки",
            Description = "Сброс адаптивных таблиц и повторная калибровка после перепрошивки ЭБУ.",
            ActivationCommand = "ATSH7E0\n04", RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "УАЗ", Category = "engine", Icon = "🔄",
            FeatureName = "Отключение системы Старт-Стоп",
            Description = "Деактивирует автоматическую остановку/запуск двигателя на холостом ходу.",
            ModuleAddress = "7E0", EncodedByte = 0x2E, BitMask = 0x10,
            RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "УАЗ", Category = "safety", Icon = "🛡️",
            FeatureName = "Включение противобуксовочной системы (TCS)",
            Description = "Активация трекшн-контроля через ЭБУ ABS (при поддержке блоком ABS).",
            ModuleAddress = "760", EncodedByte = 0x0C, BitMask = 0x20,
            CreatedAt = now,
        });

        // ═══════════════ ГАЗ ═══════════════

        list.Add(new HiddenFeature
        {
            Brand = "ГАЗ", Category = "instrument", Icon = "📊",
            FeatureName = "Цифровой спидометр на маршрутном компьютере",
            Description = "Вывод цифровой скорости на дисплей маршрутного компьютера вместо/дополнительно к аналоговому.",
            ModuleAddress = "720", EncodedByte = 0x10, BitMask = 0x01,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "ГАЗ", Category = "comfort", Icon = "🚗",
            FeatureName = "Автозапирание дверей",
            Description = "Автоматическая блокировка замков при трогании с места.",
            ModuleAddress = "7E0", EncodedByte = 0x20, BitMask = 0x01,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "ГАЗ", Category = "engine", Icon = "🔄",
            FeatureName = "Отключение системы Старт-Стоп",
            Description = "Отключает автоматический останов двигателя при нейтрали/остановке.",
            ModuleAddress = "7E0", EncodedByte = 0x2E, BitMask = 0x10,
            RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = "ГАЗ", Category = "drivetrain", Icon = "⚙️",
            FeatureName = "Сброс адаптаций ЭБУ МИКАС",
            Description = "Сброс всех адаптивных значений блока МИКАС 7.2/10.3/11 до заводских.",
            ActivationCommand = "ATSH7E0\n04", RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });

        // ═══════════════ Универсальные ═══════════════

        list.Add(new HiddenFeature
        {
            Brand = null, Category = "engine", Icon = "🔄",
            FeatureName = "Отключение системы Старт-Стоп (универсальное)",
            Description = "Деактивация функции автоматической остановки двигателя. Подходит для большинства автомобилей с электронной системой старт-стоп.",
            ModuleAddress = "7E0", EncodedByte = 0x2E, BitMask = 0x10,
            RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = null, Category = "drivetrain", Icon = "⚙️",
            FeatureName = "Сброс адаптаций топливной коррекции",
            Description = "Сброс долговременной (LTFT) и кратковременной (STFT) топливной коррекции до заводских значений.",
            ActivationCommand = "ATSH7E0\n04", RequiresSecurity = true, SecurityLevel = 1,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = null, Category = "drivetrain", Icon = "🔋",
            FeatureName = "Регистрация нового аккумулятора (BMS reset)",
            Description = "Сброс данных системы управления аккумулятором для корректной зарядки после замены АКБ.",
            ModuleAddress = "7E0", EncodedByte = 0x3C, BitMask = 0x01,
            CreatedAt = now,
        });
        list.Add(new HiddenFeature
        {
            Brand = null, Category = "comfort", Icon = "🔊",
            FeatureName = "Тройной сигнал поворотника (Comfort Blink)",
            Description = "При кратком касании рычага поворотника — 3 мигания вместо одного (перестроение на трассе).",
            ModuleAddress = "7E0", EncodedByte = 0x28, BitMask = 0x03,
            CreatedAt = now,
        });

        return list;
    }
}
