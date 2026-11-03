using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Services;
using Windows.Devices.Radios;

namespace NowBar.App.ViewModels;

/// <summary>
/// 도구 탭의 빠른 설정 (2줄 타일).
/// 1줄: Wi-Fi · 블루투스 · 마이크 · 다크 모드 / 2줄: 전원 모드 · 밝기(또는 볼륨) · 잠금
/// 보일 때만 3초마다 다시 읽어 다른 곳에서 바뀐 상태를 반영한다.
/// </summary>
public partial class QuickSettingsViewModel : ObservableObject, IDisposable
{
    private readonly QuickSettingsService _service;
    private readonly AudioMixerService _mixer;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _sliderDebounce;
    private bool _refreshing;
    private bool _syncingSlider;
    private DateTime _lastSliderChange;

    [ObservableProperty] private RadioToggle _wifi = RadioToggle.Missing;
    [ObservableProperty] private RadioToggle _bluetooth = RadioToggle.Missing;
    [ObservableProperty] private bool? _micMuted;
    [ObservableProperty] private bool _darkMode;
    [ObservableProperty] private PowerMode _powerMode = PowerMode.Unavailable;

    /// <summary>true면 슬라이더가 화면 밝기, false면 (밝기를 지원하지 않는 모니터) 전체 볼륨.</summary>
    [ObservableProperty] private bool _brightnessSupported;
    [ObservableProperty] private double _sliderValue;
    [ObservableProperty] private string? _message;

    public QuickSettingsViewModel(QuickSettingsService service, AudioMixerService mixer)
    {
        _service = service;
        _mixer = mixer;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _sliderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _sliderDebounce.Tick += async (_, _) => { _sliderDebounce.Stop(); await ApplySliderAsync(); };
    }

    // ── 타일 표시 ────────────────────────────────────────
    public bool WifiOn => Wifi == RadioToggle.On;
    public bool WifiAvailable => Wifi != RadioToggle.Missing;
    public string WifiText => Wifi switch { RadioToggle.On => "켜짐", RadioToggle.Off => "꺼짐", _ => "없음" };

    public bool BluetoothOn => Bluetooth == RadioToggle.On;
    public bool BluetoothAvailable => Bluetooth != RadioToggle.Missing;
    public string BluetoothText => Bluetooth switch { RadioToggle.On => "켜짐", RadioToggle.Off => "꺼짐", _ => "없음" };

    public bool MicIsMuted => MicMuted == true;
    /// <summary>다른 타일과 같은 규칙: 마이크가 켜져(음소거 아님) 있으면 강조색.</summary>
    public bool MicOn => MicMuted == false;
    public bool MicAvailable => MicMuted is not null;
    public string MicIcon => MicMuted == true ? "MicOff" : "Mic";
    public string MicText => MicMuted switch { true => "음소거", false => "켜짐", _ => "없음" };

    public string DarkText => DarkMode ? "켜짐" : "꺼짐";

    public bool PowerAvailable => PowerMode != PowerMode.Unavailable;
    public bool PowerIsPerformance => PowerMode == PowerMode.Performance;
    public string PowerText => PowerMode switch
    {
        PowerMode.Efficiency => "효율",
        PowerMode.Balanced => "균형",
        PowerMode.Performance => "성능",
        _ => "지원 안 함",
    };

    public string SliderLabel => BrightnessSupported ? "밝기" : "볼륨";
    public string SliderIcon => BrightnessSupported ? "Sun" : "Volume";
    public string SliderText => $"{Math.Round(SliderValue):0}%";

    partial void OnWifiChanged(RadioToggle value) { OnPropertyChanged(nameof(WifiOn)); OnPropertyChanged(nameof(WifiAvailable)); OnPropertyChanged(nameof(WifiText)); }
    partial void OnBluetoothChanged(RadioToggle value) { OnPropertyChanged(nameof(BluetoothOn)); OnPropertyChanged(nameof(BluetoothAvailable)); OnPropertyChanged(nameof(BluetoothText)); }
    partial void OnMicMutedChanged(bool? value) { OnPropertyChanged(nameof(MicIsMuted)); OnPropertyChanged(nameof(MicOn)); OnPropertyChanged(nameof(MicAvailable)); OnPropertyChanged(nameof(MicIcon)); OnPropertyChanged(nameof(MicText)); }
    partial void OnDarkModeChanged(bool value) => OnPropertyChanged(nameof(DarkText));
    partial void OnPowerModeChanged(PowerMode value) { OnPropertyChanged(nameof(PowerAvailable)); OnPropertyChanged(nameof(PowerIsPerformance)); OnPropertyChanged(nameof(PowerText)); }
    partial void OnBrightnessSupportedChanged(bool value) { OnPropertyChanged(nameof(SliderLabel)); OnPropertyChanged(nameof(SliderIcon)); }

    partial void OnSliderValueChanged(double value)
    {
        OnPropertyChanged(nameof(SliderText));
        if (_syncingSlider) return;
        _lastSliderChange = DateTime.Now;
        // 드래그 중에는 값이 계속 바뀌므로 잠시 멈췄을 때 한 번만 적용
        _sliderDebounce.Stop();
        _sliderDebounce.Start();
    }

    // ── 보일 때만 갱신 ───────────────────────────────────
    public void SetActive(bool active)
    {
        if (active == _poll.IsEnabled) return;
        if (active)
        {
            _ = RefreshAsync();
            _poll.Start();
        }
        else
        {
            _poll.Stop();
        }
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            Wifi = await _service.GetRadioAsync(RadioKind.WiFi);
            Bluetooth = await _service.GetRadioAsync(RadioKind.Bluetooth);
            MicMuted = _service.GetMicMuted();
            DarkMode = _service.IsDarkMode();
            PowerMode = _service.GetPowerMode();

            // WMI는 느릴 수 있어 백그라운드에서 읽는다
            var brightness = await Task.Run(_service.GetBrightness);
            BrightnessSupported = brightness is not null;
            if (DateTime.Now - _lastSliderChange > TimeSpan.FromSeconds(1.5))
            {
                var value = brightness ?? (_mixer.GetMaster() is { } master ? (int)Math.Round(master.Volume * 100) : 0);
                _syncingSlider = true;
                SliderValue = value;
                _syncingSlider = false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("빠른 설정 상태 읽기 실패", ex);
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ── 동작 ─────────────────────────────────────────────
    [RelayCommand]
    private async Task ToggleWifiAsync()
    {
        if (!WifiAvailable) return;
        Report(await _service.SetRadioAsync(RadioKind.WiFi, !WifiOn), "Wi-Fi");
        Wifi = await _service.GetRadioAsync(RadioKind.WiFi);
    }

    [RelayCommand]
    private async Task ToggleBluetoothAsync()
    {
        if (!BluetoothAvailable) return;
        Report(await _service.SetRadioAsync(RadioKind.Bluetooth, !BluetoothOn), "블루투스");
        Bluetooth = await _service.GetRadioAsync(RadioKind.Bluetooth);
    }

    [RelayCommand]
    private void ToggleMic()
    {
        if (!MicAvailable) return;
        Report(_service.SetMicMuted(!MicIsMuted), "마이크");
        MicMuted = _service.GetMicMuted();
    }

    [RelayCommand]
    private void ToggleDarkMode()
    {
        try
        {
            _service.SetDarkMode(!DarkMode);
            Message = null;
        }
        catch (Exception ex)
        {
            Message = "다크 모드를 바꾸지 못했습니다.";
            Logger.Error("다크 모드 변경 실패", ex);
        }
        DarkMode = _service.IsDarkMode();
    }

    [RelayCommand]
    private void CyclePowerMode()
    {
        if (!PowerAvailable) return;
        Report(_service.SetPowerMode(QuickSettingsService.Next(PowerMode)), "전원 모드");
        PowerMode = _service.GetPowerMode();
    }

    [RelayCommand]
    private void Lock()
    {
        if (!QuickSettingsService.Lock()) Message = "화면을 잠그지 못했습니다.";
    }

    private async Task ApplySliderAsync()
    {
        var value = (int)Math.Round(SliderValue);
        if (BrightnessSupported)
        {
            if (!await Task.Run(() => _service.SetBrightness(value))) Message = "밝기를 바꾸지 못했습니다.";
        }
        else
        {
            try { _mixer.SetMasterVolume(value / 100f); }
            catch (Exception ex) { Logger.Error("볼륨 변경 실패", ex); }
        }
    }

    private void Report(bool ok, string what) => Message = ok ? null : $"{what}을(를) 바꾸지 못했습니다. Windows 설정에서 허용 여부를 확인하세요.";

    public void Dispose()
    {
        _poll.Stop();
        _sliderDebounce.Stop();
    }
}
