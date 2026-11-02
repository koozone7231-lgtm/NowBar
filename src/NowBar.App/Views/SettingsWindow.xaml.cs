using System.Windows;
using NowBar.App.ViewModels;

namespace NowBar.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Claude Code 설정 파일 변경 전 사용자 승인
        vm.ConfirmPlan = (plan, title) => HooksApprovalWindow.Ask(this, plan, title);
    }
}
