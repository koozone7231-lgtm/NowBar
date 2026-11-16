using System.Runtime.InteropServices;
using System.Windows.Interop;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>
/// 어디서나 누를 수 있는 전역 단축키 (기본 Ctrl+Alt+Space → 빠른 질문).
/// 화면에 보이지 않는 메시지 전용 창으로 Windows에 등록한다. 다른 프로그램이 이미 쓰고 있으면 등록에 실패한다.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int Id = 0x4E42; // 'NB'
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    private readonly HwndSource _window;
    private bool _registered;

    public event EventHandler? Pressed;

    public HotkeyService()
    {
        _window = new HwndSource(new HwndSourceParameters("NowBarHotkey")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);
    }

    /// <summary>"Ctrl+Alt+Space" 같은 표시용 이름.</summary>
    public static string Describe(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        parts.Add(key == VK_SPACE ? "Space" : ((char)key).ToString());
        return string.Join("+", parts);
    }

    /// <summary>등록. 성공하면 true (이미 다른 프로그램이 쓰는 조합이면 false).</summary>
    public bool Register(uint modifiers, uint key)
    {
        Unregister();
        _registered = RegisterHotKey(_window.Handle, Id, modifiers | MOD_NOREPEAT, key);
        if (_registered) Logger.Info($"단축키 등록: {Describe(modifiers, key)}");
        else Logger.Info($"단축키 등록 실패 (다른 프로그램이 사용 중일 수 있음): {Describe(modifiers, key)}");
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_window.Handle, Id);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
