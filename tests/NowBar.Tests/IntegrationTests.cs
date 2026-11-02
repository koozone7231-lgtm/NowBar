using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

/// <summary>실제 외부 API / Windows API를 호출하는 통합 테스트 (인터넷 연결 필요).</summary>
public sealed class IntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OpenMeteo_geocoding_and_forecast_return_real_data()
    {
        var cache = Path.Combine(Path.GetTempPath(), $"nowbar-weather-{Guid.NewGuid():N}.json");
        try
        {
            var svc = new WeatherService(cachePath: cache);
            var results = await svc.SearchLocationsAsync("Seoul");
            var seoul = Assert.Single(results, r => r.Name == "서울특별시");

            var data = await svc.GetWeatherAsync(seoul, TemperatureUnit.Celsius);
            Assert.InRange(data.Temperature, -40, 50);
            Assert.NotNull(data.TodayMax);
            Assert.NotNull(data.TodayMin);
            Assert.NotNull(data.Humidity);

            svc.SaveCache(data);
            var cached = svc.LoadCache();
            Assert.NotNull(cached);
            Assert.True(cached!.Matches(seoul, TemperatureUnit.Celsius));
        }
        finally
        {
            File.Delete(cache);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Windows_media_session_manager_is_available()
    {
        using var media = new MediaService();
        Assert.True(await media.InitializeAsync(), media.InitError);
        // 재생 중인 앱이 없어도 목록 조회는 실패하지 않아야 한다
        Assert.NotNull(media.GetSessions());
    }
}
