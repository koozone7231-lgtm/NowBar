using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Models;

namespace NowBar.App.ViewModels;

/// <summary>
/// 캡슐 전체 상태. 확장(마우스 오버)과 탭 선택(클릭)은 서로 독립적인 상태로 관리한다.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public AppSettings Settings { get; }
    public WeatherViewModel Weather { get; }
    public MusicViewModel Music { get; }
    public AIActivityViewModel AI { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWeatherSelected), nameof(IsMusicSelected), nameof(IsAISelected))]
    private NowBarTab _selectedTab;

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isPinned;

    public bool IsWeatherSelected => SelectedTab == NowBarTab.Weather;
    public bool IsMusicSelected => SelectedTab == NowBarTab.Music;
    public bool IsAISelected => SelectedTab == NowBarTab.AI;

    public event EventHandler? OpenSettingsRequested;

    public MainViewModel(AppSettings settings, WeatherViewModel weather, MusicViewModel music, AIActivityViewModel ai)
    {
        Settings = settings;
        Weather = weather;
        Music = music;
        AI = ai;
        _selectedTab = settings.LastSelectedTab; // 마지막으로 선택한 탭 복원 (최초 실행 시 날씨)
    }

    partial void OnSelectedTabChanged(NowBarTab value) => Settings.LastSelectedTab = value;

    partial void OnIsPinnedChanged(bool value)
    {
        if (value) IsExpanded = true;
    }

    [RelayCommand]
    private void SelectTab(NowBarTab tab)
    {
        SelectedTab = tab;
        // 자동 확장이 꺼져 있으면 클릭으로 펼친다
        if (!Settings.AutoExpand) IsExpanded = true;
    }

    /// <summary>축소된 캡슐 클릭 (자동 확장을 끈 경우의 확장 수단).</summary>
    [RelayCommand]
    private void Expand() => IsExpanded = true;

    [RelayCommand]
    private void TogglePin() => IsPinned = !IsPinned;

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);

    public void CollapseAndUnpin()
    {
        IsPinned = false;
        IsExpanded = false;
    }
}
