using System.Windows.Controls;
using System.Windows.Input;
using NowBar.App.ViewModels;

namespace NowBar.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void LocationBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is SettingsViewModel vm && vm.SearchLocationCommand.CanExecute(null))
        {
            vm.SearchLocationCommand.Execute(null);
            e.Handled = true;
        }
    }
}
