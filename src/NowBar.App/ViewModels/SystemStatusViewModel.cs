using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>펼친 패널 오른쪽 위의 배터리·네트워크 아이콘과, 얼굴 표정(배터리 부족·오프라인)에 쓰는 상태.</summary>
public partial class SystemStatusViewModel : ObservableObject, IDisposable
{
    private readonly SystemStatusService _service;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _pluggedTimer;

    /// <summary>충전기를 방금 연결함 (몇 초 동안만 true) → 얼굴이 충전 시작 표정을 짓는다.</summary>
    [ObservableProperty] private bool _justPluggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBattery), nameof(BatteryPercent), nameof(IsCharging), nameof(IsBatteryLow),
        nameof(IsBatteryCritical), nameof(BatteryText), nameof(BatteryTooltip), nameof(Network), nameof(WifiBars),
        nameof(IsOffline), nameof(NetworkTooltip))]
    private SystemStatusSnapshot _status = SystemStatusSnapshot.Unknown;

    public SystemStatusViewModel(SystemStatusService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        _service.Changed += (_, s) => Ui(() => Status = s);
        _pluggedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _pluggedTimer.Tick += (_, _) => { _pluggedTimer.Stop(); JustPluggedIn = false; };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ShowSystemStatus)) OnPropertyChanged(nameof(IsVisible));
        };
        // Wi-Fi 신호 세기는 변경 이벤트가 없어 패널이 펼쳐져 있을 때만 가끔 다시 읽는다
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _refreshTimer.Tick += (_, _) => _service.Refresh();
    }

    public bool IsVisible => _settings.ShowSystemStatus;

    /// <summary>배터리가 있는 기기에서 전원이 빠져 있다가 연결된 순간인지.</summary>
    public static bool IsPlugInMoment(SystemStatusSnapshot before, SystemStatusSnapshot after) =>
        before != SystemStatusSnapshot.Unknown && after.HasBattery && !before.IsPluggedIn && after.IsPluggedIn;

    partial void OnStatusChanged(SystemStatusSnapshot? oldValue, SystemStatusSnapshot newValue)
    {
        if (oldValue is not null && IsPlugInMoment(oldValue, newValue))
        {
            Logger.Info($"충전기 연결 ({newValue.BatteryPercent}%)");
            JustPluggedIn = true;
            _pluggedTimer.Stop();
            _pluggedTimer.Start();
        }
        else if (!newValue.IsPluggedIn && JustPluggedIn)
        {
            _pluggedTimer.Stop();
            JustPluggedIn = false;
        }
    }

    public bool HasBattery => Status.HasBattery;
    public int BatteryPercent => Status.BatteryPercent;
    public bool IsCharging => Status.IsCharging;
    public bool IsBatteryLow => Status.IsBatteryLow;
    public bool IsBatteryCritical => Status.IsBatteryCritical;
    public string BatteryText => $"{Status.BatteryPercent}%";

    public string BatteryTooltip =>
        Status.IsCharging ? $"배터리 {Status.BatteryPercent}% · 충전 중"
        : Status.IsPluggedIn ? $"배터리 {Status.BatteryPercent}% · 전원 연결됨"
        : $"배터리 {Status.BatteryPercent}%";

    public NetworkKind Network => Status.Network;
    public int WifiBars => Status.WifiBars;
    public bool IsOffline => Status.IsOffline;

    public string NetworkTooltip => Status.Network switch
    {
        NetworkKind.Offline => "인터넷에 연결되어 있지 않음",
        NetworkKind.Limited => $"인터넷 연결 제한됨{Name()}",
        NetworkKind.Wifi => $"Wi-Fi{Name()} · 신호 {Status.WifiBars}/5",
        NetworkKind.Ethernet => $"유선 네트워크{Name()}",
        _ => $"네트워크{Name()}",
    };

    private string Name() => string.IsNullOrWhiteSpace(Status.NetworkName) ? "" : $" · {Status.NetworkName}";

    public void Start()
    {
        _service.Start();
        Status = _service.Current;
    }

    /// <summary>패널이 펼쳐졌을 때만 주기적으로 갱신한다.</summary>
    public void SetPanelOpen(bool open)
    {
        if (open)
        {
            _service.Refresh();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }
    }

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { Logger.Error("시스템 상태 UI 갱신 실패", ex); }
        });
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _pluggedTimer.Stop();
    }
}
