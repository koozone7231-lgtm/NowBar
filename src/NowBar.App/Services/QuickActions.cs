using System.Diagnostics;
using System.Text.RegularExpressions;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

public enum QuickActionKind { OpenLink, LaunchApp }

/// <summary>빠른 질문 답 끝에 Claude가 붙인 실행 요청 한 줄 (@open 주소 / @app 앱 이름).</summary>
public sealed record QuickAction(QuickActionKind Kind, string Target)
{
    public string Label => Kind == QuickActionKind.OpenLink ? ShortUrl(Target) : Target;

    private static string ShortUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            return uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        return url;
    }
}

/// <summary>
/// "앱 실행·링크 열기" 요청을 답에서 골라내 안전한 것만 실행한다.
/// - 링크: http/https, ms-settings:(Windows 설정), mailto: 만
/// - 앱: 이 PC 시작 메뉴에 있는 앱 이름, 또는 경로·인수 없는 실행 파일 이름(chrome, notepad 등).
///   명령 프롬프트·PowerShell·스크립트 실행기처럼 임의 명령을 돌릴 수 있는 것은 실행하지 않는다.
/// </summary>
public static partial class QuickActions
{
    [GeneratedRegex(@"^\s*@(?<kind>open|app)\s+(?<target>.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ActionLine();

    [GeneratedRegex(@"^[\p{L}\p{N}._\- ]{1,60}$")]
    private static partial Regex PlainName();

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "reg", "regedit",
        "wmic", "bash", "wsl", "msiexec", "schtasks", "certutil", "bitsadmin", "conhost", "wt", "windowsterminal",
        "explorer", "taskkill", "net", "sc", "format", "diskpart", "bcdedit", "shutdown",
    };

    /// <summary>답에서 실행 요청 줄을 떼어 내고, 보여 줄 글과 요청 목록을 돌려준다.</summary>
    public static (string Text, IReadOnlyList<QuickAction> Actions) Extract(string answer)
    {
        var actions = new List<QuickAction>();
        var kept = new List<string>();
        foreach (var line in answer.Replace("\r\n", "\n").Split('\n'))
        {
            var m = ActionLine().Match(line);
            if (!m.Success)
            {
                kept.Add(line);
                continue;
            }
            var target = m.Groups["target"].Value.Trim().Trim('`', '"', '\'', '<', '>');
            var kind = m.Groups["kind"].Value.Equals("open", StringComparison.OrdinalIgnoreCase)
                ? QuickActionKind.OpenLink : QuickActionKind.LaunchApp;
            if (target.Length > 0 && actions.Count < 3) actions.Add(new QuickAction(kind, target));
        }
        return (string.Join('\n', kept).Trim(), actions);
    }

    /// <summary>답이 아직 쓰이는 중일 때: 실행 요청이 될 줄("@…")은 미리 감춘다.</summary>
    public static string HidePending(string partial) =>
        string.Join('\n', partial.Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith('@'))).TrimEnd();

    public static bool IsAllowedLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri)
        && (uri.Scheme is "http" or "https" or "mailto" || uri.Scheme == "ms-settings");

    /// <summary>경로·인수 없이 이름만 있고, 임의 명령을 돌릴 수 있는 실행기가 아닌지.</summary>
    public static bool IsSafeAppName(string name)
    {
        var trimmed = name.Trim();
        if (!PlainName().IsMatch(trimmed)) return false;
        var stem = trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
        return !Blocked.Contains(stem.Replace(" ", ""));
    }

    /// <summary>시작 메뉴 앱 목록에서 이름이 가장 잘 맞는 앱 (정확히 → 앞부분 → 포함).</summary>
    public static AppEntry? MatchApp(string name, IEnumerable<AppEntry> apps)
    {
        static string Norm(string s) => s.Replace(" ", "").ToLowerInvariant();
        var wanted = Norm(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        if (wanted.Length == 0) return null;
        var list = apps.Where(a => !Blocked.Contains(Norm(a.Name))).ToList();
        static bool HasWord(AppEntry a, string w) => a.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part => Norm(part) == w);
        return list.FirstOrDefault(a => Norm(a.Name) == wanted)
            // "chrome" → "Google Chrome" (이름 속 낱말과 정확히 같음)이 "Chrome Remote Desktop"보다 먼저
            ?? list.Where(a => HasWord(a, wanted)).OrderBy(a => a.Name.Length).FirstOrDefault()
            ?? list.Where(a => Norm(a.Name).StartsWith(wanted)).OrderBy(a => a.Name.Length).FirstOrDefault()
            ?? (wanted.Length >= 2 ? list.Where(a => Norm(a.Name).Contains(wanted)).OrderBy(a => a.Name.Length).FirstOrDefault() : null);
    }

    /// <summary>실행. 성공하면 null, 아니면 사람이 읽을 이유.</summary>
    public static async Task<string?> RunAsync(QuickAction action)
    {
        try
        {
            if (action.Kind == QuickActionKind.OpenLink)
            {
                if (!IsAllowedLink(action.Target)) return "열 수 없는 주소입니다";
                Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute = true })?.Dispose();
                Logger.Info("빠른 질문: 링크 열기");
                return null;
            }

            if (!IsSafeAppName(action.Target)) return "실행할 수 없는 이름입니다";
            var app = MatchApp(action.Target, await AppCatalog.GetAsync());
            if (app is not null)
            {
                // 시작 메뉴 앱(일반 앱·스토어 앱 모두)은 shell:AppsFolder로 실행한다
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.Id}") { UseShellExecute = true })?.Dispose();
                Logger.Info($"빠른 질문: 앱 실행 ({app.Name})");
                return null;
            }
            // 시작 메뉴에 없으면 실행 파일 이름으로 (App Paths 등록 앱: chrome, msedge, notepad ...)
            var exe = action.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? action.Target : action.Target + ".exe";
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
            Logger.Info($"빠른 질문: 앱 실행 ({exe})");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "찾지 못했어요";
        }
    }
}

/// <summary>시작 메뉴의 앱 하나 (이름 + shell:AppsFolder 아이디).</summary>
public sealed record AppEntry(string Name, string Id);

/// <summary>이 PC에 설치된 앱 목록 (shell:AppsFolder). 처음 한 번 읽고 10분간 재사용한다.</summary>
public static class AppCatalog
{
    private static IReadOnlyList<AppEntry>? _cache;
    private static DateTime _loadedAt;

    public static Task<IReadOnlyList<AppEntry>> GetAsync()
    {
        if (_cache is not null && DateTime.Now - _loadedAt < TimeSpan.FromMinutes(10)) return Task.FromResult(_cache);
        var tcs = new TaskCompletionSource<IReadOnlyList<AppEntry>>();
        // Shell COM은 STA 스레드에서
        var thread = new Thread(() =>
        {
            try
            {
                _cache = Load();
                _loadedAt = DateTime.Now;
                tcs.SetResult(_cache);
            }
            catch (Exception ex)
            {
                Logger.Error("앱 목록 읽기 실패", ex);
                tcs.SetResult([]);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static IReadOnlyList<AppEntry> Load()
    {
        var type = Type.GetTypeFromProgID("Shell.Application");
        if (type is null) return [];
        dynamic shell = Activator.CreateInstance(type)!;
        var list = new List<AppEntry>();
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        foreach (dynamic item in folder.Items())
        {
            string name = item.Name;
            string id = item.Path;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id)) list.Add(new AppEntry(name, id));
        }
        return list;
    }
}
