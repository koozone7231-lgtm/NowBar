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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeatherSelected), nameof(IsMusicSelected), nameof(IsAISelected))]
    private NowBarTab _selectedTab;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isSettingsOpen;

    public bool IsWeatherSelected => SelectedTab == NowBarTab.Weather;
    public bool IsMusicSelected => SelectedTab == NowBarTab.Music;
    public bool IsAISelected => SelectedTab == NowBarTab.AI;

    /// <summary>
    /// 저장하지 않은 설정이 있을 때 물어보는 함수 (View가 제공).
    /// true = 저장, false = 저장 안 함(되돌림), null = 취소(설정 유지).
    /// </summary>
    public Func<bool?>? ConfirmUnsavedSettings { get; set; }

    public MainViewModel(SettingsService settingsService, WeatherViewModel weather, MusicViewModel music,
        AIActivityViewModel ai, SettingsViewModel settingsPanel)
    {
        _settingsService = settingsService;
        Settings = settingsService.Settings;
        Weather = weather;
        Music = music;
        AI = ai;
        SettingsPanel = settingsPanel;
        SettingsPanel.CloseRequested += (_, _) => TryCloseSettings();
        _selectedTab = Settings.LastSelectedTab; // 마지막으로 선택한 탭 복원 (최초 실행 시 날씨)
    }

    partial void OnSelectedTabChanged(NowBarTab value) => Settings.LastSelectedTab = value;

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
