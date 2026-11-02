using NowBar.App.Helpers;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>
/// Hook 이벤트를 session_id 별 작업 상태로 변환한다 (UI와 분리된 순수 로직).
/// 진행률은 측정할 수 없으므로 퍼센트를 만들지 않고 상태/활동 텍스트만 유지한다.
/// </summary>
public sealed class ClaudeSessionTracker
{
    public const string ManualSessionId = "nowbar-manual";

    private readonly Dictionary<string, ClaudeSession> _sessions = new();

    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;
    public bool ShowSummary { get; set; }
    public int SummaryMaxLength { get; set; } = 40;
    public bool ShowNotifications { get; set; } = true;

    public IReadOnlyCollection<ClaudeSession> Sessions => _sessions.Values;

    public ClaudeSession? Get(string sessionId) => _sessions.GetValueOrDefault(sessionId);

    /// <summary>이벤트를 반영하고 갱신된 세션을 반환한다 (세션 종료 시 null).</summary>
    public ClaudeSession? Apply(ClaudeHookEvent e)
    {
        if (e.EventName == ClaudeHookEvent.ConnectionTestEventName) return null;

        var now = Clock();
        if (e.EventName == "SessionEnd")
        {
            // IDLE로 전환하고 잠시 표시한 뒤(EndedSessionRetention) 목록에서 정리
            if (!_sessions.TryGetValue(e.SessionId, out var ended)) return null;
            ended.State = ClaudeTaskState.Idle;
            ended.EndedAt = now;
            ended.LastEventName = e.EventName;
            ended.LastEventAt = now;
            ended.LastActivity = "세션 종료됨";
            return ended;
        }

        if (!_sessions.TryGetValue(e.SessionId, out var s))
        {
            s = new ClaudeSession { SessionId = e.SessionId };
            _sessions[e.SessionId] = s;
        }
        s.EndedAt = null;

        if (!string.IsNullOrWhiteSpace(e.Cwd))
        {
            s.Cwd = e.Cwd;
            s.ProjectName = ProjectNameFromCwd(e.Cwd);
        }
        s.LastEventName = e.EventName;
        s.LastEventAt = now;

        switch (e.EventName)
        {
            case "UserPromptSubmit":
                s.State = ClaudeTaskState.Working;
                s.TaskStartedAt = now;
                s.CompletedAt = null;
                s.ErrorText = null;
                s.LastNotification = null;
                s.TaskSummary = ShowSummary ? TextSanitizer.OneLine(e.Prompt, SummaryMaxLength) : null;
                s.LastActivity = "요청 처리 중";
                break;

            case "PreToolUse":
                s.TaskStartedAt ??= now;
                s.LastToolName = e.ToolName;
                if (e.ToolName is "AskUserQuestion")
                {
                    s.State = ClaudeTaskState.Waiting;
                    s.LastActivity = "질문에 대한 답변 대기 중";
                }
                else if (e.ToolName is "ExitPlanMode")
                {
                    s.State = ClaudeTaskState.Waiting;
                    s.LastActivity = "계획 승인 대기 중";
                }
                else
                {
                    s.State = ClaudeTaskState.ToolRunning;
                    s.LastActivity = DescribeTool(e.ToolName, e.TargetFileName, running: true);
                }
                break;

            case "PostToolUse":
                s.TaskStartedAt ??= now;
                s.State = ClaudeTaskState.Working;
                s.LastToolName = e.ToolName;
                s.LastActivity = DescribeTool(e.ToolName, e.TargetFileName, running: false);
                break;

            case "PostToolUseFailure":
                s.State = ClaudeTaskState.Working;
                s.LastToolName = e.ToolName;
                s.LastActivity = $"{ToolLabel(e.ToolName)} 실패 · 계속 진행 중";
                break;

            case "PermissionRequest":
                s.State = ClaudeTaskState.Waiting;
                s.LastActivity = $"승인 대기 · {ToolLabel(e.ToolName)}";
                break;

            case "Notification":
                ApplyNotification(s, e, now);
                break;

            case "Stop":
                s.State = ClaudeTaskState.Completed;
                s.CompletedAt = now;
                s.LastActivity = "응답 완료";
                break;

            case "StopFailure":
                s.State = ClaudeTaskState.Error;
                s.CompletedAt = now;
                s.ErrorText = DescribeError(e.Error);
                s.LastActivity = "오류로 중단됨";
                break;

            default:
                // 알 수 없는/추가 이벤트: 마지막 이벤트 시각만 갱신
                break;
        }

        return s;
    }

    private void ApplyNotification(ClaudeSession s, ClaudeHookEvent e, DateTimeOffset now)
    {
        if (ShowNotifications)
        {
            s.LastNotification = TextSanitizer.OneLine(e.Message ?? e.Title, 100);
            s.LastNotificationAt = now;
        }

        switch (e.NotificationType)
        {
            case "permission_prompt":
                s.State = ClaudeTaskState.Waiting;
                s.LastActivity = "권한 승인 필요";
                break;
            case "elicitation_dialog":
            case "elicitation_url_dialog":
            case "agent_needs_input":
                s.State = ClaudeTaskState.Waiting;
                s.LastActivity = "사용자 입력 필요";
                break;
            case "idle_prompt":
                // 응답을 마치고 다음 입력을 기다리는 상태. 완료/오류 상태는 유지한다.
                if (s.State is not (ClaudeTaskState.Completed or ClaudeTaskState.Error))
                    s.State = ClaudeTaskState.Waiting;
                s.LastActivity = "다음 입력 대기 중";
                break;
        }
    }

    public ClaudeSession? GetPrimary() =>
        _sessions.Values
            .OrderByDescending(s => Priority(s.State))
            .ThenByDescending(s => s.LastEventAt)
            .FirstOrDefault();

    public static int Priority(ClaudeTaskState state) => state switch
    {
        ClaudeTaskState.Waiting => 5,
        ClaudeTaskState.Error => 4,
        ClaudeTaskState.ToolRunning or ClaudeTaskState.Working => 3,
        ClaudeTaskState.Completed => 2,
        _ => 1,
    };

    /// <summary>Hook을 쓸 수 없는 환경을 위한 수동 작업 등록.</summary>
    public ClaudeSession StartManual(string? name)
    {
        var now = Clock();
        var s = new ClaudeSession
        {
            SessionId = ManualSessionId,
            IsManual = true,
            ProjectName = "수동 작업",
            State = ClaudeTaskState.Working,
            TaskStartedAt = now,
            TaskSummary = TextSanitizer.OneLine(name, 60) ?? "이름 없는 작업",
            LastActivity = "수동으로 등록한 작업 진행 중",
            LastEventName = "Manual",
            LastEventAt = now,
        };
        _sessions[ManualSessionId] = s;
        return s;
    }

    public void CompleteManual()
    {
        if (!_sessions.TryGetValue(ManualSessionId, out var s)) return;
        var now = Clock();
        s.State = ClaudeTaskState.Completed;
        s.CompletedAt = now;
        s.LastEventAt = now;
        s.LastActivity = "수동 작업 완료";
    }

    public void RemoveManual() => _sessions.Remove(ManualSessionId);

    public void ClearSummaries()
    {
        foreach (var s in _sessions.Values.Where(s => !s.IsManual)) s.TaskSummary = null;
    }

    public void ClearNotifications()
    {
        foreach (var s in _sessions.Values) { s.LastNotification = null; s.LastNotificationAt = null; }
    }

    public static readonly TimeSpan EndedSessionRetention = TimeSpan.FromMinutes(5);

    /// <summary>
    /// 종료된 세션(EndedSessionRetention 경과)과, SessionEnd를 받지 못한 채 오래된 세션(예: 터미널 강제 종료)을 정리한다.
    /// </summary>
    public int PruneInactive(TimeSpan maxAge)
    {
        var now = Clock();
        var stale = _sessions.Values
            .Where(s => !s.IsManual && (s.LastEventAt < now - maxAge || (s.EndedAt is { } end && now - end >= EndedSessionRetention)))
            .Select(s => s.SessionId)
            .ToList();
        foreach (var id in stale) _sessions.Remove(id);
        return stale.Count;
    }

    public static string ProjectNameFromCwd(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var idx = trimmed.LastIndexOfAny(['\\', '/']);
        var name = idx >= 0 ? trimmed[(idx + 1)..] : trimmed;
        return string.IsNullOrWhiteSpace(name) ? cwd : name;
    }

    public static string ToolLabel(string? tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return "도구";
        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__");
            return parts.Length >= 3 ? $"MCP {parts[1]}" : "MCP 도구";
        }
        return tool;
    }

    public static string DescribeTool(string? tool, string? fileName, bool running)
    {
        string text = tool switch
        {
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => running ? "파일 수정 중" : "파일 수정 완료",
            "Read" => running ? "파일 읽는 중" : "파일 읽기 완료",
            "Bash" or "PowerShell" => running ? "명령 실행 중" : "명령 실행 완료",
            "Grep" or "Glob" or "LS" => running ? "코드 검색 중" : "코드 검색 완료",
            "WebFetch" or "WebSearch" => running ? "웹 정보 확인 중" : "웹 정보 확인 완료",
            "Agent" or "Task" => running ? "하위 에이전트 실행 중" : "하위 에이전트 완료",
            "TodoWrite" => running ? "작업 목록 정리 중" : "작업 목록 정리 완료",
            _ => running ? $"{ToolLabel(tool)} 실행 중" : $"{ToolLabel(tool)} 완료",
        };
        return string.IsNullOrWhiteSpace(fileName) ? text : $"{text} · {fileName}";
    }

    public static string DescribeError(string? error) => error switch
    {
        "rate_limit" => "사용량 한도에 도달했습니다",
        "overloaded" => "서버가 혼잡합니다",
        "authentication_failed" => "인증에 실패했습니다",
        "oauth_org_not_allowed" => "조직 권한이 없습니다",
        "account_on_hold" => "계정이 보류 상태입니다",
        "billing_error" => "결제 관련 오류",
        "invalid_request" => "잘못된 요청",
        "model_not_found" => "모델을 찾을 수 없습니다",
        "server_error" => "서버 오류",
        "max_output_tokens" => "출력 길이 한도 초과",
        "cloud_credential_error" => "클라우드 자격 증명 오류",
        null or "" => "알 수 없는 오류",
        _ => $"오류: {error}",
    };
}
