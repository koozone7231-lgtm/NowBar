using NowBar.App.Helpers;
using static NowBar.App.Helpers.FullscreenDetector;

namespace NowBar.Tests;

public sealed class FullscreenDetectorTests
{
    private static readonly RECT Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void Window_must_cover_the_whole_monitor()
    {
        Assert.True(CoversMonitor(new RECT(0, 0, 1920, 1080), Monitor));
        Assert.True(CoversMonitor(new RECT(-8, -8, 1928, 1088), Monitor)); // 최대화 창처럼 테두리가 밖으로 나간 경우
        Assert.False(CoversMonitor(new RECT(0, 0, 1920, 1040), Monitor));  // 작업 표시줄이 보이는 일반 최대화
        Assert.False(CoversMonitor(new RECT(100, 100, 900, 700), Monitor));
        // 다른 모니터에 있는 전체 화면 창
        Assert.False(CoversMonitor(new RECT(1920, 0, 3840, 1080), Monitor));
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("WorkerW", true)]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", true)]
    [InlineData("Chrome_WidgetWin_1", false)]
    [InlineData("UnrealWindow", false)]
    public void Desktop_and_taskbar_are_not_fullscreen_apps(string className, bool shell) =>
        Assert.Equal(shell, IsShellClass(className));
}
