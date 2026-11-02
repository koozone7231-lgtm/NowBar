using System.Text.Json;

namespace NowBar.App.Models;

public enum ClaudeTaskState
{
    /// <summary>현재 진행 중인 작업이 없음</summary>
    Idle,
    /// <summary>Claude가 요청을 처리 중</summary>
    Working,
    /// <summary>도구 실행 또는 파일 작업 중</summary>
    ToolRunning,
    /// <summary>사용자의 승인이나 입력을 기다리는 중</summary>
    Waiting,
    /// <summary>응답 또는 작업이 완료됨</summary>
    Completed,
    /// <summary>작업 중 오류 발생</summary>
    Error,
}

/// <summary>
/// Claude Code Hook으로 수신한 이벤트에서 NowBar가 사용하는 필드만 추출한 것.
/// tool_input / tool_response / transcript_path / last_assistant_message 등은 읽지 않는다
/// (파일 도구의 대상 파일 이름만 예외).
/// </summary>
public sealed class ClaudeHookEvent
{
    public const string ConnectionTestEventName = "NowBarConnectionTest";

    public required string EventName { get; init; }
    public required string SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? ToolName { get; init; }
    /// <summary>UserPromptSubmit에만 존재. 저장하지 않고, 설정이 켜져 있을 때 짧은 요약 생성에만 사용한다.</summary>
    public string? Prompt { get; init; }
    public string? NotificationType { get; init; }
    public string? Message { get; init; }
    public string? Title { get; init; }
    /// <summary>StopFailure의 error 값 (rate_limit, server_error 등).</summary>
    public string? Error { get; init; }
    /// <summary>SessionEnd의 reason 값.</summary>
    public string? Reason { get; init; }
    /// <summary>파일 도구(Read/Edit/Write/NotebookEdit)의 대상 파일 이름(경로 제외).</summary>
    public string? TargetFileName { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;

    public static ClaudeHookEvent? FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var name = Str(root, "hook_event_name");
        if (string.IsNullOrWhiteSpace(name)) return null;

        string? fileName = null;
        if (root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            var path = Str(input, "file_path") ?? Str(input, "notebook_path");
            if (!string.IsNullOrWhiteSpace(path))
            {
                try { fileName = System.IO.Path.GetFileName(path.TrimEnd('/', '\\')); }
                catch (ArgumentException) { fileName = null; }
            }
        }

        return new ClaudeHookEvent
        {
            EventName = name,
            SessionId = Str(root, "session_id") is { Length: > 0 } sid ? sid : "unknown",
            Cwd = Str(root, "cwd"),
            ToolName = Str(root, "tool_name"),
            Prompt = Str(root, "prompt"),
            NotificationType = Str(root, "notification_type"),
            Message = Str(root, "message"),
            Title = Str(root, "title"),
            Error = Str(root, "error"),
            Reason = Str(root, "reason"),
            TargetFileName = fileName,
        };
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>session_id 별로 추적하는 Claude Code 세션 상태. 대화 내용은 보관하지 않는다.</summary>
public sealed class ClaudeSession
{
    public required string SessionId { get; init; }
    public bool IsManual { get; init; }
    public string? Cwd { get; set; }
    public string ProjectName { get; set; } = "알 수 없는 프로젝트";

    public ClaudeTaskState State { get; set; } = ClaudeTaskState.Idle;
    /// <summary>작업 요약(사용자가 동의한 경우에만, 길이 제한·민감정보 마스킹 후).</summary>
    public string? TaskSummary { get; set; }
    public DateTimeOffset? TaskStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>SessionEnd 수신 시각. 일정 시간 표시 후 목록에서 제거된다.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    public string LastActivity { get; set; } = "";
    public string? LastToolName { get; set; }
    public string? LastEventName { get; set; }
    public DateTimeOffset LastEventAt { get; set; }

    public string? LastNotification { get; set; }
    public DateTimeOffset? LastNotificationAt { get; set; }
    public string? ErrorText { get; set; }
}
