using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NowBar.App.Services;

/// <summary>한 번 잰 값. 처음 한 번은 비교할 이전 값이 없어 CPU·네트워크가 0이다.</summary>
public readonly record struct SystemSample(
    double CpuPercent,
    double MemoryPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double DownBytesPerSec,
    double UpBytesPerSec);

public readonly record struct DiskSample(string Name, double UsedPercent, long FreeBytes, long TotalBytes);

public readonly record struct AppUsage(string Name, double CpuPercent);

/// <summary>
/// CPU(GetSystemTimes 차이) · 메모리(GlobalMemoryStatusEx) · 디스크(DriveInfo) · 네트워크(어댑터 누적 바이트 차이) ·
/// 앱별 CPU(프로세스 CPU 시간 차이). 온도는 관리자 권한·제조사 드라이버가 필요해 다루지 않는다.
/// 호출하는 쪽이 주기를 정한다 (보일 때 1초, 접혀 있을 때 10초).
/// </summary>
public sealed class SystemMonitorService
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastDown, _lastUp;
    private long _lastNetTicks;
    private bool _hasCpuBase;

    private readonly Dictionary<int, (string Name, TimeSpan Cpu)> _lastProcCpu = [];
    private long _lastProcTicks;

    /// <summary>CPU·메모리만 (가벼운 확인용).</summary>
    public (double Cpu, double Memory) SampleLight()
    {
        var mem = GetMemory();
        return (SampleCpu(), mem.Percent);
    }

    public SystemSample Sample()
    {
        var cpu = SampleCpu();
        var mem = GetMemory();
        var (down, up) = SampleNetwork();
        return new SystemSample(cpu, mem.Percent, mem.Used, mem.Total, down, up);
    }

    private double SampleCpu()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return 0;
        ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);
        double result = 0;
        if (_hasCpuBase)
        {
            // 커널 시간에는 유휴 시간이 포함되어 있다
            var total = (kernel - _lastKernel) + (user - _lastUser);
            var idleDelta = idle - _lastIdle;
            if (total > 0) result = Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
        }
        (_lastIdle, _lastKernel, _lastUser, _hasCpuBase) = (idle, kernel, user, true);
        return result;
    }

    private static (double Percent, ulong Used, ulong Total) GetMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0) return (0, 0, 0);
        var used = status.ullTotalPhys - status.ullAvailPhys;
        return (used * 100.0 / status.ullTotalPhys, used, status.ullTotalPhys);
    }

    private (double Down, double Up) SampleNetwork()
    {
        long down = 0, up = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics();
                down += stats.BytesReceived;
                up += stats.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            return (0, 0);
        }

        var now = Stopwatch.GetTimestamp();
        (double, double) result = (0, 0);
        if (_lastNetTicks != 0)
        {
            var seconds = Stopwatch.GetElapsedTime(_lastNetTicks, now).TotalSeconds;
            // 어댑터가 사라지면 합계가 줄어들 수 있다 → 음수는 0
            if (seconds > 0) result = (Math.Max(0, down - _lastDown) / seconds, Math.Max(0, up - _lastUp) / seconds);
        }
        (_lastDown, _lastUp, _lastNetTicks) = (down, up, now);
        return result;
    }

    /// <summary>Windows가 설치된 드라이브.</summary>
    public static DiskSample? GetSystemDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;
            var used = drive.TotalSize - drive.TotalFreeSpace;
            return new DiskSample(root.TrimEnd('\\'), used * 100.0 / drive.TotalSize, drive.TotalFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 지난 호출 이후 CPU를 많이 쓴 앱 (같은 이름의 프로세스는 합친다. 예: 브라우저 여러 개).
    /// 권한 때문에 읽을 수 없는 시스템 프로세스는 건너뛴다.
    /// </summary>
    public IReadOnlyList<AppUsage> TopApps(int count)
    {
        var now = Stopwatch.GetTimestamp();
        var current = new Dictionary<int, (string, TimeSpan)>();
        var byName = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var elapsed = _lastProcTicks == 0 ? 0 : Stopwatch.GetElapsedTime(_lastProcTicks, now).TotalMilliseconds;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.Id is 0 or 4) continue; // 유휴·System
                    var name = p.ProcessName;
                    var cpu = p.TotalProcessorTime;
                    current[p.Id] = (name, cpu);
                    if (elapsed > 0 && _lastProcCpu.TryGetValue(p.Id, out var prev) && prev.Name == name)
                    {
                        var delta = (cpu - prev.Cpu).TotalMilliseconds;
                        if (delta > 0) byName[name] = byName.GetValueOrDefault(name) + delta;
                    }
                }
                catch (Exception)
                {
                    // 접근 거부 / 이미 종료된 프로세스
                }
            }
        }

        _lastProcCpu.Clear();
        foreach (var (pid, v) in current) _lastProcCpu[pid] = v;
        _lastProcTicks = now;

        if (elapsed <= 0) return [];
        var cores = Environment.ProcessorCount;
        return byName
            .Select(kv => new AppUsage(kv.Key, Math.Min(100, kv.Value * 100.0 / (elapsed * cores))))
            .Where(a => a.CpuPercent >= 0.1)
            .OrderByDescending(a => a.CpuPercent)
            .Take(count)
            .ToList();
    }

    public static string FormatRate(double bytesPerSec) => bytesPerSec switch
    {
        < 1024 => $"{bytesPerSec:0} B/s",
        < 1024 * 1024 => $"{bytesPerSec / 1024:0} KB/s",
        < 1024 * 1024 * 1024 => $"{bytesPerSec / (1024 * 1024):0.0} MB/s",
        _ => $"{bytesPerSec / (1024 * 1024 * 1024):0.0} GB/s",
    };

    public static string FormatGb(double bytes) => $"{bytes / (1024d * 1024 * 1024):0.#} GB";

    private static ulong ToUlong(FILETIME ft) => ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

/// <summary>
/// 얼굴이 "버거워하는" 상태인지 판단 (순수 로직, 테스트 가능).
/// CPU 90% 이상 또는 메모리 95% 이상이 30초 넘게 이어지면 부담 상태.
/// (메모리 8GB PC는 평소에도 90% 안팎이라 메모리 기준은 더 높게 잡는다)
/// 조금 내려가도 바로 풀리지 않도록 CPU는 80%, 메모리는 90% 아래로 내려가야 풀린다.
/// </summary>
public sealed class LoadTracker
{
    public const double CpuHigh = 90, CpuRelease = 80, MemoryHigh = 95, MemoryRelease = 90;
    public static readonly TimeSpan Sustain = TimeSpan.FromSeconds(30);

    private readonly Signal _cpu = new(CpuHigh, CpuRelease);
    private readonly Signal _memory = new(MemoryHigh, MemoryRelease);

    public bool IsStrained { get; private set; }
    public bool IsCpuStrained => _cpu.Strained;

    /// <summary>값을 넣고, 부담 상태가 바뀌었으면 true.</summary>
    public bool Add(double cpu, double memory, DateTime now)
    {
        _cpu.Add(cpu, now);
        _memory.Add(memory, now);
        var strained = _cpu.Strained || _memory.Strained;
        if (strained == IsStrained) return false;
        IsStrained = strained;
        return true;
    }

    /// <summary>high 이상이 Sustain 동안 이어지면 켜지고, release 아래로 내려가면 꺼진다.</summary>
    private sealed class Signal(double high, double release)
    {
        private DateTime? _highSince;
        public bool Strained { get; private set; }

        public void Add(double value, DateTime now)
        {
            if (value >= high) _highSince ??= now;
            else if (value < release || !Strained) _highSince = null;

            if (_highSince is { } since && now - since >= Sustain) Strained = true;
            else if (_highSince is null) Strained = false;
        }
    }
}
