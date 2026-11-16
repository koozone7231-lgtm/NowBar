using System.Text;
using System.Text.RegularExpressions;
using NowBar.App.Services;

namespace NowBar.Tests;

/// <summary>빠른 질문의 링크 열기·앱 실행과 "Claude Code에서 계속" (실제로 실행하지는 않는다).</summary>
public sealed class QuickActionsTests
{
    [Fact]
    public void Action_lines_are_taken_out_of_the_answer()
    {
        var (text, actions) = QuickActions.Extract("유튜브와 메모장을 열게요.\n@open https://www.youtube.com\n@app 메모장");
        Assert.Equal("유튜브와 메모장을 열게요.", text);
        Assert.Equal([new QuickAction(QuickActionKind.OpenLink, "https://www.youtube.com"), new QuickAction(QuickActionKind.LaunchApp, "메모장")], actions);
        Assert.Equal("youtube.com", actions[0].Label);
    }

    [Fact]
    public void At_most_three_actions_and_plain_answers_are_untouched()
    {
        var many = string.Join('\n', Enumerable.Range(1, 5).Select(i => $"@open https://example{i}.com"));
        Assert.Equal(3, QuickActions.Extract(many).Actions.Count);

        var plain = QuickActions.Extract("이메일 주소는 me@example.com 입니다.");
        Assert.Empty(plain.Actions);
        Assert.Equal("이메일 주소는 me@example.com 입니다.", plain.Text);
    }

    [Fact]
    public void Pending_action_lines_are_hidden_while_streaming()
    {
        Assert.Equal("열게요.", QuickActions.HidePending("열게요.\n@op"));
    }

    [Theory]
    [InlineData("https://www.youtube.com", true)]
    [InlineData("http://example.com/a?b=c", true)]
    [InlineData("ms-settings:bluetooth", true)]
    [InlineData("mailto:me@example.com", true)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("youtube.com", false)]
    public void Only_web_settings_and_mail_links_open(string target, bool allowed) =>
        Assert.Equal(allowed, QuickActions.IsAllowedLink(target));

    [Theory]
    [InlineData("메모장", true)]
    [InlineData("chrome", true)]
    [InlineData("Visual Studio Code", true)]
    [InlineData("notepad.exe", true)]
    [InlineData("cmd", false)]
    [InlineData("powershell.exe", false)]
    [InlineData("power shell", false)]
    [InlineData("notepad C:\\secret.txt", false)]
    [InlineData("C:\\tools\\app.exe", false)]
    [InlineData("calc & del *", false)]
    public void Apps_are_names_only_and_never_command_runners(string name, bool safe) =>
        Assert.Equal(safe, QuickActions.IsSafeAppName(name));

    [Fact]
    public void App_names_match_the_start_menu()
    {
        AppEntry[] apps =
        [
            new("메모장", "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
            new("Google Chrome", "Chrome"),
            new("Chrome Remote Desktop", "crd"),
            new("명령 프롬프트", "cmd"),
            new("계산기", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"),
        ];
        Assert.Equal("메모장", QuickActions.MatchApp("메모장", apps)?.Name);
        Assert.Equal("Google Chrome", QuickActions.MatchApp("google chrome", apps)?.Name);
        Assert.Equal("Google Chrome", QuickActions.MatchApp("chrome", apps)?.Name); // 포함 중 가장 짧은 이름
        Assert.Equal("계산기", QuickActions.MatchApp("계산기", apps)?.Name);
        Assert.Null(QuickActions.MatchApp("포토샵", apps));
    }

    [Fact]
    public void Continue_command_decodes_back_to_the_question_and_answer()
    {
        const string question = "파이썬에서 \"리스트\" 정렬은?\n'따옴표' & $변수";
        const string answer = "sorted(x)를 쓰세요.\n- `x.sort()`는 제자리 정렬";
        var encoded = ClaudeCodeLauncher.BuildEncodedCommand(@"C:\Users\me\.local\bin\claude.exe", question, answer);

        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        var payload = Regex.Match(script, @"FromBase64String\('([^']+)'\)").Groups[1].Value;
        var prompt = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains(question, prompt);
        Assert.Contains(answer, prompt);
        Assert.Contains(@"& 'C:\Users\me\.local\bin\claude.exe' $p", script);
    }

    [Fact]
    public void Long_answers_are_shortened_to_fit_the_command_line()
    {
        var answer = string.Concat(Enumerable.Repeat("가나다라마바사 아자차카타파하. ", 2000)); // 3만 자 이상
        var encoded = ClaudeCodeLauncher.BuildEncodedCommand("claude.exe", "질문", answer);
        Assert.True(encoded.Length <= ClaudeCodeLauncher.MaxEncodedLength, $"{encoded.Length}");
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        var prompt = Encoding.UTF8.GetString(Convert.FromBase64String(Regex.Match(script, @"FromBase64String\('([^']+)'\)").Groups[1].Value));
        Assert.Contains("(생략)", prompt);
        Assert.Contains("[내 질문]\n질문", prompt);
    }
}

public sealed class TestInstanceIsolationTests
{
    [Theory]
    [InlineData(null, null, true)]                       // 실제 NowBar
    [InlineData(@"C:\tmp\nowbar-test", null, false)]     // 테스트 NowBar가 실제 Claude 설정을 건드리면 안 됨
    [InlineData(@"C:\tmp\nowbar-test", @"C:\tmp\fake-claude", true)] // 테스트용 Claude 설정 폴더를 따로 준 경우
    public void Test_instances_leave_real_claude_settings_alone(string? dataDir, string? claudeDir, bool allowed) =>
        Assert.Equal(allowed, NowBar.App.ViewModels.AIActivityViewModel.MayTouchClaudeSettings(dataDir, claudeDir));
}
