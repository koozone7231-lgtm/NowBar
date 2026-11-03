using System.Windows;
using System.Windows.Media;

namespace NowBar.App.Controls;

/// <summary>
/// 최근 값(0~100)을 잇는 작은 꺾은선 + 아래 옅은 채움. 값 배열이 바뀔 때만 다시 그린다.
/// 칸 수는 <see cref="Capacity"/>로 고정해 값이 적을 때는 오른쪽부터 채운다 (최신 값이 오른쪽 끝).
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public int Capacity { get; set; } = 60;

    public Sparkline()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values;
        var w = ActualWidth;
        var h = ActualHeight;
        if (values is not { Count: > 1 } || w <= 0 || h <= 0) return;

        var step = w / (Capacity - 1);
        var start = Capacity - Math.Min(values.Count, Capacity);
        var skip = values.Count - (Capacity - start);
        const double pad = 1.5;
        double Y(double v) => pad + (h - 2 * pad) * (1 - Math.Clamp(v, 0, 100) / 100);

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var l = line.Open())
        using (var f = fill.Open())
        {
            var x0 = start * step;
            l.BeginFigure(new Point(x0, Y(values[skip])), false, false);
            f.BeginFigure(new Point(x0, h), true, true);
            f.LineTo(new Point(x0, Y(values[skip])), false, false);
            for (var i = skip + 1; i < values.Count; i++)
            {
                var p = new Point((start + i - skip) * step, Y(values[i]));
                l.LineTo(p, true, true);
                f.LineTo(p, false, false);
            }
            f.LineTo(new Point(w, h), false, false);
        }
        line.Freeze();
        fill.Freeze();

        var area = Stroke.CloneCurrentValue();
        area.Opacity = 0.18;
        area.Freeze();
        dc.DrawGeometry(area, null, fill);
        dc.DrawGeometry(null, new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}
