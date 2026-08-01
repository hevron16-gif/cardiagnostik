using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using CarDiagnosticApp.Models;

namespace CarDiagnosticApp.Services
{
    public class ApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;

        public string BaseUrl => _baseUrl;

        public ApiService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _baseUrl = httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "https://car-diagnostic-ai.onrender.com";
            // Диагностика AI на free-Render может «просыпаться» 30–60 с
            if (_httpClient.Timeout < TimeSpan.FromSeconds(90))
                _httpClient.Timeout = TimeSpan.FromSeconds(90);
            // Отключаем Expect: 100-continue — Cloudflare на Render может блокировать
            _httpClient.DefaultRequestHeaders.ExpectContinue = false;
            // User-Agent: Cloudflare WAF блокирует запросы без UA или с подозрительным UA
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "AutoDiag/1.0 (Android; .NET MAUI) AppleWebKit/537.36"
            );
            // Accept: Cloudflare проверяет Accept-заголовок
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept",
                "application/json, text/plain, */*"
            );
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Accept-Language",
                "ru-RU, ru;q=0.9, en;q=0.8"
            );
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        /// <summary>
        /// Нормализует ответ /diagnose: JSON {diagnosis|result} → читаемый текст.
        /// </summary>
        private static string? NormalizeDiagnosisResponse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var t = raw.Trim();
            if (!t.StartsWith("{") && !t.StartsWith("["))
                return t;

            try
            {
                var jo = JObject.Parse(t);
                var diagnosis = jo["diagnosis"]?.ToString()
                             ?? jo["text"]?.ToString()
                             ?? jo["message"]?.ToString();
                if (!string.IsNullOrWhiteSpace(diagnosis))
                    return diagnosis;

                var resultToken = jo["result"];
                if (resultToken is JObject resultObj)
                {
                    var desc = resultObj["description"]?.ToString()
                            ?? resultObj["diagnosis"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        var code = jo["error_code"]?.ToString()
                                ?? jo["code"]?.ToString()
                                ?? resultObj["code"]?.ToString()
                                ?? "";
                        var brand = jo["car_brand"]?.ToString() ?? jo["brand"]?.ToString() ?? "";
                        var model = jo["car_model"]?.ToString() ?? jo["model"]?.ToString() ?? "";
                        var sb = new StringBuilder();
                        sb.AppendLine("ОБЩАЯ ОЦЕНКА");
                        if (!string.IsNullOrWhiteSpace(brand) || !string.IsNullOrWhiteSpace(model))
                            sb.AppendLine($"Авто: {brand} {model}".Trim());
                        if (!string.IsNullOrWhiteSpace(code))
                            sb.AppendLine($"Код: {code}");
                        sb.AppendLine();
                        sb.AppendLine("ОПИСАНИЕ");
                        sb.AppendLine(desc);
                        return sb.ToString().Trim();
                    }
                }

                if (resultToken is JValue jv && jv.Type == JTokenType.String)
                    return jv.ToString();
            }
            catch
            {
                // не JSON — вернём как есть
            }

            return t;
        }

        /// <summary>
        /// Диагностика с обходом Cloudflare WAF.
        /// Стратегии (по порядку):
        ///   1. GET /diagnose (WAF не блокирует GET)
        ///   2. POST JSON с полями code/brand + error_code/car_brand (совместимость)
        ///   3. POST + text/plain body (base64 JSON)
        ///   4. POST + ?payload=&lt;base64&gt;
        /// </summary>
        public async Task<string?> Diagnose(string errorCode, string brand, string model, string? analyticsContext = null)
        {
            var errorCodeEscaped = Uri.EscapeDataString(errorCode ?? "");
            var brandEscaped = Uri.EscapeDataString(brand ?? "");
            var modelEscaped = Uri.EscapeDataString(model ?? "");
            var contextEscaped = Uri.EscapeDataString(analyticsContext ?? "");

            // --- Стратегия 1: GET (самая надёжная для WAF) ---
            var getUrl = $"{_baseUrl}/diagnose?error_code={errorCodeEscaped}"
                       + $"&car_brand={brandEscaped}"
                       + $"&car_model={modelEscaped}"
                       + $"&code={errorCodeEscaped}"
                       + $"&brand={brandEscaped}"
                       + $"&model={modelEscaped}";
            if (!string.IsNullOrEmpty(analyticsContext))
                getUrl += $"&context={contextEscaped}";

            try
            {
                var getResponse = await _httpClient.GetAsync(getUrl);
                if (getResponse.IsSuccessStatusCode)
                {
                    var bytes = await getResponse.Content.ReadAsByteArrayAsync();
                    return NormalizeDiagnosisResponse(Encoding.UTF8.GetString(bytes));
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] GET /diagnose → {getResponse.StatusCode}, пробуем POST");
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiService] GET failed: {ex.Message}, пробуем POST");
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[ApiService] GET timeout, пробуем POST");
            }

            // Пелоад: сервер v1.0.15+ ждёт code/brand/model;
            // старые клиенты/WAF-обходы — error_code/car_brand/car_model.
            var jsonPayload = System.Text.Json.JsonSerializer.Serialize(new
            {
                code = errorCode ?? "",
                brand = brand ?? "",
                model = model ?? "",
                error_code = errorCode ?? "",
                car_brand = brand ?? "",
                car_model = model ?? "",
                context = analyticsContext ?? ""
            });
            var jsonBytes = Encoding.UTF8.GetBytes(jsonPayload);
            var base64Payload = Convert.ToBase64String(jsonBytes)
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');

            // --- Стратегия 2: POST application/json (основной путь API) ---
            try
            {
                var jsonContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var jsonResponse = await _httpClient.PostAsync(
                    $"{_baseUrl}/diagnose", jsonContent);
                if (jsonResponse.IsSuccessStatusCode)
                {
                    var bytes = await jsonResponse.Content.ReadAsByteArrayAsync();
                    return NormalizeDiagnosisResponse(Encoding.UTF8.GetString(bytes));
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] POST JSON → {jsonResponse.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] POST JSON failed: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[ApiService] POST JSON timeout");
            }

            // --- Стратегия 3: POST text/plain (base64) — обход WAF ---
            try
            {
                var textContent = new StringContent(base64Payload, Encoding.UTF8, "text/plain");
                var textResponse = await _httpClient.PostAsync(
                    $"{_baseUrl}/diagnose", textContent);
                if (textResponse.IsSuccessStatusCode)
                {
                    var bytes = await textResponse.Content.ReadAsByteArrayAsync();
                    return NormalizeDiagnosisResponse(Encoding.UTF8.GetString(bytes));
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] POST bypass (text/plain) → {textResponse.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] POST bypass (text/plain) failed: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[ApiService] POST bypass (text/plain) timeout");
            }

            // --- Стратегия 4: POST с ?payload=<base64> ---
            try
            {
                var bypassUrl = $"{_baseUrl}/diagnose?payload={base64Payload}";
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var bypassResponse = await _httpClient.PostAsync(bypassUrl, content);
                if (bypassResponse.IsSuccessStatusCode)
                {
                    var bytes = await bypassResponse.Content.ReadAsByteArrayAsync();
                    return NormalizeDiagnosisResponse(Encoding.UTF8.GetString(bytes));
                }
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] POST bypass (query) → {bypassResponse.StatusCode}");
            }
            catch (HttpRequestException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ApiService] POST bypass (query) failed: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine("[ApiService] POST bypass (query) timeout");
            }

            return null;
        }

        public async Task<List<CarBrand>?> GetCarBrands()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/car_brands");

                if (response.IsSuccessStatusCode)
                {
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();
                    var json = Encoding.UTF8.GetString(responseBytes);
                    return JsonConvert.DeserializeObject<List<CarBrand>>(json);
                }
            }
            catch
            {
                // Сервер недоступен — используем локальный файл
            }

            // Fallback: загружаем cars.json из ресурсов приложения
            try
            {
                using var stream = await FileSystem.OpenAppPackageFileAsync("cars.json");
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var json = await reader.ReadToEndAsync();
                return JsonConvert.DeserializeObject<List<CarBrand>>(json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiService] Не удалось загрузить cars.json: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Отправляет отзыв пользователя на сервер.
        /// </summary>
        public async Task SendFeedback(string errorCode, bool helpful, string? carBrand = null, string? carModel = null, string? diagnosis = null, string? comment = null)
        {
            var payload = new { error_code = errorCode, helpful, car_brand = carBrand, car_model = carModel, diagnosis, comment };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                await _httpClient.PostAsync($"{_baseUrl}/feedback", content);
            }
            catch
            {
                // Молча игнорируем ошибки отправки — не блокируем UI
            }
        }

        /// <summary>
        /// Очищает всю историю диагностик на сервере.
        /// </summary>
        public async Task<bool> ClearHistory()
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{_baseUrl}/history");
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Загружает историю диагностик с сервера.
        /// </summary>
        public async Task<List<HistoryItem>?> GetHistory()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/history?limit=50");
                if (response.IsSuccessStatusCode)
                {
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();
                    var json = Encoding.UTF8.GetString(responseBytes);
                    return JsonConvert.DeserializeObject<List<HistoryItem>>(json);
                }
            }
            catch
            {
                // Ошибка сети — вернём null
            }
            return null;
        }

        /// <summary>
        /// Синхронизация: загружает дельту данных с сервера.
        /// Возвращает JSON-строку ответа или null при ошибке.
        /// </summary>
        public async Task<string?> SyncDataAsync(string? since = null, string? carBrand = null, int limit = 100)
        {
            var url = $"{_baseUrl}/sync?limit={limit}";
            if (!string.IsNullOrEmpty(since))
                url += $"&since={Uri.EscapeDataString(since)}";
            if (!string.IsNullOrEmpty(carBrand))
                url += $"&car_brand={Uri.EscapeDataString(carBrand)}";

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();
                    return Encoding.UTF8.GetString(responseBytes);
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Получает краткую сводку с сервера (количество записей).
        /// </summary>
        public async Task<string?> GetSyncSummaryAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{_baseUrl}/sync/summary");
                if (response.IsSuccessStatusCode)
                {
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();
                    return Encoding.UTF8.GetString(responseBytes);
                }
            }
            catch { }
            return null;
        }
        /// <summary>
        /// Отправляет готовый диагноз на сервер для сохранения (без вызова AI).
        /// Используется для загрузки офлайн-найденных решений при восстановлении сети.
        /// </summary>
        public async Task<bool> UploadDiagnosisAsync(string errorCode, string carBrand,
            string carModel, string diagnosis, string source = "client_offline")
        {
            try
            {
                var payload = new
                {
                    error_code = errorCode,
                    car_brand = carBrand,
                    car_model = carModel,
                    diagnosis,
                    source,
                };
                var json = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_baseUrl}/sync/upload", content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }
        /// <summary>
        /// Отправляет живые данные датчиков на AI-анализ.
        /// </summary>
        public async Task<LiveAnalysisResult?> AnalyzeLiveData(string carBrand, string carModel,
            List<LivePidItem> pids)
        {
            try
            {
                var request = new LiveAnalyzeRequest
                {
                    CarBrand = carBrand,
                    CarModel = carModel,
                    Pids = pids
                };
                var json = JsonConvert.SerializeObject(request);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_baseUrl}/live-analyze", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseBytes = await response.Content.ReadAsByteArrayAsync();
                    var body = Encoding.UTF8.GetString(responseBytes);
                    return JsonConvert.DeserializeObject<LiveAnalysisResult>(body);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Ищет/скачивает схемы: /schemas/{code}/download (реальные картинки)
        /// + /schemas/{code} (структура узлов). Старый /scheme-search на сервере нет (404).
        /// </summary>
        public async Task<SchemeSearchResult?> SearchSchemesAsync(
            string errorCode, string carBrand, string carModel, int maxResults = 6)
        {
            try
            {
                var code = Uri.EscapeDataString(errorCode ?? "");
                var brand = Uri.EscapeDataString(carBrand ?? "LADA");

                // 1) Скачивание картинок с сервера
                var downloadUrl = $"{_baseUrl}/schemas/{code}/download?brand={brand}&user_id=test";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var response = await _httpClient.GetAsync(downloadUrl, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(cts.Token));
                    var jo = JObject.Parse(body);

                    // Paywall / пустой ответ
                    if (jo.Value<bool?>("available") == false)
                        return BuildFallbackSearchResult(errorCode, carBrand, carModel, jo.Value<string>("message"));

                    var images = jo["images"] as JArray;
                    var items = new List<SchemeSearchItem>();
                    if (images != null)
                    {
                        foreach (var img in images.Take(maxResults))
                        {
                            // images may be strings or objects
                            string? url = null;
                            string? page = null;
                            if (img.Type == JTokenType.String)
                                url = img.Value<string>();
                            else
                            {
                                url = img.Value<string>("url")
                                   ?? img.Value<string>("image_url")
                                   ?? img.Value<string>("full_image_url")
                                   ?? img.Value<string>("path");
                                page = img.Value<string>("page_url") ?? img.Value<string>("source");
                            }
                            if (string.IsNullOrWhiteSpace(url)) continue;
                            // Локальные пути сервера → абсолютный URL
                            if (url.StartsWith("schemas/") || url.StartsWith("/schemas/"))
                                url = $"{_baseUrl.TrimEnd('/')}/{url.TrimStart('/')}";
                            else if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                                     url.Contains('/') && !url.Contains("://"))
                                url = $"{_baseUrl.TrimEnd('/')}/schemas/downloaded/{Path.GetFileName(url)}";

                            items.Add(new SchemeSearchItem
                            {
                                title = $"Схема {errorCode} ({carBrand} {carModel})".Trim(),
                                url = url,
                                full_image_url = url,
                                image_url = url,
                                page_url = page ?? url,
                                source = "direct",
                                snippet = jo.Value<bool?>("cached") == true
                                    ? "Из библиотеки сервера"
                                    : "Найдено и скачано сервером",
                            });
                        }
                    }

                    if (items.Count > 0)
                    {
                        return new SchemeSearchResult
                        {
                            results = items,
                            total_found = items.Count,
                            query = $"{errorCode} {carBrand} {carModel}",
                        };
                    }
                }

                // 2) Fallback: structural schema JSON
                var schemaJson = await GetRawAsync($"/schemas/{code}?user_id=test");
                if (!string.IsNullOrWhiteSpace(schemaJson))
                {
                    var jo = JObject.Parse(schemaJson);
                    if (jo.Value<bool?>("available") == true)
                    {
                        var data = jo["data"];
                        var imageUrl = data?.Value<string>("image_url");
                        var items = new List<SchemeSearchItem>();
                        if (!string.IsNullOrWhiteSpace(imageUrl))
                        {
                            items.Add(new SchemeSearchItem
                            {
                                title = data?.Value<string>("title") ?? $"Схема {errorCode}",
                                url = imageUrl!,
                                full_image_url = imageUrl!,
                                image_url = imageUrl!,
                                page_url = $"{_baseUrl}/schemas/{code}",
                                source = "direct",
                                snippet = data?.Value<string>("description") ?? "",
                            });
                        }
                        // SVG endpoint
                        items.Add(new SchemeSearchItem
                        {
                            title = $"2D SVG — {errorCode}",
                            url = $"{_baseUrl}/schemas/{code}/image?user_id=test",
                            full_image_url = $"{_baseUrl}/schemas/{code}/image?user_id=test",
                            image_url = $"{_baseUrl}/schemas/{code}/image?user_id=test",
                            page_url = $"{_baseUrl}/schemas/{code}",
                            source = "direct",
                            snippet = "Векторная схема с сервера",
                        });
                        return new SchemeSearchResult
                        {
                            results = items,
                            total_found = items.Count,
                            query = errorCode,
                        };
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ApiService] SearchSchemes: {ex.Message}");
                return null;
            }
        }

        private static SchemeSearchResult BuildFallbackSearchResult(
            string? errorCode, string? brand, string? model, string? message)
        {
            return new SchemeSearchResult
            {
                results = new List<SchemeSearchItem>(),
                total_found = 0,
                query = $"{errorCode} {brand} {model}",
                // message not in model - put in first item snippet via empty list
            };
        }

        /// <summary>
        /// GET-запрос к произвольному эндпоинту сервера.
        /// Возвращает сырой JSON-строку или null.
        /// </summary>
        public async Task<string?> GetRawAsync(string relativeUrl)
        {
            try
            {
                var url = relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? relativeUrl
                    : $"{_baseUrl}{relativeUrl}";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var response = await _httpClient.GetAsync(url, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                    return Encoding.UTF8.GetString(bytes);
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Получить JSON схемы узла /schemas/{code} (библиотека сервера).</summary>
        public async Task<string?> GetSchemaJsonAsync(string errorCode, string userId = "test")
        {
            var code = Uri.EscapeDataString((errorCode ?? "").Trim().ToUpperInvariant());
            return await GetRawAsync($"/schemas/{code}?user_id={Uri.EscapeDataString(userId)}");
        }

        /// <summary>Список схем в библиотеке сервера GET /schemas.</summary>
        public async Task<string?> GetSchemasLibraryJsonAsync()
        {
            return await GetRawAsync("/schemas");
        }
    }
}
