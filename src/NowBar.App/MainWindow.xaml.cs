using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.ViewModels;

namespace NowBar.App;

/// <summary>
/// 상단 중앙 캡슐 창. 확장/축소 애니메이션, 마우스 오버 지연 처리, 항상 위 유지만 담당한다
/// (데이터/상태는 ViewModel).
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _hoverWatchdog;
    private readonly DispatcherTimer _housekeeping;
    private IntPtr _hwnd;
    private bool _hiddenForFullscreen;
    private bool _userHidden;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _settings = vm.Settings;
        DataContext = vm;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs) };
        _collapseTimer.Tick += CollapseTimer_Tick;

        // MouseLeave를 놓치는 경우(빠른 이동, 다른 창 전환)를 대비한 보조 검사
        _hoverWatchdog = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _hoverWatchdog.Tick += HoverWatchdog_Tick;

        // 항상 위 재지정 + 전체 화면 앱 감지
        _housekeeping = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _housekeeping.Tick += (_, _) => Housekeeping();

        // 설정 패널에서 사용하는 확인 창 (이 창을 소유자로 해 항상 NowBar 위에 표시)
        _vm.ConfirmUnsavedSettings = AskUnsavedSettings;
        _vm.SettingsPanel.ConfirmPlan = (plan, title) => Views.HooksApprovalWindow.Ask(this, plan, title);

        _vm.PropertyChanged += OnViewModelChanged;
        _vm.AI.PropertyChanged += OnAIChanged;
        _settings.PropertyChanged += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) => Cleanup();
    }

    /// <summary>트레이 메뉴에서 사용자가 숨긴 상태.</summary>
    public bool UserHidden
    {
        get => _userHidden;
        set
        {
            _userHidden = value;
            UpdateVisibility();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        // Alt+Tab 목록에서 제외 (작업 표시줄에는 ShowInTaskbar=false로 이미 표시 안 됨)
        var ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex = (ex | NativeMethods.WS_EX_TOOLWINDOW) & ~(long)NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        Topmost = _settings.AlwaysOnTop;
        ApplyLayout(animate: false);
        _housekeeping.Start();
    }

    // ── 배치/크기 ────────────────────────────────────────
    private void ApplyLayout(bool animate)
    {
        var rect = WindowPositionHelper.ComputeWindowRect(_settings);
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;

        Island.Margin = new Thickness(0, _settings.TopOffset, 0, 0);
        WorkingGlow.Margin = Island.Margin;
        IslandShadow.Margin = Island.Margin;
        TabRow.Height = new GridLength(_settings.CapsuleHeight - 2);
        TabsGrid.Width = _settings.ExpandedWidth - 2;
        UpdateDetailSize();

        AnimateIsland(_vm.IsExpanded, animate ? _settings.AnimationMs : 1);
    }

    // 확장 크기: 설정이 열려 있으면 설정 패널 크기(더 넓고 높게), 아니면 일반 확장 크기
    private double ExpandedTargetWidth =>
        _vm.IsSettingsOpen ? Math.Max(_settings.ExpandedWidth, AppSettings.SettingsPanelWidth) : _settings.ExpandedWidth;

    private double ExpandedTargetHeight =>
        _vm.IsSettingsOpen ? Math.Max(_settings.ExpandedHeight, AppSettings.SettingsPanelHeight) : _settings.ExpandedHeight;

    /// <summary>상세 영역은 목표 크기로 고정 배치하고 캡슐 크기 애니메이션으로 잘라 보여준다 (애니메이션 중 레이아웃 흔들림 방지).</summary>
    private void UpdateDetailSize()
    {
        DetailHost.Width = ExpandedTargetWidth - 2;
        DetailHost.Height = Math.Max(0, ExpandedTargetHeight - _settings.CapsuleHeight);
    }

    private void AnimateIsland(bool expand, int durationMs)
    {
        var width = expand ? ExpandedTargetWidth : _settings.CompactWidth;
        var height = expand ? ExpandedTargetHeight : _settings.CapsuleHeight;
        var compactRadius = Math.Min(_settings.CornerRadius, _settings.CapsuleHeight / 2);

        // 애니메이션은 현재 값에서 시작하므로 최초 1회는 크기를 직접 지정 (NaN에서 시작 불가)
        if (double.IsNaN(Island.Width) || double.IsNaN(Island.Height))
        {
            Island.Width = width;
            Island.Height = height;
            Island.CornerRadius = new CornerRadius(expand ? _settings.CornerRadius : compactRadius);
            DetailHost.Opacity = expand ? 1 : 0;
            TabsGrid.Opacity = expand ? 1 : 0;
            CompactPill.Opacity = expand ? 0 : 1;
            SetHitTest(expand);
            SetExpandedPartsVisible(expand);
            return;
        }

        if (expand)
        {
            Island.CornerRadius = new CornerRadius(_settings.CornerRadius);
            SetExpandedPartsVisible(true);
        }

        AnimationHelper.Animate(Island, WidthProperty, width, durationMs);
        AnimationHelper.Animate(Island, HeightProperty, height, durationMs, completed: () =>
        {
            if (!_vm.IsExpanded) Island.CornerRadius = new CornerRadius(compactRadius);
        });
        var fadeOut = Math.Max(1, durationMs / 2);
        AnimationHelper.Animate(DetailHost, OpacityProperty, expand ? 1 : 0, expand ? durationMs : fadeOut, completed: () =>
        {
            // 축소가 끝나면 보이지 않는 패널을 렌더링에서 제외 (매초 바뀌는 시간 텍스트가 창을 다시 그리지 않도록)
            if (!_vm.IsExpanded) SetExpandedPartsVisible(false);
        });
        // 항목 하나 ↔ 세 탭 교차 전환
        AnimationHelper.Animate(TabsGrid, OpacityProperty, expand ? 1 : 0, expand ? durationMs : fadeOut);
        AnimationHelper.Animate(CompactPill, OpacityProperty, expand ? 0 : 1, expand ? fadeOut : durationMs);
        SetHitTest(expand);
    }

    private void SetExpandedPartsVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Hidden;
        DetailHost.Visibility = v;
        TabsGrid.Visibility = v;
    }

    private void SetHitTest(bool expanded)
    {
        DetailHost.IsHitTestVisible = expanded;
        TabsGrid.IsHitTestVisible = expanded;
        CompactPill.IsHitTestVisible = !expanded;
    }

    // ── Claude 작업 중 글로우 ────────────────────────────
    private bool _glowOn;

    private void OnAIChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AIActivityViewModel.IsWorking) or "") UpdateGlow();
    }

    private void UpdateGlow()
    {
        var on = _settings.ShowWorkingGlow && _vm.AI.IsWorking;
        if (on == _glowOn) return;
        _glowOn = on;

        if (on)
        {
            WorkingGlow.Visibility = Visibility.Visible;
            // 은은한 호흡 효과. 투명 창은 프레임마다 전체를 다시 그리므로 프레임 수를 낮춰 CPU 사용을 줄인다.
            var pulse = new DoubleAnimation(0.45, 1.0, TimeSpan.FromMilliseconds(1300))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(pulse, 20);
            WorkingGlow.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400));
            Timeline.SetDesiredFrameRate(fade, 30);
            fade.Completed += (_, _) =>
            {
                if (!_glowOn) WorkingGlow.Visibility = Visibility.Collapsed; // 애니메이션 완전 정지
            };
            WorkingGlow.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
    }

    // ── 마우스 오버 확장 / 지연 축소 ─────────────────────
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop(); // 축소 대기 중 다시 들어오면 취소
        if (_settings.AutoExpand) _vm.IsExpanded = true;
    }

    private void Island_MouseLeave(object sender, MouseEventArgs e) => ScheduleCollapse();

    private void ScheduleCollapse()
    {
        if (!_vm.IsExpanded || _vm.IsPinned || _vm.IsSettingsOpen) return;
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void CollapseTimer_Tick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        if (ShouldStayOpen())
        {
            // 슬라이더 드래그 등으로 마우스가 캡처된 상태면 다시 확인
            if (!_vm.IsPinned && Mouse.Captured is not null) _collapseTimer.Start();
            return;
        }
        _vm.IsExpanded = false;
    }

    private bool ShouldStayOpen() =>
        _vm.IsPinned
        || _vm.IsSettingsOpen                // 설정 편집 중에는 마우스가 나가도 닫지 않음
        || Mouse.Captured is not null        // 슬라이더 드래그, 열린 메뉴
        || Island.ContextMenu?.IsOpen == true
        || IsCursorOverIsland();

    private void HoverWatchdog_Tick(object? sender, EventArgs e)
    {
        if (!_vm.IsExpanded || _vm.IsPinned || _vm.IsSettingsOpen)
        {
            _hoverWatchdog.Stop();
            return;
        }
        if (!_collapseTimer.IsEnabled && !ShouldStayOpen())
            ScheduleCollapse();
    }

    private bool IsCursorOverIsland()
    {
        if (!IsVisible || !NativeMethods.GetCursorPos(out var p)) return false;
        try
        {
            // 확장 중에는 애니메이션 진행 중인 현재 크기가 아니라 최종 확장 크기로 판단한다.
            // (커서를 빠르게 아래로 내리면 패널이 다 커지기 전에 '밖'으로 판정되어 닫히는 문제 방지)
            var width = _vm.IsExpanded ? Math.Max(Island.ActualWidth, ExpandedTargetWidth) : Island.ActualWidth;
            var height = _vm.IsExpanded ? Math.Max(Island.ActualHeight, ExpandedTargetHeight) : Island.ActualHeight;
            var center = Island.ActualWidth / 2;
            var topLeft = Island.PointToScreen(new Point(center - width / 2, 0));
            var bottomRight = Island.PointToScreen(new Point(center + width / 2, height));
            return p.X >= topLeft.X && p.X <= bottomRight.X && p.Y >= topLeft.Y && p.Y <= bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // 설정이 열려 있으면 설정만 닫고(저장 확인 포함), 아니면 고정 해제 + 축소
            if (_vm.IsSettingsOpen) _vm.TryCloseSettings();
            else _vm.CollapseAndUnpin();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && _vm.IsSettingsOpen)
        {
            if (_vm.SettingsPanel.SaveCommand.CanExecute(null)) _vm.SettingsPanel.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsExpanded):
                AnimateIsland(_vm.IsExpanded, _settings.AnimationMs);
                if (_vm.IsExpanded)
                {
                    _hoverWatchdog.Start();
                    if (!IsCursorOverIsland()) ScheduleCollapse(); // 트레이/클릭으로 연 경우
                }
                else
                {
                    _collapseTimer.Stop();
                    _hoverWatchdog.Stop();
                }
                break;
            case nameof(MainViewModel.IsPinned):
                if (_vm.IsPinned) _collapseTimer.Stop();
                else if (!IsCursorOverIsland()) ScheduleCollapse();
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                // 설정 열기/닫기: 패널을 설정 크기 ↔ 일반 확장 크기로 부드럽게 변경
                UpdateDetailSize();
                if (_vm.IsExpanded) AnimateIsland(true, _settings.AnimationMs);
                if (_vm.IsSettingsOpen)
                {
                    _collapseTimer.Stop();
                    Activate(); // 입력칸에 바로 입력할 수 있도록 (사용자가 설정을 연 경우에만)
                }
                else if (!_vm.IsPinned && !IsCursorOverIsland())
                {
                    ScheduleCollapse();
                }
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.CompactWidth):
            case nameof(AppSettings.CapsuleHeight):
            case nameof(AppSettings.ExpandedWidth):
            case nameof(AppSettings.ExpandedHeight):
            case nameof(AppSettings.TopOffset):
            case nameof(AppSettings.HorizontalOffset):
            case nameof(AppSettings.CornerRadius):
                ApplyLayout(animate: true);
                break;
            case nameof(AppSettings.AlwaysOnTop):
                Topmost = _settings.AlwaysOnTop;
                if (!_settings.AlwaysOnTop && _hwnd != IntPtr.Zero)
                    NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
                break;
            case nameof(AppSettings.HideInFullscreen):
                Housekeeping();
                break;
            case nameof(AppSettings.ShowWorkingGlow):
                UpdateGlow();
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.InvokeAsync(() => ApplyLayout(animate: false));

    // ── 항상 위 / 전체 화면 ──────────────────────────────
    private void Housekeeping()
    {
        if (_hwnd == IntPtr.Zero) return;

        _hiddenForFullscreen = _settings.HideInFullscreen && !_vm.IsPinned && !_vm.IsSettingsOpen && IsFullscreenAppActive();
        UpdateVisibility();

        // 다른 최상위 창(작업 표시줄 등)에 실제로 가려졌을 때만 최상위 재지정 (포커스는 가져오지 않음).
        // 매번 재지정하면 투명 창 전체가 다시 그려져 다른 '항상 위' 앱과 함께 있을 때 CPU를 계속 사용한다.
        if (_settings.AlwaysOnTop && IsVisible && IsCoveredByOtherWindow())
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOOWNERZORDER);
    }

    /// <summary>캡슐 중앙 지점의 맨 위 창이 NowBar가 아니면 가려진 것으로 본다.</summary>
    private bool IsCoveredByOtherWindow()
    {
        try
        {
            var center = Island.PointToScreen(new Point(Island.ActualWidth / 2, Island.ActualHeight / 2));
            var hit = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = (int)center.X, Y = (int)center.Y });
            return hit != IntPtr.Zero && NativeMethods.GetAncestor(hit, NativeMethods.GA_ROOT) != _hwnd;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void UpdateVisibility()
    {
        var shouldShow = !_userHidden && !_hiddenForFullscreen;
        if (shouldShow && !IsVisible) Show();
        else if (!shouldShow && IsVisible)
        {            _vm.CollapseAndUnpin();
            Hide();
        }
    }

    private static bool IsFullscreenAppActive()
    {
        try
        {
            if (NativeMethods.SHQueryUserNotificationState(out var state) != 0) return false;
            return state is NativeMethods.UserNotificationState.Busy
                or NativeMethods.UserNotificationState.RunningD3DFullScreen
                or NativeMethods.UserNotificationState.PresentationMode;
        }
        catch
        {
            return false;
        }
    }

    private bool? AskUnsavedSettings()
    {
        var answer = MessageBox.Show(this,
            "저장하지 않은 설정 변경 사항이 있습니다.\n저장하시겠습니까?\n\n[아니요]를 누르면 설정을 열기 전 상태로 되돌립니다.",
            "NowBar 설정", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => true,
            MessageBoxResult.No => false,
            _ => null,
        };
    }

    // ── 메뉴 ─────────────────────────────────────────────
    private void HideMenu_Click(object sender, RoutedEventArgs e) => UserHidden = true;

    private void ExitMenu_Click(object sender, RoutedEventArgs e) => ((App)Application.Current).ExitApp();

    private void Cleanup()
    {
        _collapseTimer.Stop();
        _hoverWatchdog.Stop();
        _housekeeping.Stop();
        _vm.PropertyChanged -= OnViewModelChanged;
        _vm.AI.PropertyChanged -= OnAIChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
