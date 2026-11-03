using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>
/// 도구 탭의 시스템 모니터.
/// 보일 때: 1초마다 CPU·메모리·네트워크, 2초마다 앱별 CPU, 10초마다 디스크.
/// 안 보일 때: 10초마다 CPU·메모리만 가볍게 확인해 얼굴 표정(버거움)에만 쓴다. 상태 표시를 끄면 이것도 멈춘다.
/// </summary>
public partial class SystemMonitorViewModel : ObservableObject, IDisposable
{
    public const int HistoryLength = 60;
    private static readonly TimeSpan HistoryGap = TimeSpan.FromSeconds(5);

    private readonly SystemMonitorService _service;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _fast;
    private readonly DispatcherTimer _slow;
    private readonly LoadTracker _load = new();
    private readonly List<double> _cpu = [];
    private readonly List<double> _memory = [];
    private DateTime _lastFastSample;
    private int _tick;
    private bool _active;

    [ObservableProperty] private double _cpuPercent;
    [ObservableProperty] private double _memoryPercent;
    [ObservableProperty] private string _memoryDetail = "";
    [ObservableProperty] private IReadOnlyList<double> _cpuHistory = [];
    [ObservableProperty] private IReadOnlyList<double> _memoryHistory = [];
    [ObservableProperty] private string _diskName = "C:";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskDetail = "";
    [ObservableProperty] private string _downText = "0 B/s";
    [ObservableProperty] private string _upText = "0 B/s";
    [ObservableProperty] private IReadOnlyList<AppUsage> _topApps = [];
    [ObservableProperty] private bool _isStrained;

    public string CpuText => $"{CpuPercent:0}%";
    public string MemoryText => $"{MemoryPercent:0}%";
    public string DiskText => $"{DiskPercent:0}%";
    public bool HasTopApps => TopApps.Count > 0;

    /// <summary>얼굴에 보여줄 문구 (CPU가 원인인지 메모리가 원인인지).</summary>
    public string StrainText => _load.IsCpuStrained ? $"CPU를 많이 쓰고 있어요 ({CpuPercent:0}%)" : $"메모리가 거의 찼어요 ({MemoryPercent:0}%)";

    partial void OnCpuPercentChanged(double value) => OnPropertyChanged(nameof(CpuText));
    partial void OnMemoryPercentChanged(double value) => OnPropertyChanged(nameof(MemoryText));
    partial void OnDiskPercentChanged(double value) => OnPropertyChanged(nameof(DiskText));
    partial void OnTopAppsChanged(IReadOnlyList<AppUsage> value) => OnPropertyChanged(nameof(HasTopApps));

    public SystemMonitorViewModel(SystemMonitorService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        _fast = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _fast.Tick += (_, _) => FastTick();
        _slow = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(10) };
        _slow.Tick += (_, _) => SlowTick();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ShowSystemStatus)) UpdateTimers();
        };
    }

    public void Start() => UpdateTimers();

    public void SetActive(bool active)
    {
        if (active == _active) return;
        _active = active;
        if (active)
        {
            if (DateTime.Now - _lastFastSample > HistoryGap)
            {
                // 오래 닫혀 있었으면 그래프의 시간축이 어긋나므로 새로 시작
                _cpu.Clear();
                _memory.Clear();
            }
            _tick = 0;
            FastTick();
        }
        UpdateTimers();
    }

    private void UpdateTimers()
    {
        if (_active) { _slow.Stop(); _fast.Start(); return; }
        _fast.Stop();
        if (_settings.ShowSystemStatus) _slow.Start();
        else
        {
            _slow.Stop();
            SetStrained(false);
        }
    }

    private void FastTick()
    {
        try
        {
            var s = _service.Sample();
            _lastFastSample = DateTime.Now;
            CpuPercent = s.CpuPercent;
            MemoryPercent = s.MemoryPercent;
            MemoryDetail = $"{SystemMonitorService.FormatGb(s.MemoryUsedBytes)} / {SystemMonitorService.FormatGb(s.MemoryTotalBytes)}";
            DownText = SystemMonitorService.FormatRate(s.DownBytesPerSec);
            UpText = SystemMonitorService.FormatRate(s.UpBytesPerSec);
            CpuHistory = Push(_cpu, s.CpuPercent);
            MemoryHistory = Push(_memory, s.MemoryPercent);
            TrackLoad(s.CpuPercent, s.MemoryPercent);

            if (_tick % 2 == 0) TopApps = _service.TopApps(2);
            if (_tick % 10 == 0 && SystemMonitorService.GetSystemDisk() is { } disk)
            {
                DiskName = disk.Name;
                DiskPercent = disk.UsedPercent;
                DiskDetail = $"{SystemMonitorService.FormatGb(disk.FreeBytes)} 남음";
            }
            _tick++;
        }
        catch (Exception ex)
        {
            Logger.Error("시스템 정보 읽기 실패", ex);
        }
    }

    private void SlowTick()
    {
        try
        {
            var (cpu, memory) = _service.SampleLight();
            TrackLoad(cpu, memory);
        }
        catch (Exception ex)
        {
            Logger.Error("시스템 정보 읽기 실패", ex);
        }
    }

    private void TrackLoad(double cpu, double memory)
    {
        if (!_settings.ShowSystemStatus) return;
        _load.Add(cpu, memory, DateTime.Now);
        SetStrained(_load.IsStrained);
    }

    private void SetStrained(bool value)
    {
        if (IsStrained == value) return;
        IsStrained = value;
        OnPropertyChanged(nameof(StrainText));
    }

    private static double[] Push(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
        return [.. list];
    }

    public void Dispose()
    {
        _fast.Stop();
        _slow.Stop();
    }
}
