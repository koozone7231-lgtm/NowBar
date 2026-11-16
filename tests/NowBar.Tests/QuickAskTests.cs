using System.Windows.Documents;
using NowBar.App.Helpers;
using NowBar.App.Services;

namespace NowBar.Tests;

/// <summary>빠른 질문: Claude Code 출력 해석과 실행 옵션 (실제 claude는 호출하지 않는다).</summary>
public sealed class QuickAskTests
{
    [Fact]
    public void Text_deltas_are_streamed()
    {
        const string line = """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"둘"}},"session_id":"x"}""";
        var parsed = ClaudeAskService.ParseLine(line);
        Assert.Equal("둘", parsed.Delta);
        Assert.Null(parsed.Final);
    }

    [Fact]
    public void Final_result_and_errors_are_read()
    {
        var ok = ClaudeAskService.ParseLine("""{"type":"result","subtype":"success","is_error":false,"result":"4","session_id":"x"}""");
        Assert.Equal("4", ok.Final);
        Assert.False(ok.IsError);

        var failed = ClaudeAskService.ParseLine("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"Not logged in","session_id":"x"}""");
        Assert.True(failed.IsError);
        Assert.Equal("Not logged in", failed.Final);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[claude-code:unrecognized_model] {\"model\":\"x\"}")]
    [InlineData("""{"type":"system","subtype":"init","tools":[]}""")]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"..."}}}""")]
    [InlineData("{not json")]
    public void Other_lines_are_ignored(string line)
    {
        var parsed = ClaudeAskService.ParseLine(line);
        Assert.Null(parsed.Delta);
        Assert.Null(parsed.Final);
    }

    [Fact]
    public void Runs_without_tools_hooks_mcp_or_saved_sessions()
    {
        var args = ClaudeAskService.BuildArguments().ToList();
        Assert.Contains("-p", args);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);           // 도구 없음 (파일·명령 실행 불가)
        Assert.Contains("--no-session-persistence", args);              // 대화 저장 안 함
        Assert.Contains("--strict-mcp-config", args);                   // MCP 서버 안 띄움
        Assert.Contains("disableAllHooks", args[args.IndexOf("--settings") + 1]); // NowBar Hook에 다시 잡히지 않게
        Assert.DoesNotContain(args, a => a.Contains("dangerously", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hotkey_is_described_for_people()
    {
        Assert.Equal("Ctrl+Alt+Space", HotkeyService.Describe(HotkeyService.MOD_CONTROL | HotkeyService.MOD_ALT, HotkeyService.VK_SPACE));
    }

    [Fact]
    public void Answer_markdown_becomes_readable_text()
    {
        string? flattened = null;
        var thread = new Thread(() =>
        {
            var inlines = MarkdownLite.ToInlines("**서울**의 날씨\n- 맑음\n```\ncode\n```\n끝");
            flattened = string.Concat(inlines.Select(i => i switch
            {
                LineBreak => "\n",
                Run r => r.Text,
                Span s => string.Concat(s.Inlines.OfType<Run>().Select(r => r.Text)),
                _ => "",
            }));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Equal("서울의 날씨\n• 맑음\ncode\n끝", flattened);
    }
}
