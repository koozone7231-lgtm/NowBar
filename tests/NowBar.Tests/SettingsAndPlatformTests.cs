using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class SettingsAndPlatformTests
{
    [Fact]
    public void Settings_roundtrip_and_normalize()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nowbar-settings-{Guid.NewGuid():N}.json");
        try
        {
            using (var svc = new SettingsService(path))
            {
                var s = svc.Load();
                Assert.False(string.IsNullOrEmpty(s.HookToken));
                s.Location = new GeoLocation("서울특별시", "서울특별시", "대한민국", 37.566, 126.9784, "Asia/Seoul");
                s.LastSelectedTab = NowBarTab.Music;
                s.CompactWidth = 5000; // 범위 밖
                s.Theme = AppTheme.Light;
            } // Dispose → 저장

            using var svc2 = new SettingsService(path);
            var loaded = svc2.Load();
            Assert.Equal("서울특별시", loaded.Location!.Name);
            Assert.Equal(NowBarTab.Music, loaded.LastSelectedTab);
            Assert.Equal(AppTheme.Light, loaded.Theme);
            Assert.Equal(AppSettings.MaxCompactWidth, loaded.CompactWidth);
            Assert.Contains("\"Theme\": \"Light\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nowbar-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ broken");
        try
        {
            using var svc = new SettingsService(path);
            var s = svc.Load();
            Assert.Equal(200, s.CompactWidth);
            Assert.Equal(NowBarTab.Weather, s.LastSelectedTab);
        }
        finally
        {
            File.Delete(path);
            foreach (var f in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*")) File.Delete(f);
        }
    }

    [Fact]
    public void Startup_registration_writes_and_removes_run_value()
    {
        var svc = new StartupService("NowBar_TEST_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            svc.SetEnabled(true, @"C:\Apps\NowBar\NowBar.exe");
            Assert.True(svc.IsEnabled());
            Assert.Equal("\"C:\\Apps\\NowBar\\NowBar.exe\" --startup", svc.GetRegisteredCommand());
        }
        finally
        {
            svc.SetEnabled(false);
        }
        Assert.False(svc.IsEnabled());
    }

    [Theory]
    [InlineData(0, true, "Sun", "맑음")]
    [InlineData(0, false, "Moon", "맑음")]
    [InlineData(61, true, "CloudRain", "약한 비")]
    [InlineData(75, true, "CloudSnow", "강한 눈")]
    [InlineData(95, true, "CloudLightning", "뇌우")]
    [InlineData(45, true, "CloudFog", "안개")]
    public void Weather_codes_map_to_icon_and_text(int code, bool isDay, string icon, string text)
    {
        Assert.Equal(icon, WeatherCodes.IconKind(code, isDay));
        Assert.Equal(text, WeatherCodes.Describe(code));
    }

    [Fact]
    public void Media_position_is_extrapolated_while_playing()
    {
        var now = DateTimeOffset.Now;
        var data = new MediaSessionData
        {
            State = MediaPlaybackState.Playing,
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(200),
            TimelineUpdatedAt = now - TimeSpan.FromSeconds(5),
        };
        Assert.InRange(data.EstimatePosition(now).TotalSeconds, 14.9, 15.1);

        var paused = new MediaSessionData
        {
            State = MediaPlaybackState.Paused,
            Position = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(200),
            TimelineUpdatedAt = now - TimeSpan.FromSeconds(5),
        };
        Assert.Equal(10, paused.EstimatePosition(now).TotalSeconds);
    }
}
