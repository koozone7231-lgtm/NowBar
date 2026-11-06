using System.Windows;
using System.Windows.Media.Animation;

namespace NowBar.App.Helpers;

public static class AnimationHelper
{
    private static readonly IEasingFunction EaseOut = CreateEase();

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>현재 값에서 목표 값까지 EaseOut으로 애니메이션한다. 진행 중인 애니메이션은 현재 위치에서 이어받는다.</summary>
    public static void Animate(IAnimatable target, DependencyProperty property, double to, int durationMs, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Math.Max(1, durationMs)))
        {
            EasingFunction = EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (completed is not null)
            animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
