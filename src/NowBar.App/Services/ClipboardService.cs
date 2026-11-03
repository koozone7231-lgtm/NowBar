using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NowBar.App.Helpers;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>
/// 클립보드 변경 감지 (AddClipboardFormatListener → WM_CLIPBOARDUPDATE).
/// 내용은 메모리에만 두고 파일·네트워크로 내보내지 않는다.
/// 비밀번호 관리자 등이 "기록하지 말 것"으로 표시한 내용과 비밀값처럼 보이는 텍스트는 건너뛴다.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int MaxTextLength = 200_000;
    private const int MaxImagePixels = 4096 * 4096;

    // 다른 앱이 "클립보드 기록·모니터에서 제외"를 요청할 때 함께 넣는 형식들
    private static readonly string[] ExcludeFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "Clipboard Viewer Ignore",
    ];

    private readonly HwndSource _listener;
    private readonly DispatcherTimer _debounce;
    private bool _listening;

    public event EventHandler<ClipboardEntry>? Captured;

    /// <summary>일시 정지 중에는 변경을 무시한다.</summary>
    public bool Paused { get; set; }

    public ClipboardService()
    {
        // 화면에 보이지 않는 메시지 전용 창으로 알림을 받는다
        _listener = new HwndSource(new HwndSourceParameters("NowBarClipboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            WindowStyle = 0,
        });
        _listener.AddHook(WndProc);

        // 한 번 복사에 알림이 여러 번 오기도 하고, 복사한 앱이 아직 클립보드를 쥐고 있을 수 있어 잠깐 기다렸다 읽는다
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Capture(); };
    }

    public void Start()
    {
        if (_listening) return;
        _listening = AddClipboardFormatListener(_listener.Handle);
        if (!_listening) Logger.Info("클립보드 감지를 시작하지 못했습니다.");
    }

    public void Stop()
    {
        if (!_listening) return;
        RemoveClipboardFormatListener(_listener.Handle);
        _listening = false;
        _debounce.Stop();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && !Paused)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private void Capture()
    {
        try
        {
            if (Retry(Read) is { } entry) Captured?.Invoke(this, entry);
        }
        catch (Exception ex)
        {
            Logger.Error("클립보드 읽기 실패", ex);
        }
    }

    private static ClipboardEntry? Read()
    {
        var data = Clipboard.GetDataObject();
        if (data is null || ShouldSkip(data)) return null;

        if (data.GetDataPresent(DataFormats.FileDrop) && Clipboard.GetFileDropList() is { Count: > 0 } files)
        {
            return new ClipboardEntry
            {
                Kind = ClipboardKind.Files,
                Files = files,
                Key = "files:" + string.Join('|', files.Cast<string>()),
            };
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength || ClipboardHistory.LooksSecret(text)) return null;
            return new ClipboardEntry
            {
                Kind = ClipboardHistory.IsLink(text) ? ClipboardKind.Link : ClipboardKind.Text,
                Text = text,
                Key = "text:" + text,
            };
        }

        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image
            && (long)image.PixelWidth * image.PixelHeight <= MaxImagePixels)
        {
            var frozen = ToBgra(image);
            return new ClipboardEntry
            {
                Kind = ClipboardKind.Image,
                Image = frozen,
                Thumbnail = MakeThumbnail(frozen, 56),
                Key = "image:" + HashPixels(frozen),
            };
        }

        return null;
    }

    /// <summary>"기록하지 말 것" 표시가 있거나 Windows 클립보드 기록에서 제외하도록 한 내용인지.</summary>
    private static bool ShouldSkip(IDataObject data)
    {
        foreach (var format in ExcludeFormats)
        {
            if (data.GetDataPresent(format)) return true;
        }
        // CanIncludeInClipboardHistory = 0 → 이 앱은 기록되기를 원하지 않는다
        if (data.GetDataPresent("CanIncludeInClipboardHistory")
            && data.GetData("CanIncludeInClipboardHistory") is MemoryStream { Length: >= 4 } ms)
        {
            var bytes = ms.ToArray();
            if (BitConverter.ToInt32(bytes, 0) == 0) return true;
        }
        return false;
    }

    // ── 다시 복사 / 붙여넣기 ─────────────────────────────
    /// <summary>기록 항목을 다시 클립보드에 넣는다.</summary>
    public bool SetClipboard(ClipboardEntry entry)
    {
        try
        {
            Retry<object?>(() =>
            {
                switch (entry.Kind)
                {
                    case ClipboardKind.Image when entry.Image is { } img: Clipboard.SetImage(img); break;
                    case ClipboardKind.Files when entry.Files is { } files: Clipboard.SetFileDropList(files); break;
                    default: Clipboard.SetText(entry.Text ?? ""); break;
                }
                return null;
            });
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("클립보드 쓰기 실패", ex);
            return false;
        }
    }

    /// <summary>대상 창을 앞으로 가져와 Ctrl+V를 보낸다.</summary>
    public static async Task PasteIntoAsync(IntPtr target)
    {
        if (target == IntPtr.Zero || !IsWindow(target)) return;
        NativeMethods.SetForegroundWindow(target);
        await Task.Delay(90); // 포커스가 넘어간 뒤에 키 입력
        SendCtrlV();
    }

    /// <summary>NowBar가 아닌, 지금 사용자가 쓰고 있는 창 (붙여넣기 대상).</summary>
    public static IntPtr GetForeignForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId ? IntPtr.Zero : hwnd;
    }

    private static void SendCtrlV()
    {
        const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_SHIFT = 0x10;
        const uint KEYUP = 0x0002;
        var inputs = new[]
        {
            Key(VK_SHIFT, KEYUP), // Shift+클릭으로 불렸으므로 Shift가 눌린 채면 Ctrl+Shift+V가 된다
            Key(VK_CONTROL, 0), Key(VK_V, 0), Key(VK_V, KEYUP), Key(VK_CONTROL, KEYUP),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

        static INPUT Key(ushort vk, uint flags) => new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
    }

    // ── 이미지 ───────────────────────────────────────────
    private static BitmapSource ToBgra(BitmapSource source)
    {
        BitmapSource bmp = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        // 원본은 클립보드 메모리를 가리킬 수 있어 독립된 사본으로 만든다
        var copy = new WriteableBitmap(bmp);
        copy.Freeze();
        return copy;
    }

    private static BitmapSource MakeThumbnail(BitmapSource source, int maxSize)
    {
        var scale = Math.Min(1.0, (double)maxSize / Math.Max(source.PixelWidth, source.PixelHeight));
        var thumb = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var copy = new WriteableBitmap(thumb);
        copy.Freeze();
        return copy;
    }

    private static string HashPixels(BitmapSource bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);
        return $"{bmp.PixelWidth}x{bmp.PixelHeight}:{Convert.ToHexString(SHA256.HashData(pixels))}";
    }

    /// <summary>다른 앱이 클립보드를 잡고 있으면 COMException이 난다 → 잠깐 쉬고 다시.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (COMException) when (attempt < 4) { Thread.Sleep(40); }
            catch (ExternalException) when (attempt < 4) { Thread.Sleep(40); }
        }
    }

    public void Dispose()
    {
        Stop();
        _listener.RemoveHook(WndProc);
        _listener.Dispose();
    }

    // ── Win32 ────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // MOUSEINPUT이 가장 커서 union 크기를 맞추려면 함께 선언해야 한다
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }
}
