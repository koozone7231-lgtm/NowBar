using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NowBar.App.Helpers;
using NowBar.App.Models;

namespace NowBar.App.Services;

public sealed class WeatherApiException(string message) : Exception(message);

/// <summary>
/// Open-Meteo 공개 API (API 키 불필요).
/// - 지역 검색: https://geocoding-api.open-meteo.com/v1/search
/// - 날씨:     https://api.open-meteo.com/v1/forecast
/// 마지막으로 성공한 결과는 %AppData%\NowBar\weather-cache.json 에 저장한다.
/// </summary>
public sealed class WeatherService
{
    private const string GeocodingUrl = "https://geocoding-api.open-meteo.com/v1/search";
    private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";

    private readonly HttpClient _http;
    private readonly string _cachePath;

    public WeatherService(HttpClient? http = null, string? cachePath = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("NowBar/1.0 (+Windows desktop widget)");
        _cachePath = cachePath ?? AppPaths.WeatherCacheFile;
    }

    public async Task<IReadOnlyList<GeoLocation>> SearchLocationsAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length < 2) return [];

        var url = $"{GeocodingUrl}?name={Uri.EscapeDataString(query)}&count=10&language=ko&format=json";
        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);

        var results = new List<GeoLocation>();
        if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in arr.EnumerateArray())
            {
                if (!r.TryGetProperty("latitude", out var lat) || !r.TryGetProperty("longitude", out var lon))
                    continue;
                results.Add(new GeoLocation(
                    Str(r, "name") ?? query,
                    Str(r, "admin1"),
                    Str(r, "country"),
                    lat.GetDouble(),
                    lon.GetDouble(),
                    Str(r, "timezone")));
            }
        }
        return results;
    }

    public async Task<WeatherData> GetWeatherAsync(GeoLocation location, TemperatureUnit unit, CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var url = string.Format(inv,
            "{0}?latitude={1}&longitude={2}" +
            "&current=temperature_2m,relative_humidity_2m,apparent_temperature,is_day,weather_code,precipitation_probability" +
            "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=auto&forecast_days=1{3}",
            ForecastUrl, location.Latitude, location.Longitude,
            unit == TemperatureUnit.Fahrenheit ? "&temperature_unit=fahrenheit" : "");

        using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
            throw new WeatherApiException("응답에 현재 날씨 정보가 없습니다.");

        var data = new WeatherData
        {
            LocationName = location.DisplayName,
            Latitude = location.Latitude,
            Longitude = location.Longitude,
            Unit = unit,
            Temperature = Num(current, "temperature_2m") ?? throw new WeatherApiException("응답에 기온 정보가 없습니다."),
            ApparentTemperature = Num(current, "apparent_temperature"),
            WeatherCode = (int)(Num(current, "weather_code") ?? -1),
            IsDay = (Num(current, "is_day") ?? 1) >= 1,
            Humidity = ToInt(Num(current, "relative_humidity_2m")),
            PrecipitationProbability = ToInt(Num(current, "precipitation_probability")),
            FetchedAt = DateTimeOffset.Now,
        };

        if (root.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Object)
        {
            data.TodayMax = FirstNum(daily, "temperature_2m_max");
            data.TodayMin = FirstNum(daily, "temperature_2m_min");
            data.PrecipitationProbabilityMax = ToInt(FirstNum(daily, "precipitation_probability_max"));
        }

        return data;
    }

    public WeatherData? LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            return JsonSerializer.Deserialize<WeatherData>(File.ReadAllText(_cachePath), SettingsService.JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.Error("날씨 캐시를 읽을 수 없습니다", ex);
            return null;
        }
    }

    public void SaveCache(WeatherData data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var tmp = _cachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, SettingsService.JsonOptions));
            File.Move(tmp, _cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error("날씨 캐시 저장 실패", ex);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string? reason = null;
            try
            {
                using var err = JsonDocument.Parse(body);
                reason = Str(err.RootElement, "reason");
            }
            catch (JsonException) { }
            throw new WeatherApiException($"날씨 서버 오류 ({(int)response.StatusCode}){(reason is null ? "" : ": " + reason)}");
        }

        try { return JsonDocument.Parse(body); }
        catch (JsonException) { throw new WeatherApiException("날씨 서버 응답을 해석할 수 없습니다."); }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static double? FirstNum(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
        && v[0].ValueKind == JsonValueKind.Number
            ? v[0].GetDouble()
            : null;

    private static int? ToInt(double? value) => value is null ? null : (int)Math.Round(value.Value);
}
