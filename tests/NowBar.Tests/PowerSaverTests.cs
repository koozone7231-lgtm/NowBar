using NowBar.App.Services;
using Xunit.Abstractions;
using static NowBar.App.Services.PowerSaverService;

namespace NowBar.Tests;

public sealed class PowerSaverTests(ITestOutputHelper output)
{
    [Fact]
    public void Turning_on_remembers_the_current_thresholds()
    {
        var plan = Decide(isOn: false, new Thresholds(0, 30), remembered: null);
        Assert.Equal(new Plan.TurnOn(new Thresholds(0, 30)), plan);
    }

    [Fact]
    public void Turning_off_restores_what_was_remembered()
    {
        var plan = Decide(isOn: true, new Thresholds(Always, Always), new Thresholds(0, 30));
        Assert.Equal(new Plan.TurnOff(new Thresholds(0, 30)), plan);
    }

    [Fact]
    public void Turning_off_without_a_memory_falls_back_to_windows_defaults()
    {
        // 다른 곳(Windows 설정의 "항상")에서 켰거나 NowBar 설정이 지워진 경우
        Assert.Equal(new Plan.TurnOff(new Thresholds(DefaultAc, DefaultDc)), Decide(true, new Thresholds(Always, Always), null));
        // 배터리 쪽만 "항상"이면 전원 연결 쪽 값은 그대로 둔다
        Assert.Equal(new Plan.TurnOff(new Thresholds(0, DefaultDc)), Decide(true, new Thresholds(0, Always), null));
        // 기억한 값이 이미 "항상"이면 믿지 않는다
        Assert.Equal(new Plan.TurnOff(new Thresholds(DefaultAc, DefaultDc)),
            Decide(true, new Thresholds(Always, Always), new Thresholds(Always, Always)));
    }

    [Fact]
    public void Always_setting_wins_even_if_the_state_has_not_caught_up()
    {
        // 방금 켜서 아직 상태 플래그가 꺼짐으로 읽혀도, 다시 누르면 끈다
        Assert.IsType<Plan.TurnOff>(Decide(isOn: false, new Thresholds(Always, Always), new Thresholds(0, 20)));
    }

    [Fact]
    public void Low_battery_saver_is_left_to_windows()
    {
        Assert.Equal(new Plan.HeldByWindows(30), Decide(isOn: true, new Thresholds(0, 30), null));
    }

    /// <summary>실제 전원 구성표를 읽고, 지금 값 그대로 다시 쓰기만 한다 → 사용자 설정은 바뀌지 않는다.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void Real_power_scheme_round_trips_with_a_no_op_write()
    {
        var svc = new PowerSaverService();
        var before = svc.ReadThresholds();
        output.WriteLine($"State={svc.GetState()} thresholds={before}");
        if (before is not { } value) return; // 절전 모드 설정이 없는 Windows
        Assert.InRange(value.Ac, 0, Always);
        Assert.InRange(value.Dc, 0, Always);
        Assert.True(svc.WriteThresholds(value));
        Assert.Equal(value, svc.ReadThresholds());
        Assert.NotEqual(PowerSaverState.Missing, svc.GetState());
    }
}
