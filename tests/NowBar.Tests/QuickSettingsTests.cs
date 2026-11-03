using NowBar.App.Services;
using Windows.Devices.Radios;
using Xunit.Abstractions;

namespace NowBar.Tests;

public sealed class QuickSettingsTests(ITestOutputHelper output)
{
    /// <summary>
    /// 실제 Windows API를 호출하되 "지금 값 그대로 다시 쓰기"만 한다 → 사용자 환경은 바뀌지 않는다.
    /// (Wi-Fi를 실제로 끄면 이 PC의 네트워크가 끊기므로 상태를 바꾸지 않는다)
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_apis_respond_with_no_op_writes()
    {
        var svc = new QuickSettingsService();

        var wifi = await svc.GetRadioAsync(RadioKind.WiFi);
        var bt = await svc.GetRadioAsync(RadioKind.Bluetooth);
        output.WriteLine($"Wi-Fi={wifi} Bluetooth={bt}");
        if (wifi != RadioToggle.Missing) Assert.True(await svc.SetRadioAsync(RadioKind.WiFi, wifi == RadioToggle.On));
        if (bt != RadioToggle.Missing) Assert.True(await svc.SetRadioAsync(RadioKind.Bluetooth, bt == RadioToggle.On));
        Assert.Equal(wifi, await svc.GetRadioAsync(RadioKind.WiFi));
        Assert.Equal(bt, await svc.GetRadioAsync(RadioKind.Bluetooth));

        var dark = svc.IsDarkMode();
        output.WriteLine($"Dark mode={dark}");
        svc.SetDarkMode(dark);
        Assert.Equal(dark, svc.IsDarkMode());

        var brightness = svc.GetBrightness();
        output.WriteLine($"Brightness={brightness?.ToString() ?? "not supported"}");
        if (brightness is { } b)
        {
            Assert.InRange(b, 0, 100);
            Assert.True(svc.SetBrightness(b));
        }
    }
}
