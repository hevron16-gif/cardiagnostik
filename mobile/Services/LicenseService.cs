using CarDiagnosticApp.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

namespace CarDiagnosticApp.Services;

/// <summary>
/// Сервис управления лицензией.
/// Хранит ключ в SecureStorage, валидирует на сервере, кеширует статус локально.
/// </summary>
public class LicenseService
{
    private const string LicenseKeyStorage = "license_key";
    private const string CachedTierStorage = "cached_tier";
    private const string CachedFeaturesStorage = "cached_features";
    private const string CachedValidUntilStorage = "cached_valid_until";

    /// <summary>
    /// ВРЕМЕННО: открыть все функции (Pro) для тестирования.
    /// Перед продакшен-релизом поставить <c>false</c>.
    /// </summary>
    public const bool TestingUnlockAll = true;

    private readonly ApiService _api;
    private LicenseInfo? _cachedInfo;
    private string? _deviceId;

    public LicenseService(ApiService api)
    {
        _api = api;
        if (TestingUnlockAll)
            _cachedInfo = CreateUnlockedInfo();
    }

    // ════════════════ Свойства ════════════════

    /// <summary>
    /// Текущий закешированный статус (без запроса к серверу).
    /// </summary>
    public LicenseInfo CachedInfo => _cachedInfo ??= TestingUnlockAll
        ? CreateUnlockedInfo()
        : new LicenseInfo
        {
            Tier = "free",
            IsPaid = false,
            Message = "Лицензия не активирована.",
        };

    public bool IsPaid => TestingUnlockAll || CachedInfo.IsPaid;
    public string CurrentTier => TestingUnlockAll ? "pro" : CachedInfo.Tier;

    /// <summary>
    /// Есть ли доступ к конкретной фиче.
    /// </summary>
    public bool CanAccess(string feature)
    {
        if (TestingUnlockAll) return true;
        return CachedInfo.Features.Contains(feature);
    }

    private static LicenseInfo CreateUnlockedInfo() => new()
    {
        Tier = "pro",
        IsPaid = true,
        IsExpired = false,
        ValidUntil = "2099-12-31",
        Features = FeatureFlags.FreeFeatures
            .Concat(FeatureFlags.PaidFeatures)
            .Concat(FeatureFlags.EnterpriseOnly)
            .Distinct()
            .ToList(),
        LockedFeatures = new List<string>(),
        Message = "Режим тестирования: все функции открыты (Pro).",
    };

    // ════════════════ Инициализация ════════════════

    /// <summary>
    /// Загрузить кеш из SecureStorage при старте приложения.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Тестовый unlock — не затираем Pro на free из SecureStorage
        if (TestingUnlockAll)
        {
            _cachedInfo = CreateUnlockedInfo();
            return;
        }

        try
        {
            var tier = await SecureStorage.GetAsync(CachedTierStorage);
            var featuresJson = await SecureStorage.GetAsync(CachedFeaturesStorage);
            var validUntil = await SecureStorage.GetAsync(CachedValidUntilStorage);

            if (!string.IsNullOrEmpty(tier) && !string.IsNullOrEmpty(featuresJson))
            {
                _cachedInfo = new LicenseInfo
                {
                    Tier = tier,
                    IsPaid = tier != "free",
                    ValidUntil = validUntil,
                    Features = featuresJson.Split('|').ToList(),
                    Message = $"Лицензия {tier.ToUpper()} (кеш).",
                };
            }
        }
        catch
        {
            _cachedInfo = new LicenseInfo { Tier = "free", IsPaid = false };
        }
    }

    // ════════════════ Активация ════════════════

    /// <summary>
    /// Активировать лицензионный ключ.
    /// </summary>
    public async Task<LicenseActivationResult> ActivateAsync(string key)
    {
        try
        {
            var deviceId = await GetDeviceIdAsync();
            var requestBody = JsonConvert.SerializeObject(new LicenseActivateRequest
            {
                Key = key.Trim().ToUpperInvariant(),
                DeviceId = deviceId,
            });

            var baseUrl = GetBaseUrl();
            var url = $"{baseUrl}/license/activate?user_id={Uri.EscapeDataString(deviceId)}";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(url, content);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonConvert.DeserializeObject<LicenseActivationResult>(json)
                             ?? new LicenseActivationResult();

                if (result.Success)
                {
                    // Сохраняем ключ и кеш
                    await SecureStorage.SetAsync(LicenseKeyStorage, key);
                    await CacheLicenseInfo(result.Tier ?? "pro", result.Features);
                    await RefreshAsync(); // синхронизируем с сервером
                }

                return result;
            }
            else
            {
                var errorJson = await response.Content.ReadAsStringAsync();
                try
                {
                    var errObj = JObject.Parse(errorJson);
                    var detail = errObj["detail"];
                    if (detail is JObject obj)
                    {
                        return JsonConvert.DeserializeObject<LicenseActivationResult>(obj.ToString())
                               ?? new LicenseActivationResult { Success = false, Error = "parse_error" };
                    }
                    return new LicenseActivationResult
                    {
                        Success = false,
                        Error = "api_error",
                        Message = detail?.Value<string>() ?? "Ошибка сервера."
                    };
                }
                catch
                {
                    return new LicenseActivationResult { Success = false, Error = "api_error", Message = errorJson };
                }
            }
        }
        catch (Exception ex)
        {
            return new LicenseActivationResult
            {
                Success = false,
                Error = "network",
                Message = $"Ошибка сети: {ex.Message}",
            };
        }
    }

    // ════════════════ Проверка статуса ════════════════

    /// <summary>
    /// Обновить статус лицензии с сервера.
    /// </summary>
    public async Task<LicenseInfo> RefreshAsync()
    {
        try
        {
            var deviceId = await GetDeviceIdAsync();
            var baseUrl = GetBaseUrl();
            var url = $"{baseUrl}/license/status?user_id={Uri.EscapeDataString(deviceId)}&device_id={Uri.EscapeDataString(deviceId)}";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = await client.GetStringAsync(url);
            var info = JsonConvert.DeserializeObject<LicenseInfo>(json);

            if (info != null)
            {
                _cachedInfo = info;
                await CacheLicenseInfo(info.Tier, info.Features);
                return info;
            }
        }
        catch
        {
            // Оставляем локальный кеш
        }

        return CachedInfo;
    }

    /// <summary>
    /// Деактивировать лицензию (выйти из Pro).
    /// </summary>
    public async Task DeactivateAsync()
    {
        SecureStorage.Remove(LicenseKeyStorage);
        SecureStorage.Remove(CachedTierStorage);
        SecureStorage.Remove(CachedFeaturesStorage);
        SecureStorage.Remove(CachedValidUntilStorage);

        _cachedInfo = new LicenseInfo
        {
            Tier = "free",
            IsPaid = false,
            Features = new List<string>(FeatureFlags.FreeFeatures),
            Message = "Бесплатная версия.",
        };
    }

    // ════════════════ Приватные ════════════════

    private async Task CacheLicenseInfo(string tier, List<string>? features)
    {
        try
        {
            await SecureStorage.SetAsync(CachedTierStorage, tier);
            if (features != null)
                await SecureStorage.SetAsync(CachedFeaturesStorage, string.Join("|", features));
        }
        catch { /* SecureStorage может быть недоступен в тестах */ }
    }

    private static async Task<string> GetDeviceIdAsync()
    {
        try
        {
            var id = await SecureStorage.GetAsync("device_id");
            if (!string.IsNullOrEmpty(id)) return id;

            id = Guid.NewGuid().ToString("N")[..16];
            await SecureStorage.SetAsync("device_id", id);
            return id;
        }
        catch
        {
            return "unknown-device";
        }
    }

    private static string GetBaseUrl()
    {
        // Берём из MauiProgram (зарегистрированный HttpClient)
        return "https://car-diagnostic-ai.onrender.com";
    }
}
