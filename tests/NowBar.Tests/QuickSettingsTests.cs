using NowBar.App.Services;
using Windows.Devices.Radios;
using Xunit.Abstractions;

namespace NowBar.Tests;

public sealed class QuickSettingsTests(ITestOutputHelper output)
{
    [Fact]
    public void Power_mode_mapping_and_cycle()
    {
        Assert.Equal(PowerMode.Efficiency, QuickSettingsService.FromOverlay(QuickSettingsService.EfficiencyOverlay));
        Assert.Equal(PowerMode.Performance, QuickSettingsService.FromOverlay(QuickSettingsService.PerformanceOverlay));
        Assert.Equal(PowerMode.Balanced, QuickSettingsService.FromOverlay(Guid.Empty));
        Assert.Equal(PowerMode.Balanced, QuickSettingsService.FromOverlay(new Guid("3af9b8d9-7c97-431d-ad78-34a8bfea439f")));
        Assert.Equal(PowerMode.Balanced, QuickSettingsService.Next(PowerMode.Efficiency));
        Assert.Equal(PowerMode.Performance, QuickSettingsService.Next(PowerMode.Balanced));
        Assert.Equal(PowerMode.Efficiency, QuickSettingsService.Next(PowerMode.Performance));
        Assert.Equal(PowerMode.Unavailable, QuickSettingsService.Next(PowerMode.Unavailable));
    }

    /// <summary>
    /// 실제 Windows API를 호출하되 "지금 값 그대로 다시 쓰기"만 한다 → 사용자 환경은 바뀌지 않는다.
    /// (Wi-Fi를 실제로 끄면 이 PC의 네트워크가 끊기므로 상태를 바꾸지 않는다)
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_apis_respond_with_no_op_writes()
    {
        using var svc = new QuickSettingsService();

        var wifi = await svc.GetRadioAsync(RadioKind.WiFi);
        var bt = await svc.GetRadioAsync(RadioKind.Bluetooth);
        output.WriteLine($"Wi-Fi={wifi} Bluetooth={bt}");
        if (wifi != RadioToggle.Missing) Assert.True(await svc.SetRadioAsync(RadioKind.WiFi, wifi == RadioToggle.On));
        if (bt != RadioToggle.Missing) Assert.True(await svc.SetRadioAsync(RadioKind.Bluetooth, bt == RadioToggle.On));
        Assert.Equal(wifi, await svc.GetRadioAsync(RadioKind.WiFi));
        Assert.Equal(bt, await svc.GetRadioAsync(RadioKind.Bluetooth));

        var mic = svc.GetMicMuted();
        output.WriteLine($"Mic muted={mic?.ToString() ?? "no device"}");
        if (mic is { } m)
        {
            Assert.True(svc.SetMicMuted(m));
            Assert.Equal(m, svc.GetMicMuted());
        }

        var dark = svc.IsDarkMode();
        output.WriteLine($"Dark mode={dark}");
        svc.SetDarkMode(dark);
        Assert.Equal(dark, svc.IsDarkMode());

        var power = svc.GetPowerMode();
        output.WriteLine($"Power mode={power}");
        if (power != PowerMode.Unavailable)
        {
            Assert.True(svc.SetPowerMode(power));
            Assert.Equal(power, svc.GetPowerMode());
        }

        var brightness = svc.GetBrightness();
        output.WriteLine($"Brightness={brightness?.ToString() ?? "not supported"}");
        if (brightness is { } b)
        {
            Assert.InRange(b, 0, 100);
            Assert.True(svc.SetBrightness(b));
        }
    }
}
