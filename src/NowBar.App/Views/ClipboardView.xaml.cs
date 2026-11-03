using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NowBar.App.Models;
using NowBar.App.ViewModels;

namespace NowBar.App.Views;

public partial class ClipboardView : UserControl
{
    public ClipboardView() => InitializeComponent();

    /// <summary>클릭 = 다시 복사, Shift+클릭 = 원래 쓰던 창에 붙여넣기.</summary>
    private async void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is ClipboardViewModel vm && (sender as FrameworkElement)?.DataContext is ClipboardEntry entry)
        {
            await vm.UseAsync(entry, paste: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }
}
