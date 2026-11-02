using System.Windows;
using NowBar.App.Models;

namespace NowBar.App.Helpers;

/// <summary>
/// 창 배치 계산. 창 자체는 확장 크기 + 그림자 여백만큼 고정 크기로 두고(투명 영역은 클릭이 통과됨),
/// 내부 캡슐(Island)의 크기만 애니메이션한다. 창 크기를 매 프레임 바꾸지 않으므로 깜빡임이 없다.
/// </summary>
public static class WindowPositionHelper
{
    /// <summary>그림자가 잘리지 않도록 캡슐 주변에 두는 투명 여백(DIP).</summary>
    public const double ShadowMargin = 24;

    public static Rect ComputeWindowRect(AppSettings s)
    {
        var width = Math.Max(s.CompactWidth, s.ExpandedWidth) + ShadowMargin * 2;
        var height = s.TopOffset + Math.Max(s.CapsuleHeight, s.ExpandedHeight) + ShadowMargin;

        // 메인 모니터 기준, 해상도와 무관하게 수평 중앙 정렬 (DIP 단위)
        var screenLeft = 0.0;
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var left = screenLeft + (screenWidth - width) / 2 + s.HorizontalOffset;
        return new Rect(Math.Round(left), 0, Math.Ceiling(width), Math.Ceiling(height));
    }
}
