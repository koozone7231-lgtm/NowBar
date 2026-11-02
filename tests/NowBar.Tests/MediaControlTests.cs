using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

/// <summary>
/// 실제로 재생 중인 미디어 세션을 제어하는 테스트. 사용자 음악을 건드리므로
/// 환경 변수 NOWBAR_MEDIA_CONTROL_TEST=1 일 때만 실행한다 (무음 파일을 재생해 두고 실행 권장).
/// </summary>
public sealed class MediaControlTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("NOWBAR_MEDIA_CONTROL_TEST") == "1";

    [Fact]
    [Trait("Category", "Manual")]
    public async Task Reads_and_controls_live_media_session()
    {
        if (!Enabled) return;

        using var media = new MediaService();
        var updates = new List<MediaSessionData?>();
        media.Updated += (_, d) => { lock (updates) updates.Add(d); };
        Assert.True(await media.InitializeAsync(), media.InitError);

        var sessions = media.GetSessions();
        Assert.NotEmpty(sessions);
        await media.RefreshAsync();

        MediaSessionData Latest()
        {
            lock (updates) return updates.LastOrDefault(u => u is not null) ?? throw new Xunit.Sdk.XunitException("세션 정보 없음");
        }

        var first = Latest();
        Console.WriteLine($"app={first.AppName} title={first.Title} artist={first.Artist} state={first.State} " +
                          $"pos={first.Position} dur={first.Duration} canSeek={first.CanSeek} thumb={first.ThumbnailBytes?.Length}");
        Assert.False(first.ReadFailed);
        Assert.False(string.IsNullOrEmpty(first.AppName));

        // 재생/일시정지 토글 → 상태 변경 이벤트로 반영되는지 확인
        var before = first.State;
        Assert.True(await media.TogglePlayPauseAsync());
        await WaitFor(() => Latest().State != before);
        Console.WriteLine($"toggle: {before} -> {Latest().State}");
        Assert.True(await media.TogglePlayPauseAsync());
        await WaitFor(() => Latest().State == before);

        // 재생 위치 조절: 명령이 수락되는지 확인 (실제 이동 여부는 플레이어 쪽 로그로 확인.
        // 앱마다 타임라인 보고가 늦을 수 있어 GSMTC가 보고하는 위치는 즉시 바뀌지 않을 수 있다)
        if (Latest().CanSeek)
        {
            var target = TimeSpan.FromSeconds(Math.Min(60, Latest().Duration.TotalSeconds / 2));
            Assert.True(await media.SeekAsync(target));
            await Task.Delay(800);
            await media.RefreshAsync();
            Console.WriteLine($"seek target={target} reported={Latest().EstimatePosition(DateTimeOffset.Now)}");
        }

        // 다음/이전 명령은 앱이 지원하는 경우에만 true를 반환해야 한다
        Console.WriteLine($"canNext={Latest().CanNext} canPrev={Latest().CanPrevious}");
        if (Latest().CanNext) Assert.True(await media.SkipNextAsync());
        if (Latest().CanPrevious) Assert.True(await media.SkipPreviousAsync());
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 40; i++)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.True(condition(), "제한 시간 안에 상태가 바뀌지 않았습니다.");
    }
}
