using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NowBar.App.Helpers;
using Windows.Devices.Radios;

namespace NowBar.App.Services;

/// <summary>무선 장치 상태: 없음 / 꺼짐 / 켜짐.</summary>
public enum RadioToggle { Missing, Off, On }

/// <summary>
/// 빠른 설정에서 바꾸는 Windows 상태. 모두 관리자 권한 없이 동작한다.
/// - Wi-Fi·블루투스: Windows.Devices.Radios
/// - 다크 모드: HKCU 개인 설정 + 변경 알림 방송
/// - 밝기: 내장 디스플레이 WMI (외장 모니터는 지원하지 않음)
/// - 잠금: LockWorkStation
/// </summary>
public sealed class QuickSettingsService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private bool? _radioAccess;

    // ── Wi-Fi / 블루투스 ─────────────────────────────────
    public async Task<RadioToggle> GetRadioAsync(RadioKind kind)
    {
        var radio = await FindRadioAsync(kind).ConfigureAwait(false);
        return radio is null ? RadioToggle.Missing : radio.State == RadioState.On ? RadioToggle.On : RadioToggle.Off;
    }

    /// <summary>켜기/끄기. 성공하면 true (장치가 없거나 Windows가 거부하면 false).</summary>
    public async Task<bool> SetRadioAsync(RadioKind kind, bool on)
    {
        var radio = await FindRadioAsync(kind).ConfigureAwait(false);
        if (radio is null) return false;
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        return result == RadioAccessStatus.Allowed;
    }

    private async Task<Radio?> FindRadioAsync(RadioKind kind)
    {
        try
        {
            _radioAccess ??= await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
            if (_radioAccess != true) return null;
            var radios = await Radio.GetRadiosAsync();
            return radios.FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception ex)
        {
            Logger.Error($"무선 장치 확인 실패 ({kind})", ex);
            return null;
        }
    }

    // ── 다크 모드 ────────────────────────────────────────
    public bool IsDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    public void SetDarkMode(bool dark)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true))
        {
            var value = dark ? 0 : 1;
            key.SetValue("AppsUseLightTheme", value, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", value, RegistryValueKind.DWord);
        }
        // 열려 있는 앱·작업 표시줄에 테마가 바뀌었다고 알림
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 200, out _);
    }

    // ── 밝기 (내장 디스플레이) ───────────────────────────
    /// <summary>현재 밝기 0~100. 내장 디스플레이가 없거나 지원하지 않으면 null.</summary>
    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active=TRUE");
            foreach (ManagementObject obj in searcher.Get())
                using (obj) return Convert.ToInt32(obj["CurrentBrightness"]);
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // 데스크톱 외장 모니터 등 → 지원 안 함
        }
        return null;
    }

    public bool SetBrightness(int percent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=TRUE");
            foreach (ManagementObject obj in searcher.Get())
            {
                using (obj) obj.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)Math.Clamp(percent, 0, 100)]);
                return true;
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            Logger.Error("밝기 변경 실패", ex);
        }
        return false;
    }

    // ── 잠금 ─────────────────────────────────────────────
    public static bool Lock() => LockWorkStation();

    // ── Win32 ────────────────────────────────────────────
    private static readonly IntPtr HWND_BROADCAST = new(0xffff);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
