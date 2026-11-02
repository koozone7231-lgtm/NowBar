using Microsoft.Win32;

namespace NowBar.App.Services;

/// <summary>HKCU\...\Run 레지스트리 값으로 Windows 시작 시 자동 실행을 등록/해제한다 (관리자 권한 불필요).</summary>
public sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _valueName;

    public StartupService(string valueName = "NowBar")
    {
        _valueName = valueName;
    }

    public static string CurrentExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NowBar.exe");

    public static string BuildCommand(string exePath) => $"\"{exePath}\" --startup";

    public string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    public bool IsEnabled() => !string.IsNullOrWhiteSpace(GetRegisteredCommand());

    /// <summary>이전 이름(TOPBAR)으로 등록된 자동 실행 항목이 있으면 지우고, 등록되어 있었는지 반환한다.</summary>
    public static bool RemoveLegacyEntry(string legacyValueName = "TOPBAR")
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(legacyValueName) is not string) return false;
        key.DeleteValue(legacyValueName, throwOnMissingValue: false);
        return true;
    }

    public void SetEnabled(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                        ?? throw new InvalidOperationException("시작 프로그램 레지스트리 키를 열 수 없습니다.");
        if (enabled)
            key.SetValue(_valueName, BuildCommand(exePath ?? CurrentExePath), RegistryValueKind.String);
        else
            key.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
