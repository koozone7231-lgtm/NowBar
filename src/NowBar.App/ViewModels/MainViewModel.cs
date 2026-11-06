using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>
/// 캡슐 전체 상태. 확장(마우스 오버)과 탭 선택(클릭)은 서로 독립적인 상태로 관리한다.
/// 설정은 별도 창이 아니라 확장 패널 안에서 열린다(<see cref="IsSettingsOpen"/>).
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    public AppSettings Settings { get; }
    public WeatherViewModel Weather { get; }
    public MusicViewModel Music { get; }
    public AIActivityViewModel AI { get; }
    public SettingsViewModel SettingsPanel { get; }
    public SystemStatusViewModel Status { get; }
    public ToolsViewModel Tools { get; }
    public UpdateViewModel Update => SettingsPanel.Update;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeatherSelected), nameof(IsMusicSelected), nameof(IsAISelected), nameof(IsToolsSelected))]
    private NowBarTab _selectedTab;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isSettingsOpen;

    public bool IsWeatherSelected => SelectedTab == NowBarTab.Weather;
    public bool IsMusicSelected => SelectedTab == NowBarTab.Music;
    public bool IsAISelected => SelectedTab == NowBarTab.AI;
    public bool IsToolsSelected => SelectedTab == NowBarTab.Tools;

    /// <summary>
    /// 저장하지 않은 설정이 있을 때 물어보는 함수 (View가 제공).
    /// true = 저장, false = 저장 안 함(되돌림), null = 취소(설정 유지).
    /// </summary>
    public Func<bool?>? ConfirmUnsavedSettings { get; set; }

    public MainViewModel(SettingsService settingsService, WeatherViewModel weather, MusicViewModel music,
        AIActivityViewModel ai, SettingsViewModel settingsPanel, SystemStatusViewModel status, ToolsViewModel tools)
    {
        Tools = tools;
        Tools.PropertyChanged += (_, _) => UpdateActivity();
        Status = status;
        _settingsService = settingsService;
        Settings = settingsService.Settings;
        Weather = weather;
        Music = music;
        AI = ai;
        SettingsPanel = settingsPanel;
        SettingsPanel.CloseRequested += (_, _) => TryCloseSettings();
        _selectedTab = Settings.LastSelectedTab; // 마지막으로 선택한 탭 복원 (최초 실행 시 날씨)

        // 얼굴 표정은 AI 작업·음악 상태에서 정해진다
        AI.PropertyChanged += (_, _) => RaiseFace();
        Music.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MusicViewModel.IsPlaying) or nameof(MusicViewModel.State) or "") RaiseFace();
        };
        Status.PropertyChanged += (_, _) => RaiseFace();
        Tools.System.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemMonitorViewModel.IsStrained)) RaiseFace();
        };
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.CompactDisplay)) OnPropertyChanged(nameof(IsFaceMode));
            if (e.PropertyName == nameof(AppSettings.ShowSystemStatus)) RaiseFace();
        };
    }

    // ── 보일 때만 돌리는 갱신 (믹서 1초 폴링, Wi-Fi 신호 15초) ──
    partial void OnIsExpandedChanged(bool value)
    {
        // 펼치기 직전에 쓰던 창을 기억해 둔다 (클립보드 Shift+클릭 붙여넣기 대상)
        if (value && Services.ClipboardService.GetForeignForeground() is var target && target != IntPtr.Zero)
            Tools.Clipboard.PasteTarget = target;
        UpdateActivity();
    }
    partial void OnIsSettingsOpenChanged(bool value) => UpdateActivity();

    private void UpdateActivity()
    {
        // 볼륨 믹서는 도구 탭에서 믹서 섹션이 보일 때만 1초마다 갱신
        var toolsVisible = IsExpanded && !IsSettingsOpen && IsToolsSelected;
        // 빠른 설정의 볼륨 줄도 믹서의 전체 볼륨을 쓴다
        Tools.Mixer.SetActive(toolsVisible && (Tools.IsMixerSection || Tools.IsQuickSettingsSection));
        Tools.QuickSettings.SetActive(toolsVisible && Tools.IsQuickSettingsSection);
        Tools.Clipboard.SetActive(toolsVisible && Tools.IsClipboardSection);
        Tools.System.SetActive(toolsVisible && Tools.IsSystemSection);
        Status.SetPanelOpen(IsExpanded);
    }

    // ── 얼굴 모드 ────────────────────────────────────────
    public bool IsFaceMode => Settings.CompactDisplay == CompactDisplay.Face;

    /// <summary>
    /// 우선순위: 응답 필요 → (방금) 충전 시작 → 작업 중 → (방금) 오류 → 배터리 부족 → 오프라인 → 과부하 → 음악 감상 → (방금) 완료 → 평소.
    /// 사용자가 행동해야 하는 상태가 가장 먼저 보이도록 한다.
    /// </summary>
    public Controls.FaceMood FaceMood
    {
        get
        {
            var ai = AI.AggregateState;
            if (ai == ClaudeTaskState.Waiting) return Controls.FaceMood.Waiting;
            if (Settings.ShowSystemStatus && Status.JustPluggedIn) return Controls.FaceMood.Charging;
            if (AI.IsWorking) return Controls.FaceMood.Working;
            if (ai == ClaudeTaskState.Error && AI.RecentlyFinished) return Controls.FaceMood.Error;
            if (Settings.ShowSystemStatus && Status.IsBatteryCritical) return Controls.FaceMood.LowBattery;
            if (Settings.ShowSystemStatus && Status.IsOffline) return Controls.FaceMood.Offline;
            if (Settings.ShowSystemStatus && Tools.System.IsStrained) return Controls.FaceMood.Strained;
            if (Music.IsPlaying) return Controls.FaceMood.Music;
            if (ai == ClaudeTaskState.Completed && AI.RecentlyFinished) return Controls.FaceMood.Happy;
            return Controls.FaceMood.Idle;
        }
    }

    public string FaceStatusText => FaceMood switch
    {
        Controls.FaceMood.Waiting => "Claude가 응답을 기다리고 있어요",
        Controls.FaceMood.Working => "Claude가 작업 중이에요",
        Controls.FaceMood.Charging => $"충전을 시작했어요 ({Status.BatteryPercent}%)",
        Controls.FaceMood.LowBattery => $"배터리가 부족해요 ({Status.BatteryPercent}%)",
        Controls.FaceMood.Offline => "인터넷에 연결되어 있지 않아요",
        Controls.FaceMood.Strained => Tools.System.StrainText,
        Controls.FaceMood.Error => "Claude 작업 중 오류가 났어요",
        Controls.FaceMood.Music => $"음악 감상 중 · {Music.DisplayTitle}",
        Controls.FaceMood.Happy => "Claude 작업이 끝났어요",
        _ => "NowBar",
    };

    private Controls.FaceMood _lastMood;

    private void RaiseFace()
    {
        var mood = FaceMood;
        if (mood == _lastMood) return; // 매초 갱신되는 AI 상태에도 표정이 바뀔 때만 알린다
        _lastMood = mood;
        OnPropertyChanged(nameof(FaceMood));
        OnPropertyChanged(nameof(FaceStatusText));
    }

    partial void OnSelectedTabChanged(NowBarTab value)
    {
        Settings.LastSelectedTab = value;
        UpdateActivity();
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (value) IsExpanded = true;
    }

    [RelayCommand]
    private void SelectTab(NowBarTab tab)
    {
        // 설정을 보고 있었다면 탭을 누르는 것은 "설정 닫고 이 탭 보기"
        if (IsSettingsOpen && !TryCloseSettings()) return;
        SelectedTab = tab;
        // 자동 확장이 꺼져 있으면 클릭으로 펼친다
        if (!Settings.AutoExpand) IsExpanded = true;
    }

    /// <summary>축소된 캡슐 클릭 (자동 확장을 끈 경우의 확장 수단).</summary>
    [RelayCommand]
    private void Expand() => IsExpanded = true;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    /// <summary>숨김 모드 켜기: 패널을 접고 화면 위로 숨긴다 (창이 설정 변경을 보고 처리).</summary>
    [RelayCommand]
    private void EnterHiddenMode()
    {
        CollapseAndUnpin();
        Settings.HiddenMode = true;
    }

    /// <summary>설정 아이콘: 열려 있으면 닫고, 닫혀 있으면 패널을 키워 설정을 연다.</summary>
    [RelayCommand]
    private void OpenSettings()
    {
        if (IsSettingsOpen)
        {
            TryCloseSettings();
            return;
        }
        ShowSettings();
    }

    /// <summary>트레이 메뉴 등 외부에서 설정을 여는 경로 (이미 열려 있으면 그대로 둔다).</summary>
    public void ShowSettings()
    {
        if (!IsSettingsOpen)
        {
            _settingsService.BeginEdit(); // 이때부터 [저장]해야 파일에 기록
            SettingsPanel.OnOpened();
            IsSettingsOpen = true;
        }
        IsExpanded = true;
    }

    /// <summary>설정을 닫는다. 저장하지 않은 변경이 있으면 물어보고, 사용자가 취소하면 false.</summary>
    public bool TryCloseSettings()
    {
        if (!IsSettingsOpen) return true;

        if (_settingsService.HasUnsavedChanges)
        {
            bool? answer = ConfirmUnsavedSettings is { } ask ? ask() : false;
            if (answer is null) return false;
            _settingsService.EndEdit(save: answer.Value);
        }
        else
        {
            _settingsService.EndEdit(save: false);
        }

        IsSettingsOpen = false;
        return true;
    }

    public void CollapseAndUnpin()
    {
        IsPinned = false;
        IsExpanded = false;
    }
}
