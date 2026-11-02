using CommunityToolkit.Mvvm.ComponentModel;

namespace NowBar.App.Models;

public enum AppTheme { Dark, Light }

public enum NowBarTab { Weather, Music, AI }

/// <summary>마우스를 올리지 않았을 때 축소 캡슐에 보여줄 내용.</summary>
public enum CompactDisplay
{
    /// <summary>눈만 있는 얼굴 — 작업 중·음악 감상 등 상태를 표정으로 표현</summary>
    Face,
    /// <summary>선택한 탭 하나 (아이콘 + 이름 + 상태)</summary>
    Item,
}

/// <summary>
/// %AppData%\NowBar\settings.json 에 저장되는 사용자 설정.
/// 속성 변경은 SettingsService가 감지하여 자동 저장한다.
/// </summary>
public partial class AppSettings : ObservableObject
{
    // ── 일반 ──────────────────────────────────────────────
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private double _topOffset = 8;
    [ObservableProperty] private double _horizontalOffset;
    /// <summary>마우스를 올리지 않았을 때(항목 하나만 표시) 캡슐 너비.</summary>
    [ObservableProperty] private double _compactWidth = 200;
    [ObservableProperty] private double _capsuleHeight = 48;
    [ObservableProperty] private double _expandedWidth = 480;
    [ObservableProperty] private double _expandedHeight = 270;
    [ObservableProperty] private bool _autoExpand = true;
    [ObservableProperty] private int _collapseDelayMs = 500;
    [ObservableProperty] private bool _alwaysOnTop = true;
    [ObservableProperty] private bool _hideInFullscreen = true;

    // ── 디자인 ────────────────────────────────────────────
    [ObservableProperty] private AppTheme _theme = AppTheme.Dark;
    [ObservableProperty] private double _backgroundOpacity = 0.94;
    [ObservableProperty] private string _accentColor = "#1ED760";
    [ObservableProperty] private double _cornerRadius = 20;
    [ObservableProperty] private int _animationMs = 200;

    // ── 날씨 ──────────────────────────────────────────────
    [ObservableProperty] private GeoLocation? _location;
    [ObservableProperty] private TemperatureUnit _temperatureUnit = TemperatureUnit.Celsius;
    [ObservableProperty] private int _weatherRefreshMinutes = 15;

    // ── 음악 ──────────────────────────────────────────────
    [ObservableProperty] private bool _autoSelectMediaSession = true;
    [ObservableProperty] private bool _allowSessionPicker = true;
    [ObservableProperty] private bool _showAlbumArt = true;

    // ── AI (Claude Code) ──────────────────────────────────
    [ObservableProperty] private bool _claudeIntegrationEnabled = true;
    [ObservableProperty] private int _hookPort = 17432;
    [ObservableProperty] private string _hookToken = "";
    /// <summary>프롬프트 앞부분을 작업 요약으로 표시할지 여부. 기본값은 꺼짐(사용자 동의 필요).</summary>
    [ObservableProperty] private bool _showTaskSummary;
    [ObservableProperty] private int _summaryMaxLength = 40;
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _showSessions = true;
    /// <summary>Claude 작업 중일 때 캡슐 테두리에 은은하게 깜빡이는 글로우 표시.</summary>
    [ObservableProperty] private bool _showWorkingGlow = true;

    [ObservableProperty] private CompactDisplay _compactDisplay = CompactDisplay.Face;

    /// <summary>디자인 기본값 마이그레이션 버전 (2 = Spotify 영감 디자인).</summary>
    [ObservableProperty] private int _designVersion;

    // ── 상태 ──────────────────────────────────────────────
    [ObservableProperty] private NowBarTab _lastSelectedTab = NowBarTab.Weather;

    public const double MinCompactWidth = 160, MaxCompactWidth = 400;
    public const double MinCapsuleHeight = 40, MaxCapsuleHeight = 64;
    public const double MinExpandedWidth = 440, MaxExpandedWidth = 600;
    public const double MinExpandedHeight = 220, MaxExpandedHeight = 340;

    /// <summary>설정을 열었을 때 확장되는 패널 크기 (탭 영역 포함).</summary>
    public const double SettingsPanelWidth = 540, SettingsPanelHeight = 480;
    public const int CurrentDesignVersion = 2;

    /// <summary>얼굴 모드일 때 축소 캡슐 너비.</summary>
    public const double FaceCompactWidth = 120;

    /// <summary>
    /// 이전 디자인의 기본값을 그대로 쓰고 있으면 새 디자인 기본값으로 바꾼다 (사용자가 직접 바꾼 값은 유지).
    /// </summary>
    public bool MigrateDesignDefaults()
    {
        if (DesignVersion >= CurrentDesignVersion) return false;
        if (string.Equals(AccentColor, "#9EB4FF", StringComparison.OrdinalIgnoreCase)) AccentColor = "#1ED760";
        if (Math.Abs(CornerRadius - 24) < 0.01) CornerRadius = 20;
        DesignVersion = CurrentDesignVersion;
        return true;
    }

    /// <summary>손상되었거나 범위를 벗어난 값을 안전한 범위로 보정한다.</summary>
    public void Normalize()
    {
        TopOffset = Math.Clamp(TopOffset, 0, 60);
        HorizontalOffset = Math.Clamp(HorizontalOffset, -800, 800);
        CompactWidth = Math.Clamp(CompactWidth, MinCompactWidth, MaxCompactWidth);
        CapsuleHeight = Math.Clamp(CapsuleHeight, MinCapsuleHeight, MaxCapsuleHeight);
        ExpandedWidth = Math.Clamp(ExpandedWidth, MinExpandedWidth, MaxExpandedWidth);
        ExpandedHeight = Math.Clamp(ExpandedHeight, MinExpandedHeight, MaxExpandedHeight);
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 200, 3000);
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.5, 1.0);
        CornerRadius = Math.Clamp(CornerRadius, 8, 32);
        AnimationMs = Math.Clamp(AnimationMs, 80, 600);
        WeatherRefreshMinutes = Math.Clamp(WeatherRefreshMinutes, 10, 60);
        HookPort = HookPort is >= 1024 and <= 65535 ? HookPort : 17432;
        SummaryMaxLength = Math.Clamp(SummaryMaxLength, 10, 120);
        if (string.IsNullOrWhiteSpace(AccentColor)) AccentColor = "#1ED760";
        if (!Enum.IsDefined(Theme)) Theme = AppTheme.Dark;
        if (!Enum.IsDefined(CompactDisplay)) CompactDisplay = CompactDisplay.Face;
        if (!Enum.IsDefined(LastSelectedTab)) LastSelectedTab = NowBarTab.Weather;
        if (!Enum.IsDefined(TemperatureUnit)) TemperatureUnit = TemperatureUnit.Celsius;
    }
}
