using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>빠른 질문 한 번의 결과.</summary>
public sealed record AskResult(bool Ok, string Text, string? Error);

/// <summary>Claude Code 출력(stream-json) 한 줄을 읽은 결과.</summary>
public readonly record struct AskLine(string? Delta, string? Final, bool IsError);

/// <summary>
/// 단축키로 여는 빠른 질문: 사용자가 이미 로그인해 둔 Claude Code(`claude -p`)에 질문을 넘기고
/// 답을 받아 오는 대로 화면에 흘려 보낸다. NowBar는 API 키·인증 정보를 다루지 않는다.
/// - 도구 없음(--tools ""), Hook·MCP 서버 끔, 세션 저장 안 함, 빈 작업 폴더에서 실행
/// - 질문과 답은 메모리에만 두고 파일·로그에 남기지 않는다
/// </summary>
public sealed class ClaudeAskService
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private const string SystemPrompt =
        "You are answering a quick question typed into NowBar, a small desktop widget. " +
        "Reply in the user's language, short and direct: usually 1-5 sentences. " +
        "Use a short bullet list only when it truly helps; avoid tables, headings and long code blocks.";

    /// <summary>claude 실행 파일 (사용자 로컬 설치 → PATH 순). 없으면 null.</summary>
    public static string? FindClaude()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // PATH에 잘못된 항목
                }
            }
        }
        return null;
    }

    /// <summary>claude에 넘길 인수 (질문은 표준 입력으로 보내 따옴표·특수 문자 문제를 피한다).</summary>
    public static IReadOnlyList<string> BuildArguments() =>
    [
        "-p",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--tools", "",
        "--strict-mcp-config",
        "--no-session-persistence",
        "--effort", "low",
        "--settings", "{\"disableAllHooks\":true}",
        "--append-system-prompt", SystemPrompt,
    ];

    /// <summary>stream-json 한 줄 해석: 답 조각(text_delta), 최종 결과(result), 그 밖은 무시.</summary>
    public static AskLine ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{') return default;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type == "stream_event"
                && root.TryGetProperty("event", out var ev)
                && ev.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta"
                && delta.TryGetProperty("text", out var text))
                return new AskLine(text.GetString(), null, false);

            if (type == "result")
            {
                var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                var result = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return new AskLine(null, result ?? "", isError);
            }
        }
        catch (JsonException)
        {
            // JSON이 아닌 진단 줄
        }
        return default;
    }

    /// <summary>질문을 보내고, 답 조각이 올 때마다 onText(지금까지의 전체 답)를 부른다.</summary>
    public async Task<AskResult> AskAsync(string question, Action<string> onText, CancellationToken ct)
    {
        var claude = FindClaude();
        if (claude is null)
            return new AskResult(false, "", "Claude Code(claude)를 찾지 못했습니다. Claude Code를 설치하고 한 번 로그인해 주세요.");

        var workDir = Path.Combine(AppPaths.DataDir, "ask");
        Directory.CreateDirectory(workDir);

        var psi = new ProcessStartInfo
        {
            FileName = claude,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // npm으로 설치한 경우: cmd를 거쳐 실행
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(claude);
        }
        foreach (var arg in BuildArguments()) psi.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        var answer = new StringBuilder();
        string? final = null;
        var failed = false;
        var started = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("claude를 시작하지 못했습니다.");
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

            // 질문은 표준 입력으로 (UTF-8, BOM 없이)
            using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                await stdin.WriteAsync(question.AsMemory(), timeout.Token);

            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                var parsed = ParseLine(line);
                if (parsed.Delta is { Length: > 0 } piece)
                {
                    answer.Append(piece);
                    onText(answer.ToString());
                }
                else if (parsed.Final is not null)
                {
                    final = parsed.Final;
                    failed = parsed.IsError;
                }
            }
            await process.WaitForExitAsync(timeout.Token);

            var text = string.IsNullOrWhiteSpace(final) ? answer.ToString() : final!;
            Logger.Info($"빠른 질문 완료 ({started.Elapsed.TotalSeconds:0.0}초, 종료 코드 {process.ExitCode})");
            if (failed || (process.ExitCode != 0 && text.Length == 0))
            {
                var stderr = await stderrTask;
                var reason = text.Length > 0 ? text : FirstMeaningfulLine(stderr);
                return new AskResult(false, answer.ToString(), $"Claude Code가 답하지 못했습니다. {reason}".Trim());
            }
            return new AskResult(true, text.Trim(), null);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            var reason = ct.IsCancellationRequested ? "취소했습니다." : "응답이 너무 오래 걸려 멈췄습니다 (2분).";
            return new AskResult(false, answer.ToString(), reason);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Kill(process);
            Logger.Error("빠른 질문 실행 실패", ex);
            return new AskResult(false, answer.ToString(), "Claude Code를 실행하지 못했습니다.");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string FirstMeaningfulLine(string stderr) =>
        stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('[')) ?? "";

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 이미 끝남
        }
    }
}
