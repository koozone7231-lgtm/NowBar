using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace NowBar.App.ViewModels;

/// <summary>도구 탭의 섹션.</summary>
public enum ToolsSection { Mixer, QuickSettings, Clipboard, System }

/// <summary>"도구" 탭: 위쪽 칩으로 섹션을 고르고 그 아래에 해당 도구를 보여준다.</summary>
public partial class ToolsViewModel : ObservableObject
{
    public AudioMixerViewModel Mixer { get; }
    public QuickSettingsViewModel QuickSettings { get; }
    public ClipboardViewModel Clipboard { get; }
    public SystemMonitorViewModel System { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMixerSection), nameof(IsQuickSettingsSection), nameof(IsClipboardSection), nameof(IsSystemSection))]
    private ToolsSection _section = ToolsSection.QuickSettings;

    public bool IsMixerSection => Section == ToolsSection.Mixer;
    public bool IsQuickSettingsSection => Section == ToolsSection.QuickSettings;
    public bool IsClipboardSection => Section == ToolsSection.Clipboard;
    public bool IsSystemSection => Section == ToolsSection.System;

    public ToolsViewModel(AudioMixerViewModel mixer, QuickSettingsViewModel quickSettings, ClipboardViewModel clipboard, SystemMonitorViewModel system)
    {
        Mixer = mixer;
        QuickSettings = quickSettings;
        Clipboard = clipboard;
        System = system;
    }

    [RelayCommand]
    private void SelectSection(ToolsSection section) => Section = section;
}
