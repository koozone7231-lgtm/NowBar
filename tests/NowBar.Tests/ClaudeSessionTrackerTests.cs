using System.Text.Json;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class ClaudeSessionTrackerTests
{
    private static ClaudeHookEvent Evt(string name, string session = "s1", string? tool = null, string? prompt = null,
        string? notificationType = null, string? message = null, string? error = null, string? file = null) => new()
    {
        EventName = name,
        SessionId = session,
        Cwd = @"C:\work\NowBar",
        ToolName = tool,
        Prompt = prompt,
        NotificationType = notificationType,
        Message = message,
        Error = error,
        TargetFileName = file,
    };

    [Fact]
    public void Full_turn_moves_through_states()
    {
        var t = new ClaudeSessionTracker();

        var s = t.Apply(Evt("UserPromptSubmit", prompt: "WPF 인터페이스 구현"))!;
        Assert.Equal(ClaudeTaskState.Working, s.State);
        Assert.Equal("NowBar", s.ProjectName);
        Assert.NotNull(s.TaskStartedAt);

        t.Apply(Evt("PreToolUse", tool: "Edit", file: "MainWindow.xaml"));
        Assert.Equal(ClaudeTaskState.ToolRunning, s.State);
        Assert.Equal("파일 수정 중 · MainWindow.xaml", s.LastActivity);

        t.Apply(Evt("PostToolUse", tool: "Edit", file: "MainWindow.xaml"));
        Assert.Equal(ClaudeTaskState.Working, s.State);

        t.Apply(Evt("PermissionRequest", tool: "Bash"));
        Assert.Equal(ClaudeTaskState.Waiting, s.State);

        t.Apply(Evt("Stop"));
        Assert.Equal(ClaudeTaskState.Completed, s.State);
        Assert.NotNull(s.CompletedAt);

        // SessionEnd → IDLE로 표시, 보관 시간이 지나면 정리
        var now = DateTimeOffset.Now;
        t.Clock = () => now;
        t.Apply(Evt("SessionEnd"));
        Assert.Equal(ClaudeTaskState.Idle, s.State);
        Assert.Single(t.Sessions);
        t.Clock = () => now + ClaudeSessionTracker.EndedSessionRetention;
        Assert.Equal(1, t.PruneInactive(TimeSpan.FromHours(12)));
        Assert.Empty(t.Sessions);
    }

    [Fact]
    public void Session_end_for_unknown_session_is_ignored()
    {
        var t = new ClaudeSessionTracker();
        Assert.Null(t.Apply(Evt("SessionEnd", session: "never-seen")));
        Assert.Empty(t.Sessions);
    }

    [Fact]
    public void Prompt_is_not_kept_unless_summary_enabled()
    {
        var t = new ClaudeSessionTracker();
        var s = t.Apply(Evt("UserPromptSubmit", prompt: "비밀 프로젝트 내용"))!;
        Assert.Null(s.TaskSummary);

        t.ShowSummary = true;
        t.SummaryMaxLength = 10;
        s = t.Apply(Evt("UserPromptSubmit", prompt: "WPF 인터페이스를\n구현해 주세요 아주 길게"))!;
        Assert.Equal(10, s.TaskSummary!.Length);
        Assert.EndsWith("…", s.TaskSummary);
        Assert.DoesNotContain("\n", s.TaskSummary);
    }

    [Fact]
    public void Summary_redacts_secret_like_values()
    {
        var t = new ClaudeSessionTracker { ShowSummary = true, SummaryMaxLength = 120 };
        var s = t.Apply(Evt("UserPromptSubmit", prompt: "use api_key=abc123secret and sk-ant-0123456789abcdef"))!;
        Assert.DoesNotContain("abc123secret", s.TaskSummary);
        Assert.DoesNotContain("0123456789abcdef", s.TaskSummary);
    }

    [Fact]
    public void Stop_failure_sets_error()
    {
        var t = new ClaudeSessionTracker();
        t.Apply(Evt("UserPromptSubmit"));
        var s = t.Apply(Evt("StopFailure", error: "rate_limit"))!;
        Assert.Equal(ClaudeTaskState.Error, s.State);
        Assert.Equal("사용량 한도에 도달했습니다", s.ErrorText);
    }

    [Fact]
    public void Notifications_map_to_waiting_and_respect_setting()
    {
        var t = new ClaudeSessionTracker();
        t.Apply(Evt("UserPromptSubmit"));
        var s = t.Apply(Evt("Notification", notificationType: "permission_prompt", message: "Claude needs your permission"))!;
        Assert.Equal(ClaudeTaskState.Waiting, s.State);
        Assert.Equal("Claude needs your permission", s.LastNotification);

        t.Apply(Evt("Stop"));
        t.Apply(Evt("Notification", notificationType: "idle_prompt", message: "waiting"));
        Assert.Equal(ClaudeTaskState.Completed, s.State); // 완료 상태 유지

        t.ShowNotifications = false;
        t.Apply(Evt("UserPromptSubmit"));
        t.Apply(Evt("Notification", notificationType: "agent_needs_input", message: "hidden"));
        Assert.Null(s.LastNotification);
        Assert.Equal(ClaudeTaskState.Waiting, s.State);
    }

    [Fact]
    public void Sessions_are_separated_and_primary_prefers_waiting()
    {
        var t = new ClaudeSessionTracker();
        t.Apply(Evt("UserPromptSubmit", session: "a"));
        t.Apply(Evt("UserPromptSubmit", session: "b"));
        t.Apply(Evt("PermissionRequest", session: "a", tool: "Bash"));
        t.Apply(Evt("PreToolUse", session: "b", tool: "Read"));

        Assert.Equal(2, t.Sessions.Count);
        Assert.Equal("a", t.GetPrimary()!.SessionId);
    }

    [Fact]
    public void Connection_test_event_does_not_create_session()
    {
        var t = new ClaudeSessionTracker();
        Assert.Null(t.Apply(Evt(ClaudeHookEvent.ConnectionTestEventName)));
        Assert.Empty(t.Sessions);
    }

    [Fact]
    public void Manual_task_lifecycle()
    {
        var t = new ClaudeSessionTracker();
        var s = t.StartManual("문서 작성");
        Assert.Equal(ClaudeTaskState.Working, s.State);
        t.CompleteManual();
        Assert.Equal(ClaudeTaskState.Completed, s.State);
        t.RemoveManual();
        Assert.Empty(t.Sessions);
    }

    [Fact]
    public void Hook_event_parses_documented_fields_only()
    {
        const string json = """
            {
              "session_id": "abc123",
              "transcript_path": "/home/user/.claude/projects/x.jsonl",
              "cwd": "/home/user/my-project",
              "permission_mode": "default",
              "hook_event_name": "PreToolUse",
              "tool_name": "Write",
              "tool_input": { "file_path": "/path/to/file.txt", "content": "SECRET CONTENT" },
              "tool_use_id": "toolu_01"
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var e = ClaudeHookEvent.FromJson(doc.RootElement)!;
        Assert.Equal("PreToolUse", e.EventName);
        Assert.Equal("abc123", e.SessionId);
        Assert.Equal("Write", e.ToolName);
        Assert.Equal("file.txt", e.TargetFileName);
        Assert.Equal("my-project", ClaudeSessionTracker.ProjectNameFromCwd(e.Cwd!));
    }

    [Fact]
    public void Hook_event_without_name_is_rejected()
    {
        using var doc = JsonDocument.Parse("""{ "session_id": "x" }""");
        Assert.Null(ClaudeHookEvent.FromJson(doc.RootElement));
    }
}
