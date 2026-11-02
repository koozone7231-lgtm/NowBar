using System.Windows;
using System.Windows.Media;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>설정(테마·배경 투명도·강조색)에 맞춰 앱 전역 브러시 리소스를 교체한다. 모든 화면은 DynamicResource로 참조한다.</summary>
public static class ThemeService
{
    public static readonly string[] AccentPresets =
        ["#9EB4FF", "#BCA5FF", "#F4C66A", "#6FD39A", "#FF9E9E", "#7FD6E8", "#F2F3F7"];

    public static void Apply(AppSettings s)
    {
        var res = Application.Current.Resources;
        var dark = s.Theme == AppTheme.Dark;

        var bg = Hex(dark ? "#15171C" : "#F5F6F9");
        var alpha = (byte)Math.Round(Math.Clamp(s.BackgroundOpacity, 0.5, 1.0) * 255);

        Set(res, "Brush.Island", Color.FromArgb(alpha, bg.R, bg.G, bg.B));
        Set(res, "Brush.Window", bg);
        Set(res, "Brush.Card", Hex(dark ? "#22252D" : "#FFFFFF"));
        Set(res, "Brush.Selected", Hex(dark ? "#2C303A" : "#E4E7EE"));
        Set(res, "Brush.Border", Hex(dark ? "#363A45" : "#D5D9E2"));
        Set(res, "Brush.Divider", Hex(dark ? "#3A3E4A" : "#D9DCE3"));
        Set(res, "Brush.Text", Hex(dark ? "#F2F3F7" : "#15171C"));
        Set(res, "Brush.SubText", Hex(dark ? "#969BA8" : "#5E6472"));
        Set(res, "Brush.Weather", Hex(dark ? "#F4C66A" : "#B7791F"));
        Set(res, "Brush.Music", Hex(dark ? "#9EB4FF" : "#3D5BD9"));
        Set(res, "Brush.AI", Hex(dark ? "#BCA5FF" : "#7457D9"));
        Set(res, "Brush.Hover", Hex(dark ? "#14FFFFFF" : "#0F000000"));
        Set(res, "Brush.Pressed", Hex(dark ? "#24FFFFFF" : "#1C000000"));
        Set(res, "Brush.Track", Hex(dark ? "#3A3E4A" : "#D5D9E2"));
        Set(res, "Brush.Success", Hex(dark ? "#6FD39A" : "#1F8A4C"));
        Set(res, "Brush.Warning", Hex(dark ? "#F4C66A" : "#B7791F"));
        Set(res, "Brush.Danger", Hex(dark ? "#FF8A8A" : "#C93C3C"));

        var accent = TryHex(s.AccentColor) ?? Hex("#9EB4FF");
        Set(res, "Brush.Accent", accent);
        // 강조색 위 글자는 밝기에 따라 검정/흰색 선택
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        Set(res, "Brush.OnAccent", luminance > 0.6 ? Hex("#15171C") : Hex("#FFFFFF"));
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
