namespace NowBar.App.Models;

public enum NetworkKind
{
    /// <summary>연결된 네트워크 없음</summary>
    Offline,
    /// <summary>연결은 됐지만 인터넷 접근이 제한됨 (로그인 필요한 Wi-Fi 등)</summary>
    Limited,
    Wifi,
    Ethernet,
    /// <summary>모바일 데이터 등 기타</summary>
    Other,
}

/// <summary>배터리·네트워크 상태 스냅샷 (Windows.System.Power / Windows.Networking.Connectivity).</summary>
public sealed record SystemStatusSnapshot(
    bool HasBattery,
    int BatteryPercent,
    bool IsCharging,
    bool IsPluggedIn,
    NetworkKind Network,
    int WifiBars,
    string? NetworkName)
{
    public static SystemStatusSnapshot Unknown { get; } = new(false, 100, false, true, NetworkKind.Ethernet, 0, null);

    /// <summary>얼굴이 반응할 정도로 배터리가 부족한지 (15% 이하이고 충전 중이 아님).</summary>
    public bool IsBatteryCritical => HasBattery && !IsCharging && !IsPluggedIn && BatteryPercent <= 15;

    /// <summary>상태 아이콘을 경고색으로 표시할 정도인지 (20% 이하이고 충전 중이 아님).</summary>
    public bool IsBatteryLow => HasBattery && !IsCharging && !IsPluggedIn && BatteryPercent <= 20;

    public bool IsOffline => Network == NetworkKind.Offline;
}
