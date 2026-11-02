using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>믹서의 앱 한 줄.</summary>
public partial class AudioAppItem : ObservableObject
{
    private readonly AudioMixerService _service;
    private bool _syncing;
    private DateTime _lastUserChange;

    public string Key { get; }
    public string Name { get; }
    public ImageSource? Icon { get; }
    public bool IsSystem { get; }

    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _isActive;

    public AudioAppItem(AudioMixerService service, AudioAppInfo info, ImageSource? icon)
    {
        _service = service;
        Key = info.Key;
        Name = info.Name;
        IsSystem = info.IsSystem;
        Icon = icon;
        Sync(info, force: true);
    }

    public string VolumeText => $"{Math.Round(Volume):0}";
    public string MuteIcon => Muted || Volume < 0.5 ? "VolumeX" : "Volume";

    /// <summary>바깥(Windows 믹서 등)에서 바뀐 값을 반영. 방금 사용자가 조절했으면 덮어쓰지 않는다.</summary>
    public void Sync(AudioAppInfo info, bool force = false)
    {
        IsActive = info.IsActive;
        if (!force && DateTime.Now - _lastUserChange < TimeSpan.FromSeconds(1.5)) return;
        _syncing = true;
        Volume = Math.Round(info.Volume * 100);
        Muted = info.Muted;
        _syncing = false;
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(MuteIcon));
        if (_syncing) return;
        _lastUserChange = DateTime.Now;
        Try(() => _service.SetAppVolume(Key, (float)(value / 100)));
    }

    partial void OnMutedChanged(bool value) => OnPropertyChanged(nameof(MuteIcon));

    [RelayCommand]
    private void ToggleMute()
    {
        _lastUserChange = DateTime.Now;
        Muted = !Muted;
        Try(() => _service.SetAppMute(Key, Muted));
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error("앱 볼륨 변경 실패", ex); }
    }
}

/// <summary>
/// 음악 탭의 오디오 믹서. 믹서가 화면에 보일 때만 1초마다 Windows 상태를 다시 읽는다(숨겨져 있으면 0).
/// </summary>
public partial class AudioMixerViewModel : ObservableObject, IDisposable
{
    private readonly AudioMixerService _service;
    private readonly DispatcherTimer _poll;
    private readonly Dictionary<string, ImageSource?> _icons = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncingMaster;
    private DateTime _lastMasterChange;

    public ObservableCollection<AudioAppItem> Apps { get; } = [];

    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private double _masterVolume;
    [ObservableProperty] private bool _masterMuted;

    public AudioMixerViewModel(AudioMixerService service)
    {
        _service = service;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += (_, _) => Refresh();
    }

    public string MasterVolumeText => $"{Math.Round(MasterVolume):0}";
    public string MasterIcon => MasterMuted || MasterVolume < 0.5 ? "VolumeX" : "Volume";
    public bool HasApps => Apps.Count > 0;

    /// <summary>믹서가 실제로 보일 때만 갱신 타이머를 돌린다.</summary>
    public void SetActive(bool active)
    {
        if (active == _poll.IsEnabled) return;
        if (active)
        {
            Refresh();
            _poll.Start();
        }
        else
        {
            _poll.Stop();
        }
    }

    public void Refresh()
    {
        try
        {
            var master = _service.GetMaster();
            IsAvailable = master is not null;
            if (master is null)
            {
                Apps.Clear();
                OnPropertyChanged(nameof(HasApps));
                return;
            }

            DeviceName = master.DeviceName;
            if (DateTime.Now - _lastMasterChange > TimeSpan.FromSeconds(1.5))
            {
                _syncingMaster = true;
                MasterVolume = Math.Round(master.Volume * 100);
                MasterMuted = master.Muted;
                _syncingMaster = false;
            }

            var apps = _service.GetApps();
            var keys = apps.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Apps.Count - 1; i >= 0; i--)
                if (!keys.Contains(Apps[i].Key)) Apps.RemoveAt(i);

            for (var i = 0; i < apps.Count; i++)
            {
                var info = apps[i];
                var existing = Apps.FirstOrDefault(a => string.Equals(a.Key, info.Key, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    Apps.Insert(Math.Min(i, Apps.Count), new AudioAppItem(_service, info, IconFor(info)));
                }
                else
                {
                    existing.Sync(info);
                    var index = Apps.IndexOf(existing);
                    if (index != i && i < Apps.Count) Apps.Move(index, i);
                }
            }
            OnPropertyChanged(nameof(HasApps));
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            Logger.Error("오디오 믹서 갱신 실패", ex);
        }
    }

    partial void OnMasterVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(MasterIcon));
        if (_syncingMaster) return;
        _lastMasterChange = DateTime.Now;
        try { _service.SetMasterVolume((float)(value / 100)); }
        catch (Exception ex) { Logger.Error("전체 볼륨 변경 실패", ex); }
    }

    partial void OnMasterMutedChanged(bool value) => OnPropertyChanged(nameof(MasterIcon));

    [RelayCommand]
    private void ToggleMasterMute()
    {
        _lastMasterChange = DateTime.Now;
        MasterMuted = !MasterMuted;
        try { _service.SetMasterMute(MasterMuted); }
        catch (Exception ex) { Logger.Error("전체 음소거 변경 실패", ex); }
    }

    /// <summary>앱 실행 파일의 아이콘 (한 번만 추출해 캐시).</summary>
    private ImageSource? IconFor(AudioAppInfo info)
    {
        if (info.IsSystem || string.IsNullOrEmpty(info.ExePath)) return null;
        if (_icons.TryGetValue(info.ExePath, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(info.ExePath);
            if (icon is not null)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                image = source;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("앱 아이콘 추출 실패", ex);
        }
        _icons[info.ExePath] = image;
        return image;
    }

    public void Dispose() => _poll.Stop();
}
