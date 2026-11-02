namespace NowBar.App.Helpers;

/// <summary>%AppData%\NowBar\nowbar.log 에 기록하는 단순 파일 로거 (1MB 초과 시 .old로 교체).</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1_000_000;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.DataDir);
                var path = AppPaths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로깅 실패가 앱을 종료시키면 안 된다.
        }
    }
}
