using NowBar.App.Helpers;
using NowBar.App.Models;
using Windows.Networking.Connectivity;
using Windows.System.Power;

namespace NowBar.App.Services;

/// <summary>
/// 배터리·네트워크 상태. 변화는 Windows 이벤트로 받으며(폴링 없음),
/// Wi-Fi 신호 세기처럼 이벤트가 없는 값은 패널이 펼쳐져 있을 때만 <see cref="Refresh"/>로 갱신한다.
/// 이벤트는 백그라운드 스레드에서 발생하므로 구독자가 UI 스레드로 마샬링해야 한다.
/// </summary>
public sealed class SystemStatusService : IDisposable
{
    private readonly object _gate = new();
    private Timer? _debounce;
    private bool _started;

    public SystemStatusSnapshot Current { get; private set; } = SystemStatusSnapshot.Unknown;

    public event EventHandler<SystemStatusSnapshot>? Changed;

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            PowerManager.RemainingChargePercentChanged += OnPowerChanged;
            PowerManager.BatteryStatusChanged += OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
        }
        catch (Exception ex)
        {
            Logger.Error("배터리 상태 구독 실패", ex);
        }
        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkChanged;
        }
        catch (Exception ex)
        {
            Logger.Error("네트워크 상태 구독 실패", ex);
        }
        Refresh();
    }

    /// <summary>현재 상태를 다시 읽어 바뀌었으면 <see cref="Changed"/>를 발생시킨다.</summary>
    public void Refresh()
    {
        var next = Read();
        lock (_gate)
        {
            if (next == Current) return;
            Current = next;
        }
        Changed?.Invoke(this, next);
    }

    public static SystemStatusSnapshot Read()
    {
        bool hasBattery = false, charging = false, plugged = true;
        var percent = 100;
        try
        {
            var battery = PowerManager.BatteryStatus;
            hasBattery = battery != BatteryStatus.NotPresent;
            charging = battery == BatteryStatus.Charging;
            plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
            percent = Math.Clamp(PowerManager.RemainingChargePercent, 0, 100);
        }
        catch (Exception ex)
        {
            Logger.Error("배터리 상태 읽기 실패", ex);
        }

        var kind = NetworkKind.Offline;
        var bars = 0;
        string? name = null;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is not null)
            {
                var level = profile.GetNetworkConnectivityLevel();
                name = profile.ProfileName;
                if (profile.IsWlanConnectionProfile)
                {
                    kind = NetworkKind.Wifi;
                    bars = profile.GetSignalBars() ?? 0;
                    try
                    {
                        var ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid();
                        if (!string.IsNullOrWhiteSpace(ssid)) name = ssid;
                    }
                    catch
                    {
                        // 위치 권한이 없으면 SSID를 못 읽는다 → 프로필 이름 사용
                    }
                }
                else if (profile.IsWwanConnectionProfile)
                {
                    kind = NetworkKind.Other;
                    bars = profile.GetSignalBars() ?? 0;
                }
                else
                {
                    kind = NetworkKind.Ethernet;
                }

                if (level == NetworkConnectivityLevel.None) kind = NetworkKind.Offline;
                else if (level != NetworkConnectivityLevel.InternetAccess) kind = NetworkKind.Limited;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("네트워크 상태 읽기 실패", ex);
        }

        return new SystemStatusSnapshot(hasBattery, percent, charging, plugged, kind, Math.Clamp(bars, 0, 5), name);
    }

    private void OnPowerChanged(object? sender, object e) => Refresh();

    // 네트워크 이벤트는 연결 전환 중 여러 번 연달아 오므로 0.5초 모아서 한 번만 읽는다
    private void OnNetworkChanged(object sender)
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => Refresh(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(500, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_started)
        {
            try
            {
                PowerManager.RemainingChargePercentChanged -= OnPowerChanged;
                PowerManager.BatteryStatusChanged -= OnPowerChanged;
                PowerManager.PowerSupplyStatusChanged -= OnPowerChanged;
                NetworkInformation.NetworkStatusChanged -= OnNetworkChanged;
            }
            catch { /* 종료 중 */ }
        }
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
