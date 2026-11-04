using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Services;
using Windows.Devices.Radios;

namespace NowBar.App.ViewModels;

/// <summary>
/// 도구 탭의 빠른 설정.
/// 1줄: Wi-Fi · 블루투스 · 다크 모드 · 절전 모드 타일
/// 2줄: 밝기 슬라이더 · 잠금 / 3줄: 볼륨 슬라이더 · 믹서 열기
/// 보일 때만 3초마다 다시 읽어 다른 곳에서 바뀐 상태를 반영한다.
/// </summary>
public partial class QuickSettingsViewModel : ObservableObject, IDisposable
{
    private readonly QuickSettingsService _service;
    private readonly PowerSaverService _powerSaverService;
    private readonly Action _openMixer;
    private readonly DispatcherTimer _poll;
    private readonly DispatcherTimer _sliderDebounce;
    private bool _refreshing;
    private bool _syncingSlider;
    private DateTime _lastSliderChange;

    [ObservableProperty] private RadioToggle _wifi = RadioToggle.Missing;
    [ObservableProperty] private RadioToggle _bluetooth = RadioToggle.Missing;
    [ObservableProperty] private bool _darkMode;
    [ObservableProperty] private PowerSaverState _powerSaver = PowerSaverState.Missing;

    /// <summary>내장 디스플레이처럼 밝기를 바꿀 수 있는 화면인지 (외장 모니터는 대부분 지원 안 함).</summary>
    [ObservableProperty] private bool _brightnessSupported;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private string? _message;

    /// <summary>볼륨 줄은 믹서의 전체 볼륨·음소거를 그대로 쓴다.</summary>
    public AudioMixerViewModel Mixer { get; }

    public QuickSettingsViewModel(QuickSettingsService service, PowerSaverService powerSaver, AudioMixerViewModel mixer, Action openMixer)
    {
        _service = service;
        _powerSaverService = powerSaver;
        _openMixer = openMixer;
        Mixer = mixer;
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

    public string DarkText => DarkMode ? "켜짐" : "꺼짐";

    public bool PowerSaverOn => PowerSaver == PowerSaverState.On;
    public bool PowerSaverAvailable => PowerSaver != PowerSaverState.Missing;
    public string PowerSaverText => PowerSaver switch { PowerSaverState.On => "켜짐", PowerSaverState.Off => "꺼짐", _ => "없음" };

    public string BrightnessText => $"{Math.Round(Brightness):0}%";

    partial void OnWifiChanged(RadioToggle value) { OnPropertyChanged(nameof(WifiOn)); OnPropertyChanged(nameof(WifiAvailable)); OnPropertyChanged(nameof(WifiText)); }
    partial void OnBluetoothChanged(RadioToggle value) { OnPropertyChanged(nameof(BluetoothOn)); OnPropertyChanged(nameof(BluetoothAvailable)); OnPropertyChanged(nameof(BluetoothText)); }
    partial void OnDarkModeChanged(bool value) => OnPropertyChanged(nameof(DarkText));
    partial void OnPowerSaverChanged(PowerSaverState value) { OnPropertyChanged(nameof(PowerSaverOn)); OnPropertyChanged(nameof(PowerSaverAvailable)); OnPropertyChanged(nameof(PowerSaverText)); }

    partial void OnBrightnessChanged(double value)
    {
        OnPropertyChanged(nameof(BrightnessText));
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
            DarkMode = _service.IsDarkMode();
            PowerSaver = _powerSaverService.GetState();

            // WMI는 느릴 수 있어 백그라운드에서 읽는다
            var brightness = await Task.Run(_service.GetBrightness);
            BrightnessSupported = brightness is not null;
            if (brightness is { } value && DateTime.Now - _lastSliderChange > TimeSpan.FromSeconds(1.5))
            {
                _syncingSlider = true;
                Brightness = value;
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
    private void TogglePowerSaver()
    {
        if (!PowerSaverAvailable) return;
        var on = !PowerSaverOn;
        if (_powerSaverService.SetOn(on))
        {
            Message = null;
            Logger.Info(on ? "절전 모드 켬" : "절전 모드 끔");
        }
        else
        {
            Message = on ? "절전 모드를 켜지 못했습니다." : "절전 모드를 끄지 못했습니다.";
        }
        PowerSaver = _powerSaverService.GetState();
    }

    [RelayCommand]
    private void OpenMixer() => _openMixer();

    [RelayCommand]
    private void Lock()
    {
        if (!QuickSettingsService.Lock()) Message = "화면을 잠그지 못했습니다.";
    }

    private async Task ApplySliderAsync()
    {
        if (!BrightnessSupported) return;
        var value = (int)Math.Round(Brightness);
        if (!await Task.Run(() => _service.SetBrightness(value))) Message = "밝기를 바꾸지 못했습니다.";
    }

    private void Report(bool ok, string what) => Message = ok ? null : $"{what}을(를) 바꾸지 못했습니다. Windows 설정에서 허용 여부를 확인하세요.";

    public void Dispose()
    {
        _poll.Stop();
        _sliderDebounce.Stop();
    }
}
