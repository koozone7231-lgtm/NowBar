using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

public sealed record ClaudeEnvironmentInfo(
    string ConfigDir,
    string SettingsPath,
    bool ConfigDirExists,
    bool SettingsExists,
    string? CliPath);

public sealed record HooksStatus(
    bool Installed,
    bool TokenMatches,
    IReadOnlyList<string> MissingEvents,
    string? Error)
{
    public static HooksStatus Failed(string error) => new(false, false, [], error);
}

/// <summary>settings.json 변경 계획. 사용자 승인 후 <see cref="ClaudeHooksSetupService.Apply"/>로 적용한다.</summary>
public sealed class HooksPlan
{
    public required string SettingsPath { get; init; }
    public string? OriginalText { get; init; }
    public string NewText { get; set; } = "";
    /// <summary>승인 창에 보여줄 hooks 섹션 (토큰은 가림).</summary>
    public string PreviewText { get; set; } = "";
    public List<string> Changes { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasChanges => Changes.Count > 0;
}

/// <summary>
/// Claude Code 사용자 설정(~/.claude/settings.json)에 NowBar HTTP Hook을 병합/제거한다.
/// - 기존 설정과 다른 Hook은 그대로 유지하고, NowBar 항목(127.0.0.1:{port}/api/claude-hook)만 추가·갱신·제거한다.
/// - 쓰기 전에 원본을 타임스탬프 백업으로 복사한다.
/// - 계획 생성 이후 파일이 바뀌었으면 적용을 거부한다.
/// </summary>
public sealed class ClaudeHooksSetupService
{
    /// <summary>
    /// NowBar가 구독하는 이벤트. SessionStart/Setup은 HTTP Hook을 지원하지 않으므로 제외한다
    /// (세션은 첫 이벤트 수신 시 생성됨).
    /// </summary>
    public static readonly string[] HookEvents =
    [
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PostToolUseFailure",
        "PermissionRequest",
        "Notification",
        "Stop",
        "StopFailure",
        "SessionEnd",
    ];

    /// <summary>Hook 요청 제한 시간(초). NowBar가 꺼져 있으면 연결 거부로 즉시 실패하며 Claude 동작은 막지 않는다.</summary>
    public const int HookTimeoutSeconds = 3;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions LenientParse = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string SettingsPath { get; }

    public ClaudeHooksSetupService(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(DefaultConfigDir, "settings.json");
    }

    public static string DefaultConfigDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(env)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : env;
        }
    }

    // ── 1. 설치/설정 위치 확인 ───────────────────────────
    public ClaudeEnvironmentInfo Detect()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        return new ClaudeEnvironmentInfo(dir, SettingsPath, Directory.Exists(dir), File.Exists(SettingsPath), FindCli());
    }

    public static string? FindCli()
    {
        var names = new[] { "claude.exe", "claude.cmd", "claude" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var dir in dirs)
        {
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    // ── 2. 상태 확인 ─────────────────────────────────────
    public HooksStatus GetStatus(string url, string token)
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new HooksStatus(false, false, HookEvents, null);

            var root = ParseRoot(File.ReadAllText(SettingsPath), out _);
            var hooks = root["hooks"] as JsonObject;
            var missing = new List<string>();
            var tokenOk = true;
            foreach (var ev in HookEvents)
            {
                var handler = (hooks?[ev] as JsonArray)?
                    .OfType<JsonObject>()
                    .SelectMany(g => (g["hooks"] as JsonArray)?.OfType<JsonObject>() ?? [])
                    .FirstOrDefault(IsNowBarHandler);
                if (handler is null) { missing.Add(ev); continue; }
                if (!HandlerMatches(handler, url, token)) tokenOk = false;
            }
            return new HooksStatus(missing.Count == 0, missing.Count < HookEvents.Length && tokenOk, missing, null);
        }
        catch (Exception ex)
        {
            return HooksStatus.Failed(ex.Message);
        }
    }

    // ── 3~5. 병합 계획 ───────────────────────────────────
    public HooksPlan BuildInstallPlan(string url, string token)
    {
        var original = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        var root = ParseRoot(original, out var lenientOnly);
        var plan = new HooksPlan { SettingsPath = SettingsPath, OriginalText = original };
        if (lenientOnly)
            plan.Warnings.Add("settings.json의 주석/후행 쉼표는 저장 시 제거됩니다 (원본은 백업됩니다).");

        if (root["hooks"] is { } existing && existing is not JsonObject)
            throw new InvalidDataException("settings.json의 \"hooks\" 항목이 객체 형식이 아니어서 안전하게 병합할 수 없습니다.");

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var ev in HookEvents)
        {
            var node = hooks[ev];
            if (node is not null && node is not JsonArray)
            {
                plan.Warnings.Add($"{ev}: 알 수 없는 형식이라 건드리지 않았습니다.");
                continue;
            }
            var groups = node as JsonArray;
            if (groups is null)
            {
                groups = new JsonArray();
                hooks[ev] = groups;
            }

            var found = false;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray handlers) continue;
                var removedAny = false;
                foreach (var h in handlers.OfType<JsonObject>().ToList())
                {
                    if (!IsNowBarHandler(h)) continue;
                    if (!found)
                    {
                        found = true;
                        if (!HandlerMatches(h, url, token))
                        {
                            UpdateHandler(h, url, token);
                            plan.Changes.Add($"{ev}: 기존 NowBar Hook 갱신 (URL/토큰)");
                        }
                    }
                    else
                    {
                        handlers.Remove(h);
                        removedAny = true;
                        plan.Changes.Add($"{ev}: 중복된 NowBar Hook 제거");
                    }
                }
                if (removedAny && handlers.Count == 0) groups.Remove(group);
            }

            if (!found)
            {
                groups.Add(new JsonObject { ["hooks"] = new JsonArray(CreateHandler(url, token)) });
                plan.Changes.Add($"{ev}: NowBar Hook 추가");
            }
        }

        AddEnvironmentWarnings(root, url, plan);
        plan.NewText = Serialize(root);
        plan.PreviewText = BuildPreview(root);
        return plan;
    }

    /// <summary>
    /// 시작할 때마다 하는 자동 재연결용 계획.
    /// 사용자가 이전에 연동을 승인해 NowBar Hook이 하나라도 있을 때만, NowBar 항목의 URL·토큰을 현재 값으로 맞춘다.
    /// 아직 연동한 적이 없거나, 이미 최신이거나, 주석 제거처럼 NowBar 항목 밖이 바뀌는 경우는 null (수동 연동으로).
    /// </summary>
    public HooksPlan? BuildReconnectPlan(string url, string token)
    {
        var status = GetStatus(url, token);
        if (status.Error is not null || status.MissingEvents.Count == HookEvents.Length) return null;
        if (status.Installed && status.TokenMatches) return null;

        var plan = BuildInstallPlan(url, token);
        if (!plan.HasChanges || plan.Warnings.Any(w => w.Contains("주석"))) return null;
        return plan;
    }

    public HooksPlan BuildRemovePlan()
    {
        var original = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        var plan = new HooksPlan { SettingsPath = SettingsPath, OriginalText = original };
        if (original is null)
        {
            plan.NewText = "";
            return plan;
        }

        var root = ParseRoot(original, out var lenientOnly);
        if (lenientOnly)
            plan.Warnings.Add("settings.json의 주석/후행 쉼표는 저장 시 제거됩니다 (원본은 백업됩니다).");

        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var (ev, node) in hooks.ToList())
            {
                if (node is not JsonArray groups) continue;
                var removedInEvent = 0;
                foreach (var group in groups.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is not JsonArray handlers) continue;
                    var removed = 0;
                    foreach (var h in handlers.OfType<JsonObject>().Where(IsNowBarHandler).ToList())
                    {
                        handlers.Remove(h);
                        removed++;
                    }
                    if (removed > 0 && handlers.Count == 0) groups.Remove(group);
                    removedInEvent += removed;
                }
                if (removedInEvent > 0)
                {
                    plan.Changes.Add($"{ev}: NowBar Hook 제거");
                    if (groups.Count == 0) hooks.Remove(ev);
                }
            }
            if (plan.HasChanges && hooks.Count == 0) root.Remove("hooks");
        }

        plan.NewText = Serialize(root);
        plan.PreviewText = root["hooks"] is JsonObject h2 ? BuildPreview(root) : "(hooks 항목 없음)";
        return plan;
    }

    // ── 4~6. 백업 후 저장 ────────────────────────────────
    /// <returns>백업 파일 경로 (원본이 없었으면 null)</returns>
    public string? Apply(HooksPlan plan)
    {
        if (plan.SettingsPath != SettingsPath)
            throw new InvalidOperationException("다른 설정 파일에 대한 계획입니다.");

        var current = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : null;
        if (!string.Equals(current, plan.OriginalText, StringComparison.Ordinal))
            throw new InvalidOperationException("변경 내용을 확인하는 동안 settings.json이 수정되었습니다. 다시 시도하세요.");

        if (!plan.HasChanges) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

        string? backup = null;
        if (current is not null)
        {
            var stamp = $"{SettingsPath}.nowbar-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
            backup = stamp;
            for (var i = 2; File.Exists(backup); i++) backup = $"{stamp}-{i}"; // 기존 백업은 절대 덮어쓰지 않음
            File.Copy(SettingsPath, backup, overwrite: false);
        }

        var tmp = SettingsPath + ".nowbar-tmp";
        File.WriteAllText(tmp, plan.NewText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(tmp, SettingsPath, overwrite: true);
        Logger.Info($"Claude Code 설정 변경 적용: {SettingsPath} (백업: {backup ?? "없음"})");
        return backup;
    }

    /// <summary>자동 설정을 쓸 수 없을 때 사용자가 직접 붙여넣을 수 있는 hooks JSON.</summary>
    public static string BuildManualSnippet(string url, string token)
    {
        var hooks = new JsonObject();
        foreach (var ev in HookEvents)
            hooks[ev] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(CreateHandler(url, token)) });
        return Serialize(new JsonObject { ["hooks"] = hooks });
    }

    // ── 내부 도우미 ──────────────────────────────────────
    public static bool IsNowBarHandler(JsonObject handler)
    {
        if (handler["type"]?.GetValueKind() != JsonValueKind.String || (string?)handler["type"] != "http") return false;
        if (handler["url"]?.GetValueKind() != JsonValueKind.String) return false;
        if (!Uri.TryCreate((string?)handler["url"], UriKind.Absolute, out var uri)) return false;
        return (uri.Host is "127.0.0.1" or "localhost")
               && string.Equals(uri.AbsolutePath, ClaudeHookServer.HookPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HandlerMatches(JsonObject h, string url, string token) =>
        (string?)h["url"] == url
        && h["headers"] is JsonObject headers
        && headers["Authorization"]?.GetValueKind() == JsonValueKind.String
        && (string?)headers["Authorization"] == "Bearer " + token
        && h["timeout"]?.GetValueKind() == JsonValueKind.Number;

    private static JsonObject CreateHandler(string url, string token) => new()
    {
        ["type"] = "http",
        ["url"] = url,
        ["timeout"] = HookTimeoutSeconds,
        ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + token },
    };

    private static void UpdateHandler(JsonObject h, string url, string token)
    {
        h["url"] = url;
        h["timeout"] = HookTimeoutSeconds;
        if (h["headers"] is not JsonObject headers)
        {
            headers = new JsonObject();
            h["headers"] = headers;
        }
        headers["Authorization"] = "Bearer " + token;
    }

    private static void AddEnvironmentWarnings(JsonObject root, string url, HooksPlan plan)
    {
        if (root["disableAllHooks"]?.GetValueKind() == JsonValueKind.True)
            plan.Warnings.Add("\"disableAllHooks\": true 로 설정되어 있어 Hook이 실행되지 않습니다.");

        if (root["allowedHttpHookUrls"] is JsonArray allowed)
        {
            var listed = allowed.Any(n => n?.GetValueKind() == JsonValueKind.String
                                          && ((string?)n)!.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase));
            if (!listed)
                plan.Warnings.Add($"\"allowedHttpHookUrls\" 허용 목록이 있습니다. {url} 이 허용되지 않으면 Claude Code가 NowBar Hook을 실행하지 않습니다.");
        }
    }

    private static JsonObject ParseRoot(string? text, out bool lenientOnly)
    {
        lenientOnly = false;
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            try
            {
                node = JsonNode.Parse(text, documentOptions: LenientParse);
                lenientOnly = true;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"settings.json이 올바른 JSON이 아닙니다: {ex.Message}");
            }
        }
        return node as JsonObject ?? throw new InvalidDataException("settings.json의 최상위가 JSON 객체가 아닙니다.");
    }

    private static string Serialize(JsonNode node) => node.ToJsonString(WriteOptions) + Environment.NewLine;

    private static string BuildPreview(JsonObject root)
    {
        var hooksCopy = root["hooks"]?.DeepClone() as JsonObject ?? new JsonObject();
        foreach (var (_, node) in hooksCopy)
        {
            if (node is not JsonArray groups) continue;
            foreach (var h in groups.OfType<JsonObject>()
                         .SelectMany(g => (g["hooks"] as JsonArray)?.OfType<JsonObject>() ?? [])
                         .Where(IsNowBarHandler))
            {
                if (h["headers"] is JsonObject headers && headers["Authorization"]?.GetValueKind() == JsonValueKind.String)
                {
                    var value = (string?)headers["Authorization"] ?? "";
                    headers["Authorization"] = "Bearer " + TokenGenerator.Mask(value.Replace("Bearer ", ""));
                }
            }
        }
        return Serialize(new JsonObject { ["hooks"] = hooksCopy });
    }
}
