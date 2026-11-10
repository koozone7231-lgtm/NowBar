using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class ReleaseNotesTests
{
    private const string Sample = """
        # 변경 기록

        ## v1.6.0 — 2026-11-10

        ### 새 기능
        - **숨김 모드 버튼**: 펼친 패널에서 켜고 끕니다.
        - 업데이트 내용 팝업

        ### 다운로드
        | 파일 | 설명 |
        |---|---|
        | `NowBar-1.6.0-win-x64.exe` | 권장 |

        v1.2.0 이후 버전을 쓰고 있다면 자동으로 업데이트됩니다.

        ## v1.5.0 — 2026-11-06

        ### 새 기능
        - 충전 시작 표정

        ## v1.4.2 — 2026-11-04

        ### 수정
        - 절전 모드
        """;

    [Fact]
    public void Download_table_and_auto_update_line_are_left_out()
    {
        var sections = ReleaseNotes.Parse(Sample);
        Assert.Equal(3, sections.Count);
        var latest = sections[0];
        Assert.Equal(new Version(1, 6, 0), latest.Version);
        Assert.Equal("2026-11-10", latest.Date);
        Assert.Equal(["### 새 기능", "- **숨김 모드 버튼**: 펼친 패널에서 켜고 끕니다.", "- 업데이트 내용 팝업"], latest.Lines);
    }

    [Fact]
    public void Shows_every_version_since_the_last_one_seen()
    {
        var shown = ReleaseNotes.ToShow(Sample, "1.4.2", new Version(1, 6, 0), afterUpdate: true);
        Assert.Equal([new Version(1, 6, 0), new Version(1, 5, 0)], shown.Select(s => s.Version));
    }

    [Fact]
    public void Upgrading_from_before_this_feature_shows_only_the_current_version()
    {
        var shown = ReleaseNotes.ToShow(Sample, "", new Version(1, 6, 0), afterUpdate: true);
        Assert.Equal([new Version(1, 6, 0)], shown.Select(s => s.Version));
    }

    [Fact]
    public void Nothing_on_a_fresh_install_or_after_it_was_seen()
    {
        Assert.Empty(ReleaseNotes.ToShow(Sample, "", new Version(1, 6, 0), afterUpdate: false));
        Assert.Empty(ReleaseNotes.ToShow(Sample, "1.6.0", new Version(1, 6, 0), afterUpdate: true));
        // 이전 버전으로 되돌린 경우
        Assert.Empty(ReleaseNotes.ToShow(Sample, "1.6.0", new Version(1, 5, 0), afterUpdate: true));
    }

    [Fact]
    public void Assembly_version_with_four_parts_matches_three_part_heading()
    {
        var shown = ReleaseNotes.ToShow(Sample, "1.5.0", new Version(1, 6, 0, 0), afterUpdate: false);
        Assert.Equal([new Version(1, 6, 0)], shown.Select(s => s.Version));
    }

    [Fact]
    public void The_app_carries_a_changelog_entry_for_its_own_version()
    {
        var changelog = ReleaseNotes.LoadEmbedded();
        Assert.NotEmpty(changelog);
        var current = typeof(ReleaseNotes).Assembly.GetName().Version!; // 테스트 실행기 말고 NowBar 자체 버전
        Assert.Contains(ReleaseNotes.Parse(changelog), s => s.Version == new Version(current.Major, current.Minor, current.Build));
    }
}
