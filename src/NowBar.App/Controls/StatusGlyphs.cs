using System.Windows;
using System.Windows.Media;
using NowBar.App.Models;

namespace NowBar.App.Controls;

/// <summary>배터리 아이콘: 남은 양만큼 채워지고, 충전 중이면 강조색, 부족하면 경고색.</summary>
public sealed class BatteryGlyph : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(int), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(100, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsChargingProperty = DependencyProperty.Register(
        nameof(IsCharging), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsLowProperty = DependencyProperty.Register(
        nameof(IsLow), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Level { get => (int)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool IsCharging { get => (bool)GetValue(IsChargingProperty); set => SetValue(IsChargingProperty, value); }
    public bool IsLow { get => (bool)GetValue(IsLowProperty); set => SetValue(IsLowProperty, value); }

    public BatteryGlyph()
    {
        Width = 22;
        Height = 12;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        var outline = Res("Brush.SubText", Brushes.Gray);
        var fill = IsLow ? Res("Brush.Warning", Brushes.Orange)
            : IsCharging ? Res("Brush.Accent", Brushes.LimeGreen)
            : Res("Brush.Text", Brushes.White);

        // 몸체 + 단자
        var body = new Rect(0.6, 0.6, 18.8, 10.8);
        dc.DrawRoundedRectangle(null, new Pen(outline, 1.2), body, 2.5, 2.5);
        dc.DrawRoundedRectangle(outline, null, new Rect(20, 3.8, 1.8, 4.4), 0.8, 0.8);

        // 남은 양
        var width = Math.Max(1.5, 15.2 * Math.Clamp(Level, 0, 100) / 100.0);
        dc.DrawRoundedRectangle(fill, null, new Rect(2.4, 2.4, width, 7.2), 1.2, 1.2);
    }

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}

/// <summary>네트워크 아이콘: Wi-Fi 신호 막대(3단계), 유선, 제한됨(경고색), 연결 없음(빗금).</summary>
public sealed class NetworkGlyph : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(NetworkKind), typeof(NetworkGlyph),
        new FrameworkPropertyMetadata(NetworkKind.Ethernet, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(int), typeof(NetworkGlyph),
        new FrameworkPropertyMetadata(5, FrameworkPropertyMetadataOptions.AffectsRender));

    public NetworkKind Kind { get => (NetworkKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    /// <summary>Windows 신호 막대 0~5.</summary>
    public int Bars { get => (int)GetValue(BarsProperty); set => SetValue(BarsProperty, value); }

    public NetworkGlyph()
    {
        Width = 16;
        Height = 14;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Width, Height);

    protected override void OnRender(DrawingContext dc)
    {
        var on = Res("Brush.Text", Brushes.White);
        var off = Res("Brush.Track", Brushes.DimGray);
        var warn = Res("Brush.Warning", Brushes.Orange);

        if (Kind == NetworkKind.Ethernet)
        {
            // 유선: 위 상자 하나에서 아래 두 상자로 연결
            var pen = Pen(on, 1.4);
            dc.DrawRoundedRectangle(null, pen, new Rect(5, 1, 6, 4.5), 1, 1);
            dc.DrawLine(pen, new Point(8, 5.5), new Point(8, 8));
            dc.DrawLine(pen, new Point(3.5, 8), new Point(12.5, 8));
            dc.DrawRoundedRectangle(null, pen, new Rect(1.2, 9, 4.6, 4), 1, 1);
            dc.DrawRoundedRectangle(null, pen, new Rect(10.2, 9, 4.6, 4), 1, 1);
            return;
        }

        // Wi-Fi(및 모바일) 부채꼴 3단계
        var lit = Kind switch
        {
            NetworkKind.Offline => 0,
            _ => Bars >= 4 ? 3 : Bars >= 2 ? 2 : 1,
        };
        var color = Kind == NetworkKind.Limited ? warn : on;
        var center = new Point(8, 12.5);
        for (var i = 1; i <= 3; i++)
        {
            var r = 3.2 * i + 0.4;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                var a1 = DegToPoint(center, r, 225);
                var a2 = DegToPoint(center, r, 315);
                ctx.BeginFigure(a1, false, false);
                ctx.ArcTo(a2, new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, Pen(i <= lit ? color : off, 1.6), g);
        }
        dc.DrawEllipse(lit > 0 ? color : off, null, center, 1.3, 1.3);

        if (Kind == NetworkKind.Offline)
            dc.DrawLine(Pen(warn, 1.6), new Point(2, 2), new Point(14, 13));
    }

    private static Point DegToPoint(Point c, double r, double deg)
    {
        var rad = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }

    private static Pen Pen(Brush brush, double thickness) =>
        new(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private Brush Res(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
