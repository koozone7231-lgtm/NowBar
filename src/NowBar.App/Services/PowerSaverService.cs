using System.Runtime.InteropServices;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>절전 모드(Windows 에너지 절약/배터리 절약) 타일의 현재 상태.</summary>
public enum PowerSaverState { Missing, Off, On }

/// <summary>
/// Windows 절전 모드 켜기/끄기 — 설정 → 전원 및 배터리 → 절전 모드의 "항상 절전 모드 사용" 스위치와 똑같이 동작한다.
/// 공개 API가 없어서, 그 스위치가 바꾸는 Windows 알림 상태(WNF, 전원 관리자 소유)를 같은 값으로 쓴다:
/// 1 = 켜짐, 2 = 꺼짐. 일반 사용자 권한으로 쓸 수 있고, Windows 설정 화면도 바로 따라 바뀐다.
/// 자동 켜짐 충전 수준 등 다른 전원 설정은 건드리지 않는다.
/// </summary>
public sealed class PowerSaverService
{
    /// <summary>"항상 절전 모드 사용" 상태 (WNF_PO 소유, Windows 11 24H2 이후).</summary>
    private const ulong AlwaysOnStateName = 0x41C6013DA3BC3075;
    public const int ValueOn = 1, ValueOff = 2;

    /// <summary>스위치 값과 Windows가 알려 주는 상태(배터리 부족으로 자동으로 켜진 경우 포함)로 타일 상태를 정한다.</summary>
    public static PowerSaverState Interpret(int? switchValue, bool reportedOn)
    {
        if (switchValue is null) return PowerSaverState.Missing;
        return switchValue == ValueOn || reportedOn ? PowerSaverState.On : PowerSaverState.Off;
    }

    public PowerSaverState GetState() =>
        Interpret(ReadSwitch(), GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1);

    /// <summary>"항상 절전 모드 사용" 스위치 값. 이 Windows에 없으면 null.</summary>
    public int? ReadSwitch()
    {
        try
        {
            var name = AlwaysOnStateName;
            var buffer = new byte[16];
            var size = (uint)buffer.Length;
            if (NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref size) != 0) return null;
            // 한 번도 바뀐 적 없으면 데이터가 비어 있다 → 꺼짐(자동)
            return size >= 4 ? BitConverter.ToInt32(buffer, 0) : 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>켜기/끄기. 성공하면 true.</summary>
    public bool SetOn(bool on)
    {
        try
        {
            var name = AlwaysOnStateName;
            var data = BitConverter.GetBytes(on ? ValueOn : ValueOff);
            var status = NtUpdateWnfStateData(ref name, data, (uint)data.Length, IntPtr.Zero, IntPtr.Zero, 0, 0);
            if (status != 0) Logger.Info($"절전 모드 변경 거부됨 (0x{status:X8})");
            return status == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Error("절전 모드 변경 실패", ex);
            return false;
        }
    }

    // ── Win32 ────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("ntdll.dll")] private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope, out uint changeStamp, byte[] buffer, ref uint bufferSize);
    [DllImport("ntdll.dll")] private static extern int NtUpdateWnfStateData(ref ulong stateName, byte[] buffer, uint length, IntPtr typeId, IntPtr explicitScope, uint matchingChangeStamp, uint checkStamp);
}
