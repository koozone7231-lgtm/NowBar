using System.Windows;
using System.Windows.Media;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>
/// 설정(테마·배경 투명도·강조색)에 맞춰 앱 전역 브러시 리소스를 교체한다. 모든 화면은 DynamicResource로 참조한다.
/// Spotify 영감 디자인: UI는 무채색(근검정 표면 + 흰/은색 글자), 색은 강조색 하나와 의미 색(경고·오류·안내)만 사용한다.
/// </summary>
public static class ThemeService
{
    public const string DefaultAccent = "#1ED760";

    public static readonly string[] AccentPresets =
        [DefaultAccent, "#539DF5", "#FFA42B", "#F3727F", "#BCA5FF", "#FFFFFF"];

    public static void Apply(AppSettings s)
    {
        var res = Application.Current.Resources;
        var dark = s.Theme == AppTheme.Dark;

        var bg = Hex(dark ? "#121212" : "#F6F6F6");
        var alpha = (byte)Math.Round(Math.Clamp(s.BackgroundOpacity, 0.5, 1.0) * 255);
        var text = Hex(dark ? "#FFFFFF" : "#121212");

        Set(res, "Brush.Island", Color.FromArgb(alpha, bg.R, bg.G, bg.B));
        Set(res, "Brush.Window", bg);
        Set(res, "Brush.Card", Hex(dark ? "#181818" : "#FFFFFF"));
        Set(res, "Brush.Surface", Hex(dark ? "#1F1F1F" : "#EEEEEE"));
        Set(res, "Brush.Selected", Hex(dark ? "#2A2A2A" : "#E2E2E2"));
        Set(res, "Brush.Border", Hex(dark ? "#2A2A2A" : "#DDDDDD"));
        Set(res, "Brush.Outline", Hex(dark ? "#7C7C7C" : "#8A8A8A"));
        Set(res, "Brush.Divider", Hex(dark ? "#2A2A2A" : "#E2E2E2"));
        Set(res, "Brush.Text", text);
        Set(res, "Brush.SubText", Hex(dark ? "#B3B3B3" : "#6A6A6A"));
        // 탭 아이콘은 무채색 — 색은 앨범 이미지와 강조색에서만 나온다
        Set(res, "Brush.Weather", text);
        Set(res, "Brush.Music", text);
        Set(res, "Brush.AI", text);
        Set(res, "Brush.Hover", Hex(dark ? "#12FFFFFF" : "#0D000000"));
        Set(res, "Brush.Pressed", Hex(dark ? "#22FFFFFF" : "#1A000000"));
        Set(res, "Brush.Track", Hex(dark ? "#4D4D4D" : "#CBCBCB"));
        Set(res, "Brush.Warning", Hex(dark ? "#FFA42B" : "#C76B00"));
        Set(res, "Brush.Danger", Hex(dark ? "#F3727F" : "#D2283A"));
        Set(res, "Brush.Info", Hex(dark ? "#539DF5" : "#1F6FD1"));

        var accent = TryHex(s.AccentColor) ?? Hex(DefaultAccent);
        Set(res, "Brush.Accent", accent);
        Set(res, "Brush.Success", accent);
        // 강조색 위 글자는 밝기에 따라 검정/흰색 선택
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        Set(res, "Brush.OnAccent", luminance > 0.55 ? Hex("#000000") : Hex("#FFFFFF"));
    }

    public static Color? TryHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim()); }
        catch (FormatException) { return null; }
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static void Set(ResourceDictionary res, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        res[key] = brush;
    }
}
