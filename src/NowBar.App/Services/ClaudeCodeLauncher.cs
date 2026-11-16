using System.Diagnostics;
using System.Text;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>
/// 빠른 질문을 Claude Code 대화형 작업창(터미널)에서 이어 가기.
/// 빠른 질문은 저장하지 않으므로, 질문과 답을 첫 메시지로 넣은 새 대화를 연다.
/// 글자는 base64로 넘겨 따옴표·한글·줄바꿈이 명령줄에서 깨지지 않게 한다 (파일에 쓰지 않는다).
/// </summary>
public static class ClaudeCodeLauncher
{
    /// <summary>Windows 명령줄은 약 32,000자까지 → 인코딩한 명령이 이보다 짧아야 한다.</summary>
    public const int MaxEncodedLength = 30000;

    public static string BuildPrompt(string question, string answer, int maxAnswerChars = int.MaxValue)
    {
        var trimmed = answer.Length > maxAnswerChars ? answer[..maxAnswerChars] + "\n…(생략)" : answer;
        return "NowBar 빠른 질문에서 이어서 진행합니다.\n\n" +
               $"[내 질문]\n{question.Trim()}\n\n" +
               $"[방금 받은 답]\n{trimmed.Trim()}\n\n" +
               "이 내용을 이어서 도와줘. 먼저 무엇을 더 하면 좋을지 짧게 물어봐 줘.";
    }

    /// <summary>PowerShell -EncodedCommand 용 스크립트: 질문을 base64에서 되살려 claude에 첫 메시지로 넘긴다.</summary>
    public static string BuildScript(string claudePath, string prompt)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt));
        var exe = claudePath.Replace("'", "''");
        return "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
               $"$p = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payload}')); " +
               $"& '{exe}' $p";
    }

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>명령줄 길이 안에 들어오도록 답을 줄여 가며 인코딩한 명령을 만든다.</summary>
    public static string BuildEncodedCommand(string claudePath, string question, string answer)
    {
        if (question.Length > 1500) question = question[..1500];
        var limit = answer.Length;
        while (true)
        {
            var encoded = Encode(BuildScript(claudePath, BuildPrompt(question, answer, limit)));
            if (encoded.Length <= MaxEncodedLength || limit == 0) return encoded;
            limit = Math.Max(0, (int)(limit * 0.7) - 50);
        }
    }

    /// <summary>Windows Terminal이 있으면 새 탭으로, 없으면 PowerShell 창으로 연다. 실패하면 이유를 돌려준다.</summary>
    public static string? Launch(string question, string answer)
    {
        var claude = ClaudeAskService.FindClaude();
        if (claude is null) return "Claude Code(claude)를 찾지 못했습니다.";

        var workDir = Path.Combine(AppPaths.DataDir, "ask");
        Directory.CreateDirectory(workDir);
        var encoded = BuildEncodedCommand(claude, question, answer);
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        try
        {
            var wt = FindWindowsTerminal();
            ProcessStartInfo psi;
            if (wt is not null)
            {
                psi = new ProcessStartInfo(wt) { UseShellExecute = false, WorkingDirectory = workDir };
                foreach (var a in new[] { "-w", "new", "new-tab", "--title", "Claude Code", "-d", workDir, shell, "-NoLogo", "-NoExit", "-EncodedCommand", encoded })
                    psi.ArgumentList.Add(a);
            }
            else
            {
                // 새 콘솔 창으로 (환경 변수를 고칠 수 있도록 셸 실행은 쓰지 않는다)
                psi = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = false, WorkingDirectory = workDir };
                foreach (var a in new[] { "-NoLogo", "-NoExit", "-EncodedCommand", encoded }) psi.ArgumentList.Add(a);
            }
            ClaudeAskService.RemoveSessionMarkers(psi);
            Process.Start(psi)?.Dispose();
            Logger.Info($"빠른 질문을 Claude Code 작업창에서 이어서 ({(wt is null ? "PowerShell" : "Windows Terminal")})");
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Logger.Error("Claude Code 작업창 열기 실패", ex);
            return "Claude Code 작업창을 열지 못했습니다.";
        }
    }

    private static string? FindWindowsTerminal()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "wt.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}
