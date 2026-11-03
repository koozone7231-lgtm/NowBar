using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NowBar.App.Controls;

/// <summary>축소된 캡슐에 표시하는 얼굴의 표정.</summary>
public enum FaceMood
{
    /// <summary>평소: 기본 눈, 가끔 깜빡이고 두리번거림</summary>
    Idle,
    /// <summary>Claude 작업 중: 가늘게 뜬 눈이 좌우로 훑음 (읽는 중)</summary>
    Working,
    /// <summary>사용자 응답 필요: 위를 올려다보는 경고색 눈</summary>
    Waiting,
    /// <summary>음악 감상: 웃는 눈(◠ ◠)이 박자에 맞춰 까딱이고 음표가 떠오름</summary>
    Music,
    /// <summary>방금 작업 완료: 웃는 눈</summary>
    Happy,
    /// <summary>오류: ✕ ✕ 눈</summary>
    Error,
    /// <summary>배터리 부족: 반쯤 감긴 지친 눈 (경고색)</summary>
    LowBattery,
    /// <summary>인터넷 끊김: 작은 점 눈이 두리번거림</summary>
    Offline,
    /// <summary>과부하(CPU·메모리): 질끈 감은 &gt; &lt; 눈이 떨리고 식은땀이 흐름</summary>
    Strained,
}

/// <summary>
/// 눈만 있는 얼굴. 하나의 요소가 직접 그리며(OnRender), 애니메이션은 눈 크기·시선·까딱임 같은 숫자 속성만 바꾼다.
/// 투명 창은 프레임마다 전체를 다시 그리므로 연속 애니메이션은 낮은 프레임(20fps)으로 돌리고,
/// 화면에 보이지 않을 때(패널이 펼쳐졌거나 창이 숨겨졌을 때)는 모두 멈춘다.
/// </summary>
public sealed class FaceView : FrameworkElement
{
    private const int LoopFps = 20;
    private static readonly Random Rng = new();

    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(FaceMood), typeof(FaceView),
        new FrameworkPropertyMetadata(FaceMood.Idle, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((FaceView)d).RestartAnimations(entering: true)));

    // 애니메이션 대상 속성 (모두 다시 그리기 유발)
    private static readonly DependencyProperty OpennessProperty = Register(nameof(Openness), 1.0);
    private static readonly DependencyProperty GazeXProperty = Register(nameof(GazeX), 0.0);
    private static readonly DependencyProperty GazeYProperty = Register(nameof(GazeY), 0.0);
    private static readonly DependencyProperty BobProperty = Register(nameof(Bob), 0.0);
    private static readonly DependencyProperty NoteProgressProperty = Register(nameof(NoteProgress), 0.0);

    private static DependencyProperty Register(string name, double value) =>
        DependencyProperty.Register(name, typeof(double), typeof(FaceView),
            new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsRender));

    public FaceMood Mood
    {
        get => (FaceMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    private double Openness => (double)GetValue(OpennessProperty);
    private double GazeX => (double)GetValue(GazeXProperty);
    private double GazeY => (double)GetValue(GazeYProperty);
    private double Bob => (double)GetValue(BobProperty);
    private double NoteProgress => (double)GetValue(NoteProgressProperty);

    private readonly DispatcherTimer _blinkTimer;

    public FaceView()
    {
        Width = 72;
        Height = 32;
        IsHitTestVisible = false;
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background);
        _blinkTimer.Tick += (_, _) => OnBlinkTick();
        IsVisibleChanged += (_, _) => RestartAnimations(entering: false);
    }

    protected override Size MeasureOverride(Size availableSize) => new(Width, Height);

    // ── 애니메이션 ───────────────────────────────────────
    private void RestartAnimations(bool entering)
    {
        StopAll();
        if (!IsVisible) return; // 보이지 않으면 아무것도 돌리지 않는다 (CPU 0)

        switch (Mood)
        {
            case FaceMood.Idle:
                ScheduleBlink();
                break;

            case FaceMood.Working:
                // 읽는 듯이 좌우로 천천히 훑기
                Loop(GazeXProperty, -4, 4, 1400);
                ScheduleBlink();
                break;

            case FaceMood.Waiting:
                SetValue(GazeYProperty, -3.0);
                ScheduleBlink();
                break;

            case FaceMood.Music:
                // 박자에 맞춰 까딱 (약 120bpm) + 음표가 떠오르기
                Loop(BobProperty, 0, -3, 250);
                var note = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1800)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(note, LoopFps);
                BeginAnimation(NoteProgressProperty, note);
                break;

            case FaceMood.Happy:
                if (entering) Once(BobProperty, -3, 0, 260); // 기뻐서 한 번 통통
                break;

            case FaceMood.Error:
                break;

            case FaceMood.LowBattery:
                ScheduleBlink();
                break;

            case FaceMood.Offline:
                // 어디 갔지? 하듯 천천히 좌우를 살핌
                Loop(GazeXProperty, -4, 4, 1800);
                break;

            case FaceMood.Strained:
                // 힘겨워 부들부들 + 땀방울이 흘러내림
                Loop(GazeXProperty, -0.8, 0.8, 100);
                var sweat = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(1600)) { RepeatBehavior = RepeatBehavior.Forever };
                Timeline.SetDesiredFrameRate(sweat, LoopFps);
                BeginAnimation(NoteProgressProperty, sweat);
                break;
        }
    }

    private void StopAll()
    {
        _blinkTimer.Stop();
        foreach (var dp in new[] { OpennessProperty, GazeXProperty, GazeYProperty, BobProperty, NoteProgressProperty })
        {
            BeginAnimation(dp, null);
            ClearValue(dp);
        }
    }

    private void Loop(DependencyProperty dp, double from, double to, int halfPeriodMs)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(halfPeriodMs))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(anim, LoopFps);
        BeginAnimation(dp, anim);
    }

    private void Once(DependencyProperty dp, double from, double to, int ms)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(anim, 30);
        BeginAnimation(dp, anim);
    }

    private void ScheduleBlink()
    {
        _blinkTimer.Interval = TimeSpan.FromMilliseconds(Rng.Next(2500, 6000));
        _blinkTimer.Start();
    }

    private void OnBlinkTick()
    {
        _blinkTimer.Stop();
        if (!IsVisible || Mood is not (FaceMood.Idle or FaceMood.Working or FaceMood.Waiting or FaceMood.LowBattery)) return;

        // 눈 깜빡임
        var blink = new DoubleAnimation(1, 0.08, TimeSpan.FromMilliseconds(80))
        {
            AutoReverse = true,
            FillBehavior = FillBehavior.Stop,
        };
        Timeline.SetDesiredFrameRate(blink, 30);
        BeginAnimation(OpennessProperty, blink);

        // 평소에는 가끔 다른 곳을 힐끗 본다
        if (Mood == FaceMood.Idle && Rng.NextDouble() < 0.35)
        {
            var target = Rng.Next(-3, 4);
            var glance = new DoubleAnimation(target, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Timeline.SetDesiredFrameRate(glance, 30);
            BeginAnimation(GazeXProperty, glance);
        }

        ScheduleBlink();
    }

    // ── 그리기 ───────────────────────────────────────────
    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var eyeBrush = ResBrush("Brush.Text", Brushes.White);
        var cy = h / 2 + GazeY + Bob;
        var spacing = 15.0; // 얼굴 중심에서 각 눈까지
        var leftX = w / 2 - spacing + GazeX;
        var rightX = w / 2 + spacing + GazeX;

        switch (Mood)
        {
            case FaceMood.Music:
                DrawHappyEyes(dc, eyeBrush, leftX, rightX, cy);
                DrawNote(dc, rightX + 14, cy);
                break;

            case FaceMood.Happy:
                DrawHappyEyes(dc, eyeBrush, leftX, rightX, cy);
                break;

            case FaceMood.Error:
                var danger = ResBrush("Brush.Danger", Brushes.IndianRed);
                DrawCross(dc, danger, leftX, cy);
                DrawCross(dc, danger, rightX, cy);
                break;

            case FaceMood.Waiting:
                var warning = ResBrush("Brush.Warning", Brushes.Orange);
                DrawOpenEye(dc, warning, leftX, cy, 20);
                DrawOpenEye(dc, warning, rightX, cy, 20);
                break;

            case FaceMood.Working:
                DrawOpenEye(dc, eyeBrush, leftX, cy, 11);
                DrawOpenEye(dc, eyeBrush, rightX, cy, 11);
                break;

            case FaceMood.LowBattery:
                // 지쳐서 반쯤 감긴 눈: 아래쪽 절반만 보이고 위는 평평한 눈꺼풀
                var tired = ResBrush("Brush.Warning", Brushes.Orange);
                DrawTiredEye(dc, tired, leftX, cy + 3);
                DrawTiredEye(dc, tired, rightX, cy + 3);
                break;

            case FaceMood.Offline:
                var dim = ResBrush("Brush.SubText", Brushes.Gray);
                dc.DrawEllipse(dim, null, new Point(leftX, cy), 3.5 * Openness, 3.5 * Openness);
                dc.DrawEllipse(dim, null, new Point(rightX, cy), 3.5 * Openness, 3.5 * Openness);
                break;

            case FaceMood.Strained:
                DrawSquint(dc, eyeBrush, leftX, cy, pointRight: true);
                DrawSquint(dc, eyeBrush, rightX, cy, pointRight: false);
                DrawSweat(dc, rightX + 13, cy);
                break;

            default:
                DrawOpenEye(dc, eyeBrush, leftX, cy, 17);
                DrawOpenEye(dc, eyeBrush, rightX, cy, 17);
                break;
        }
    }

    /// <summary>세로로 긴 둥근 눈. 깜빡일 때는 높이만 줄어든다.</summary>
    private void DrawOpenEye(DrawingContext dc, Brush brush, double cx, double cy, double fullHeight)
    {
        const double eyeWidth = 9;
        var height = Math.Max(2, fullHeight * Openness);
        var radius = Math.Min(eyeWidth, height) / 2;
        dc.DrawRoundedRectangle(brush, null, new Rect(cx - eyeWidth / 2, cy - height / 2, eyeWidth, height), radius, radius);
    }

    /// <summary>반쯤 감긴 눈 — 아래가 둥글고 위가 평평한 반원.</summary>
    private void DrawTiredEye(DrawingContext dc, Brush brush, double cx, double cy)
    {
        var h = Math.Max(1.5, 7 * Openness);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx - 5, cy - h / 2), true, true);
            ctx.LineTo(new Point(cx + 5, cy - h / 2), false, false);
            ctx.ArcTo(new Point(cx - 5, cy - h / 2), new Size(5, h), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        dc.DrawGeometry(brush, null, g);
    }

    /// <summary>웃는 눈 ◠ — 위로 볼록한 곡선.</summary>
    private static void DrawHappyEyes(DrawingContext dc, Brush brush, double leftX, double rightX, double cy)
    {
        var pen = RoundPen(brush, 3.4);
        foreach (var cx in new[] { leftX, rightX })
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(cx - 7, cy + 3), false, false);
                ctx.QuadraticBezierTo(new Point(cx, cy - 7), new Point(cx + 7, cy + 3), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
    }

    private static void DrawCross(DrawingContext dc, Brush brush, double cx, double cy)
    {
        var pen = RoundPen(brush, 3);
        const double r = 5.5;
        dc.DrawLine(pen, new Point(cx - r, cy - r), new Point(cx + r, cy + r));
        dc.DrawLine(pen, new Point(cx - r, cy + r), new Point(cx + r, cy - r));
    }

    /// <summary>질끈 감은 눈: 왼쪽은 &gt;, 오른쪽은 &lt;.</summary>
    private static void DrawSquint(DrawingContext dc, Brush brush, double cx, double cy, bool pointRight)
    {
        var dir = pointRight ? 1 : -1;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(cx - 5 * dir, cy - 6), false, false);
            ctx.LineTo(new Point(cx + 5 * dir, cy), true, true);
            ctx.LineTo(new Point(cx - 5 * dir, cy + 6), true, true);
        }
        g.Freeze();
        dc.DrawGeometry(null, RoundPen(brush, 3.2), g);
    }

    /// <summary>오른쪽 눈 옆에서 흘러내리는 땀방울 (정보색).</summary>
    private void DrawSweat(DrawingContext dc, double x, double cy)
    {
        var p = NoteProgress;
        var opacity = p < 0.75 ? 1 : (1 - p) / 0.25;
        var y = cy - 8 + p * 10;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            // 위가 뾰족한 물방울
            ctx.BeginFigure(new Point(x, y - 4.5), true, true);
            ctx.QuadraticBezierTo(new Point(x + 3.2, y), new Point(x + 2.6, y + 1.6), true, true);
            ctx.ArcTo(new Point(x - 2.6, y + 1.6), new Size(2.8, 2.8), 0, true, SweepDirection.Clockwise, true, true);
            ctx.QuadraticBezierTo(new Point(x - 3.2, y), new Point(x, y - 4.5), true, true);
        }
        g.Freeze();
        dc.PushOpacity(opacity);
        dc.DrawGeometry(ResBrush("Brush.Info", Brushes.SkyBlue), null, g);
        dc.Pop();
    }

    /// <summary>오른쪽 눈 옆에서 떠오르며 사라지는 작은 음표 (강조색).</summary>
    private void DrawNote(DrawingContext dc, double x, double cy)
    {
        if (TryFindResource("Icon.Music") is not Geometry note) return;
        var p = NoteProgress;
        var opacity = Math.Sin(p * Math.PI); // 0 → 1 → 0
        if (opacity <= 0.02) return;

        const double size = 11;
        var scale = size / 24.0;
        var pen = RoundPen(ResBrush("Brush.Accent", Brushes.LimeGreen), 2.4 / scale * 0.5);
        dc.PushOpacity(opacity);
        dc.PushTransform(new TranslateTransform(x, cy - 2 - p * 9 - size / 2));
        dc.PushTransform(new ScaleTransform(scale, scale));
        dc.DrawGeometry(null, pen, note);
        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private static Pen RoundPen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    private Brush ResBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
