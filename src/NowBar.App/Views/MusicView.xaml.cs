using System.Windows.Controls;
using System.Windows.Input;
using NowBar.App.ViewModels;

namespace NowBar.App.Views;

public partial class MusicView : UserControl
{
    public MusicView() => InitializeComponent();

    private MusicViewModel? Vm => DataContext as MusicViewModel;

    // 드래그 중에는 재생 위치 자동 갱신을 멈추고, 놓는 순간 한 번만 위치 변경을 요청한다
    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Vm?.BeginSeek();

    private async void SeekSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Vm is { } vm) await vm.CommitSeekAsync(SeekSlider.Value);
    }

    private async void SeekSlider_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Released && Vm is { } vm)
            await vm.CommitSeekAsync(SeekSlider.Value);
    }
}
