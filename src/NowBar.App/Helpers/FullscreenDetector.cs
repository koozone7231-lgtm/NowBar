using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NowBar.App.Helpers;

/// <summary>
/// "전체 화면 앱 실행 중이면 숨기기"용 판단.
/// Windows의 사용자 알림 상태(SHQueryUserNotificationState)의 Busy는 다른 모니터의 전체 화면 앱,
/// 녹화·게임 오버레이·라이브 배경화면 같은 보이지 않는 전체 화면 창에도 켜져서, 그런 환경에서는
/// NowBar가 접히자마자 사라지는 문제가 있었다. 그래서 Busy는 쓰지 않고, 지금 쓰고 있는 창(전경 창)이
/// NowBar가 있는 모니터를 실제로 덮고 있는지 직접 확인한다.
/// </summary>
public static class FullscreenDetector
{
    /// <summary>바탕 화면·작업 표시줄 — 화면 전체 크기여도 전체 화면 앱이 아니다.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    public readonly record struct Result(bool IsFullscreen, string Reason);

    /// <summary>창 사각형이 모니터 전체를 덮는지 (테두리 오차 없이 모니터 이상).</summary>
    public static bool CoversMonitor(RECT window, RECT monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top
        && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    public static bool IsShellClass(string className) => ShellClasses.Contains(className);

    /// <param name="nowBarWindow">NowBar 창 (같은 모니터인지 비교하고, 자기 자신은 제외)</param>
    public static Result Check(IntPtr nowBarWindow)
    {
        // 발표 모드와 Direct3D 전용 전체 화면(게임)은 Windows가 확실히 알려 준다
        if (SHQueryUserNotificationState(out var state) == 0)
        {
            if (state == 4) return new(true, "발표 모드");
            if (state == 3) return new(true, "Direct3D 전체 화면");
        }

        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return new(false, "");
        var root = GetAncestor(fg, GA_ROOT);
        if (root != IntPtr.Zero) fg = root;
        if (fg == nowBarWindow || !IsWindowVisible(fg) || IsIconic(fg)) return new(false, "");

        var className = ClassName(fg);
        if (IsShellClass(className)) return new(false, "");

        // 클릭이 통과하는 투명 오버레이(녹화·게임 오버레이 등)는 화면을 가리는 앱이 아니다
        var ex = GetWindowLongPtr(fg, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TRANSPARENT) != 0 && (ex & WS_EX_LAYERED) != 0) return new(false, "");

        // 다른 가상 데스크톱에 있거나 숨겨진(cloaked) 창
        if (DwmGetWindowAttribute(fg, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return new(false, "");

        // NowBar가 있는 모니터를 덮을 때만 (다른 모니터의 전체 화면 영상은 상관없다)
        var monitor = MonitorFromWindow(nowBarWindow, MONITOR_DEFAULTTONEAREST);
        if (MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST) != monitor) return new(false, "");
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(fg, out var rect)) return new(false, "");
        if (!CoversMonitor(rect, info.rcMonitor)) return new(false, "");

        return new(true, $"{ProcessName(fg)} ({className})");
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return "?";
        }
    }

    // ── Win32 ────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int left, int top, int right, int bottom) => (Left, Top, Right, Bottom) = (left, top, right, bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_LAYERED = 0x00080000;
    private const uint GA_ROOT = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DWMWA_CLOAKED = 14;

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
