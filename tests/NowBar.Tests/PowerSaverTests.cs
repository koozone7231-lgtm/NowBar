using NowBar.App.Services;
using Xunit.Abstractions;
using static NowBar.App.Services.PowerSaverService;

namespace NowBar.Tests;

public sealed class PowerSaverTests(ITestOutputHelper output)
{
    [Fact]
    public void Tile_follows_the_windows_always_on_switch()
    {
        Assert.Equal(PowerSaverState.On, Interpret(ValueOn, reportedOn: false));
        Assert.Equal(PowerSaverState.Off, Interpret(ValueOff, reportedOn: false));
        // 한 번도 바뀐 적 없는 상태(빈 값)는 꺼짐
        Assert.Equal(PowerSaverState.Off, Interpret(0, reportedOn: false));
    }

    [Fact]
    public void Low_battery_saver_shows_as_on()
    {
        Assert.Equal(PowerSaverState.On, Interpret(ValueOff, reportedOn: true));
    }

    [Fact]
    public void Missing_switch_disables_the_tile()
    {
        Assert.Equal(PowerSaverState.Missing, Interpret(null, reportedOn: true));
    }

    /// <summary>실제 스위치를 읽고, 지금 값 그대로 다시 쓰기만 한다 → 사용자 설정은 바뀌지 않는다.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Real_switch_round_trips_with_a_no_op_write()
    {
        var svc = new PowerSaverService();
        var before = svc.ReadSwitch();
        output.WriteLine($"switch={before?.ToString() ?? "missing"} state={svc.GetState()}");
        if (before is not { } value || (value != ValueOn && value != ValueOff)) return; // 없거나 한 번도 바뀐 적 없음 → 쓰지 않는다
        Assert.True(svc.SetOn(value == ValueOn));
        Assert.Equal(value, svc.ReadSwitch());
    }
}
