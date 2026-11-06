using NowBar.App.Models;
using NowBar.App.ViewModels;

namespace NowBar.Tests;

public sealed class ChargingFaceTests
{
    private static SystemStatusSnapshot Battery(bool plugged, bool charging = false, int percent = 40) =>
        new(true, percent, charging, plugged, NetworkKind.Wifi, 4, null);

    [Fact]
    public void Plugging_in_the_charger_is_detected()
    {
        Assert.True(SystemStatusViewModel.IsPlugInMoment(Battery(plugged: false), Battery(plugged: true, charging: true)));
        // 꽂았지만 이미 가득 차서 충전 중이 아니어도 연결은 연결
        Assert.True(SystemStatusViewModel.IsPlugInMoment(Battery(false, percent: 100), Battery(true, percent: 100)));
    }

    [Fact]
    public void Other_changes_are_not_a_plug_in()
    {
        // 이미 연결된 상태에서 퍼센트만 바뀜
        Assert.False(SystemStatusViewModel.IsPlugInMoment(Battery(true, true, 40), Battery(true, true, 41)));
        // 뽑음
        Assert.False(SystemStatusViewModel.IsPlugInMoment(Battery(true), Battery(false)));
        // 시작할 때 첫 상태 (이전 상태를 모름)
        Assert.False(SystemStatusViewModel.IsPlugInMoment(SystemStatusSnapshot.Unknown, Battery(true)));
        // 배터리 없는 데스크톱
        var desktop = new SystemStatusSnapshot(false, 100, false, true, NetworkKind.Ethernet, 0, null);
        Assert.False(SystemStatusViewModel.IsPlugInMoment(desktop with { IsPluggedIn = false }, desktop));
    }
}
