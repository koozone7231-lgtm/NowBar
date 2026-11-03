using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NowBar.App.ViewModels;

/// <summary>도구 탭의 섹션. 클립보드·시스템·빠른 설정이 여기에 차례로 추가된다.</summary>
public enum ToolsSection { Mixer, QuickSettings }

/// <summary>"도구" 탭: 위쪽 칩으로 섹션을 고르고 그 아래에 해당 도구를 보여준다.</summary>
public partial class ToolsViewModel : ObservableObject
{
    public AudioMixerViewModel Mixer { get; }
    public QuickSettingsViewModel QuickSettings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMixerSection), nameof(IsQuickSettingsSection))]
    private ToolsSection _section = ToolsSection.QuickSettings;

    public bool IsMixerSection => Section == ToolsSection.Mixer;
    public bool IsQuickSettingsSection => Section == ToolsSection.QuickSettings;

    public ToolsViewModel(AudioMixerViewModel mixer, QuickSettingsViewModel quickSettings)
    {
        Mixer = mixer;
        QuickSettings = quickSettings;
    }

    [RelayCommand]
    private void SelectSection(ToolsSection section) => Section = section;
}
