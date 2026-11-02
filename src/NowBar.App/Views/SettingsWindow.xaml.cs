using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NowBar.App.ViewModels;

namespace NowBar.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        // Claude Code 설정 파일 변경 전 사용자 승인
        vm.ConfirmPlan = (plan, title) => HooksApprovalWindow.Ask(this, plan, title);

        // Ctrl+S = 저장
        InputBindings.Add(new KeyBinding(vm.SaveCommand, Key.S, ModifierKeys.Control));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !_vm.HasUnsavedChanges) return;

        var answer = MessageBox.Show(this,
            "저장하지 않은 변경 사항이 있습니다.\n저장하시겠습니까?\n\n[아니요]를 누르면 창을 열기 전 설정으로 되돌립니다.",
            "NowBar 설정", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        switch (answer)
        {
            case MessageBoxResult.Yes:
                _vm.SaveCommand.Execute(null);
                break;
            case MessageBoxResult.No:
                _vm.RevertCommand.Execute(null);
                break;
            default:
                e.Cancel = true;
                break;
        }
    }
}
