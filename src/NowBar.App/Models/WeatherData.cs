using System.Text.Json.Serialization;

namespace NowBar.App.Models;

public enum TemperatureUnit { Celsius, Fahrenheit }

/// <summary>Open-Meteo Geocoding API 검색 결과로 사용자가 선택한 지역.</summary>
public sealed record GeoLocation(
    string Name,
    string? Admin1,
    string? Country,
    double Latitude,
    double Longitude,
    string? Timezone)
{
    [JsonIgnore]
    public string DisplayName =>
        string.Join(", ", new[] { Name, Admin1, Country }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct());
}

/// <summary>Open-Meteo Forecast API에서 받은 현재 날씨. 캐시 파일에도 이 형식으로 저장된다.</summary>
public sealed class WeatherData
{
    public string LocationName { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public TemperatureUnit Unit { get; set; }

    public double Temperature { get; set; }
    public double? ApparentTemperature { get; set; }
    public int WeatherCode { get; set; }
    public bool IsDay { get; set; } = true;
    public int? Humidity { get; set; }
    /// <summary>현재 시각 기준 강수 확률(%).</summary>
    public int? PrecipitationProbability { get; set; }
    /// <summary>오늘 최대 강수 확률(%).</summary>
    public int? PrecipitationProbabilityMax { get; set; }
    public double? TodayMax { get; set; }
    public double? TodayMin { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public bool Matches(GeoLocation? location, TemperatureUnit unit) =>
        location is not null
        && Unit == unit
        && Math.Abs(Latitude - location.Latitude) < 0.01
        && Math.Abs(Longitude - location.Longitude) < 0.01;
}
