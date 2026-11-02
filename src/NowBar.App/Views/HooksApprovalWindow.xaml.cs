using System.Windows;
using NowBar.App.Services;

namespace NowBar.App.Views;

/// <summary>Claude Code settings.json을 수정하기 전에 변경 내용을 보여주고 승인을 받는 창.</summary>
public partial class HooksApprovalWindow : Window
{
    private HooksApprovalWindow(HooksPlan plan, string title)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = $"{title}: Claude Code 설정 파일을 다음과 같이 변경합니다.";
        PathText.Text = plan.SettingsPath;
        ChangesList.ItemsSource = plan.Changes;
        WarningsList.ItemsSource = plan.Warnings;
        PreviewBox.Text = plan.PreviewText;
    }

    public static bool Ask(Window owner, HooksPlan plan, string title)
    {
        var window = new HooksApprovalWindow(plan, title) { Owner = owner };
        return window.ShowDialog() == true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
