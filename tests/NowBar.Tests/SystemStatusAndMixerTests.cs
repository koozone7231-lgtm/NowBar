using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class SystemStatusAndMixerTests
{
    [Theory]
    [InlineData(true, 12, false, false, true, true)]    // 방전 중 12% → 얼굴 반응 + 경고색
    [InlineData(true, 18, false, false, false, true)]   // 18% → 경고색만
    [InlineData(true, 10, true, true, false, false)]    // 충전 중이면 반응 없음
    [InlineData(true, 10, false, true, false, false)]   // 전원 연결됨
    [InlineData(false, 5, false, false, false, false)]  // 배터리 없는 데스크톱
    public void Battery_thresholds(bool hasBattery, int percent, bool charging, bool plugged, bool critical, bool low)
    {
        var s = new SystemStatusSnapshot(hasBattery, percent, charging, plugged, NetworkKind.Wifi, 4, "home");
        Assert.Equal(critical, s.IsBatteryCritical);
        Assert.Equal(low, s.IsBatteryLow);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Reads_real_battery_and_network_status()
    {
        var s = SystemStatusService.Read();
        Assert.InRange(s.BatteryPercent, 0, 100);
        Assert.InRange(s.WifiBars, 0, 5);
        Assert.True(Enum.IsDefined(s.Network));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void Reads_real_audio_mixer_without_changing_volume()
    {
        using var mixer = new AudioMixerService();
        var master = mixer.GetMaster();
        if (master is null) return; // 출력 장치가 없는 환경
        Assert.InRange(master.Volume, 0f, 1f);
        Assert.False(string.IsNullOrWhiteSpace(master.DeviceName));
        foreach (var app in mixer.GetApps())
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Name));
            Assert.InRange(app.Volume, 0f, 1f);
        }
    }
}
