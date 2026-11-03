using System.Text.Json.Nodes;
using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class ClaudeHooksSetupServiceTests : IDisposable
{
    private const string Url = "http://127.0.0.1:17432/api/claude-hook";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nowbar-tests-" + Guid.NewGuid().ToString("N"));
    private string SettingsPath => Path.Combine(_dir, "settings.json");

    public ClaudeHooksSetupServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string ExistingSettings = """
        {
          "model": "opus",
          "permissions": { "allow": ["Bash(git status)"] },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "echo mine" } ] }
            ],
            "SessionStart": [
              { "hooks": [ { "type": "command", "command": "echo start" } ] }
            ]
          },
          "statusLine": { "type": "command", "command": "status.sh" }
        }
        """;

    [Fact]
    public void Install_preserves_existing_settings_and_hooks()
    {
        File.WriteAllText(SettingsPath, ExistingSettings);
        var svc = new ClaudeHooksSetupService(SettingsPath);

        var plan = svc.BuildInstallPlan(Url, "tok123");
        var backup = svc.Apply(plan);

        Assert.NotNull(backup);
        Assert.Equal(ExistingSettings, File.ReadAllText(backup!));

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal("Bash(git status)", (string?)root["permissions"]!["allow"]![0]);
        Assert.Equal("status.sh", (string?)root["statusLine"]!["command"]);

        // 사용자 기존 Hook 유지
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal("echo mine", (string?)pre[0]!["hooks"]![0]!["command"]);
        Assert.Equal("Bash", (string?)pre[0]!["matcher"]);
        Assert.Equal("echo start", (string?)root["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"]);

        // NowBar Hook은 별도 그룹으로 추가
        var ours = pre[1]!["hooks"]![0]!.AsObject();
        Assert.Equal("http", (string?)ours["type"]);
        Assert.Equal(Url, (string?)ours["url"]);
        Assert.Equal("Bearer tok123", (string?)ours["headers"]!["Authorization"]);

        foreach (var ev in ClaudeHooksSetupService.HookEvents)
            Assert.NotNull(root["hooks"]![ev]);

        var status = svc.GetStatus(Url, "tok123");
        Assert.True(status.Installed);
        Assert.True(status.TokenMatches);
    }

    [Fact]
    public void Install_is_idempotent_and_updates_token_in_place()
    {
        File.WriteAllText(SettingsPath, ExistingSettings);
        var svc = new ClaudeHooksSetupService(SettingsPath);
        svc.Apply(svc.BuildInstallPlan(Url, "first"));

        var again = svc.BuildInstallPlan(Url, "first");
        Assert.False(again.HasChanges);

        Assert.False(svc.GetStatus(Url, "second").TokenMatches);
        var update = svc.BuildInstallPlan(Url, "second");
        Assert.All(update.Changes, c => Assert.Contains("갱신", c));
        svc.Apply(update);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        var pre = root["hooks"]!["PreToolUse"]!.AsArray();
        Assert.Equal(2, pre.Count); // 중복 추가 없음
        Assert.Equal("Bearer second", (string?)pre[1]!["hooks"]![0]!["headers"]!["Authorization"]);
        Assert.True(svc.GetStatus(Url, "second").TokenMatches);
    }

    [Fact]
    public void Remove_deletes_only_nowbar_hooks()
    {
        File.WriteAllText(SettingsPath, ExistingSettings);
        var svc = new ClaudeHooksSetupService(SettingsPath);
        svc.Apply(svc.BuildInstallPlan(Url, "tok"));

        var remove = svc.BuildRemovePlan();
        Assert.True(remove.HasChanges);
        svc.Apply(remove);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        var hooks = root["hooks"]!.AsObject();
        Assert.Equal(["PreToolUse", "SessionStart"], hooks.Select(kv => kv.Key).ToArray());
        Assert.Single(hooks["PreToolUse"]!.AsArray());
        Assert.Equal("echo mine", (string?)hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]);
        Assert.False(svc.GetStatus(Url, "tok").Installed);
    }

    [Fact]
    public void Reconnect_only_fixes_previously_approved_nowbar_hooks()
    {
        File.WriteAllText(SettingsPath, ExistingSettings);
        var svc = new ClaudeHooksSetupService(SettingsPath);

        // 한 번도 연동하지 않음 → 자동으로 추가하지 않는다
        Assert.Null(svc.BuildReconnectPlan(Url, "tok-a"));

        svc.Apply(svc.BuildInstallPlan(Url, "tok-a"));
        // 이미 최신 → 할 일 없음
        Assert.Null(svc.BuildReconnectPlan(Url, "tok-a"));

        // 토큰·포트가 바뀜 → NowBar 항목만 현재 값으로
        var newUrl = "http://127.0.0.1:17500/api/claude-hook";
        var plan = svc.BuildReconnectPlan(newUrl, "tok-b");
        Assert.NotNull(plan);
        svc.Apply(plan!);
        var status = svc.GetStatus(newUrl, "tok-b");
        Assert.True(status.Installed && status.TokenMatches);

        var root = JsonNode.Parse(File.ReadAllText(SettingsPath))!.AsObject();
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal("echo mine", (string?)root["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]);
        Assert.Null(svc.BuildReconnectPlan(newUrl, "tok-b"));
    }

    [Fact]
    public void Install_creates_file_when_missing()
    {
        var svc = new ClaudeHooksSetupService(SettingsPath);
        var plan = svc.BuildInstallPlan(Url, "tok");
        Assert.Null(svc.Apply(plan)); // 백업할 원본 없음
        Assert.True(svc.GetStatus(Url, "tok").Installed);
    }

    [Fact]
    public void Apply_refuses_when_file_changed_after_plan()
    {
        File.WriteAllText(SettingsPath, ExistingSettings);
        var svc = new ClaudeHooksSetupService(SettingsPath);
        var plan = svc.BuildInstallPlan(Url, "tok");

        File.WriteAllText(SettingsPath, """{ "model": "sonnet" }""");

        Assert.Throws<InvalidOperationException>(() => svc.Apply(plan));
        Assert.Equal("""{ "model": "sonnet" }""", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Invalid_json_is_not_overwritten()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var svc = new ClaudeHooksSetupService(SettingsPath);
        Assert.Throws<InvalidDataException>(() => svc.BuildInstallPlan(Url, "tok"));
        Assert.Equal("{ not json", File.ReadAllText(SettingsPath));
    }

    [Fact]
    public void Preview_masks_token_and_warns_about_allowlist()
    {
        File.WriteAllText(SettingsPath, """{ "allowedHttpHookUrls": ["https://example.com/*"] }""");
        var svc = new ClaudeHooksSetupService(SettingsPath);
        var plan = svc.BuildInstallPlan(Url, "supersecrettoken");

        Assert.DoesNotContain("supersecrettoken", plan.PreviewText);
        Assert.Contains("••••oken", plan.PreviewText);
        Assert.Contains(plan.Warnings, w => w.Contains("allowedHttpHookUrls"));
    }
}
