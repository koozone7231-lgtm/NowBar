namespace NowBar.App.Helpers;

public static class AppPaths
{
    /// <summary>%AppData%\NowBar. 테스트에서는 NOWBAR_DATA_DIR 환경 변수로 바꿀 수 있다.</summary>
    public static string DataDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("NOWBAR_DATA_DIR");
            return string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NowBar")
                : overrideDir;
        }
    }

    /// <summary>이전 이름(TOPBAR)으로 실행하던 시절의 데이터 폴더.</summary>
    public static string LegacyDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Topbar");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string WeatherCacheFile => Path.Combine(DataDir, "weather-cache.json");
    public static string LogFile => Path.Combine(DataDir, "nowbar.log");

    /// <summary>
    /// 최초 실행 시 이전 TOPBAR 데이터(설정·인증 토큰·날씨 캐시)를 새 폴더로 복사한다.
    /// 원본은 지우지 않는다. 이미 새 설정이 있으면 아무것도 하지 않는다.
    /// </summary>
    public static bool MigrateLegacyData()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOWBAR_DATA_DIR"))) return false;
        if (File.Exists(SettingsFile) || !Directory.Exists(LegacyDataDir)) return false;

        Directory.CreateDirectory(DataDir);
        var copied = false;
        foreach (var name in new[] { "settings.json", "weather-cache.json" })
        {
            var source = Path.Combine(LegacyDataDir, name);
            if (!File.Exists(source)) continue;
            File.Copy(source, Path.Combine(DataDir, name), overwrite: false);
            copied = true;
        }
        return copied;
    }
}
