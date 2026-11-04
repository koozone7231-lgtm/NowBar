using System.Runtime.InteropServices;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>절전 모드(Windows 에너지 절약/배터리 절약) 타일의 현재 상태.</summary>
public enum PowerSaverState { Missing, Off, On }

/// <summary>
/// Windows 절전 모드 켜기/끄기.
/// 공개된 "지금 켜기" API가 없어서, Windows 설정의 "절전 모드 자동 켜기: 항상"과 같은 방법을 쓴다:
/// 현재 전원 구성표의 절전 모드 충전 수준(ESBATTTHRESHOLD)을 100%로 두면 항상 켜진다.
/// 켤 때 원래 값을 기억해 두었다가 끌 때 그대로 되돌린다 (다른 전원 설정은 건드리지 않는다).
/// </summary>
public sealed class PowerSaverService
{
    /// <summary>"항상"을 뜻하는 충전 수준.</summary>
    public const int Always = 100;
    /// <summary>되돌릴 값을 모를 때 쓰는 Windows 기본값 (전원 연결 시 끔, 배터리 20%).</summary>
    public const int DefaultAc = 0, DefaultDc = 20;

    private static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid EsBattThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

    public readonly record struct Thresholds(int Ac, int Dc)
    {
        /// <summary>NowBar(또는 Windows 설정의 "항상")가 켜 둔 상태.</summary>
        public bool IsAlways => Ac >= Always || Dc >= Always;
    }

    /// <summary>타일을 눌렀을 때 할 일.</summary>
    public abstract record Plan
    {
        /// <summary>켜기: 원래 값을 기억하고 100%로.</summary>
        public sealed record TurnOn(Thresholds Remember) : Plan;
        /// <summary>끄기: 이 값으로 되돌린다.</summary>
        public sealed record TurnOff(Thresholds Restore) : Plan;
        /// <summary>배터리가 부족해서 Windows가 켠 상태 → NowBar가 끄지 않는다.</summary>
        public sealed record HeldByWindows(int Threshold) : Plan;
    }

    /// <param name="isOn">지금 실제로 켜져 있는지</param>
    /// <param name="current">현재 충전 수준 설정</param>
    /// <param name="remembered">NowBar가 켤 때 기억해 둔 원래 값 (없으면 null)</param>
    public static Plan Decide(bool isOn, Thresholds current, Thresholds? remembered)
    {
        if (current.IsAlways)
        {
            // 항상 켜짐으로 설정돼 있으면 끈다 (실제 상태 반영이 늦더라도 설정이 기준)
            var restore = remembered is { IsAlways: false } r
                ? r
                : new Thresholds(current.Ac >= Always ? DefaultAc : current.Ac, current.Dc >= Always ? DefaultDc : current.Dc);
            return new Plan.TurnOff(restore);
        }
        if (isOn) return new Plan.HeldByWindows(current.Dc);
        return new Plan.TurnOn(current);
    }

    // ── 읽기 ─────────────────────────────────────────────
    public PowerSaverState GetState()
    {
        if (ReadThresholds() is null) return PowerSaverState.Missing;
        return GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1 ? PowerSaverState.On : PowerSaverState.Off;
    }

    public Thresholds? ReadThresholds()
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0) return null;
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
                var sub = SubEnergySaver;
                var setting = EsBattThreshold;
                if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var ac) != 0) return null;
                if (PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var dc) != 0) return null;
                return new Thresholds((int)ac, (int)dc);
            }
            finally
            {
                LocalFree(schemePtr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // ── 쓰기 ─────────────────────────────────────────────
    /// <summary>현재 전원 구성표의 충전 수준을 바꾸고 바로 적용한다. 성공하면 true.</summary>
    public bool WriteThresholds(Thresholds value)
    {
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0) return false;
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
                var sub = SubEnergySaver;
                var setting = EsBattThreshold;
                var ok = PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)Math.Clamp(value.Ac, 0, Always)) == 0
                      && PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)Math.Clamp(value.Dc, 0, Always)) == 0;
                // 같은 구성표를 다시 활성화해야 바뀐 값이 즉시 적용된다
                return PowerSetActiveScheme(IntPtr.Zero, ref scheme) == 0 && ok;
            }
            finally
            {
                LocalFree(schemePtr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Logger.Error("절전 모드 설정 실패", ex);
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
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootKey, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr rootKey, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}
