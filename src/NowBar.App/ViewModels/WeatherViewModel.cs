using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

public partial class WeatherViewModel : ObservableObject, IDisposable
{
    private readonly WeatherService _service;
    private readonly AppSettings _settings;
    private readonly Action _openSettings;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData), nameof(CompactText), nameof(IconKind), nameof(TemperatureText),
        nameof(ConditionText), nameof(FeelsLikeText), nameof(HumidityText), nameof(PrecipitationText),
        nameof(PrecipitationLabel), nameof(MaxText), nameof(MinText), nameof(UpdatedText), nameof(ShowPlaceholder))]
    private WeatherData? _data;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatedText), nameof(HasError), nameof(PlaceholderText))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderText), nameof(UpdatedText))]
    private bool _isLoading;

    public WeatherViewModel(WeatherService service, AppSettings settings, Action openSettings)
    {
        _service = service;
        _settings = settings;
        _openSettings = openSettings;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(settings.WeatherRefreshMinutes) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _settings.PropertyChanged += OnSettingsChanged;
    }

    public bool HasLocation => _settings.Location is not null;
    public bool HasData => Data is not null && HasLocation;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public bool ShowPlaceholder => HasLocation && Data is null;

    public string LocationText => _settings.Location?.DisplayName ?? "지역 미설정";

    public string CompactText =>
        !HasLocation ? "지역 설정" : Data is null ? "--°" : $"{Math.Round(Data.Temperature):0}°";

    public string IconKind => Data is null ? "Thermometer" : WeatherCodes.IconKind(Data.WeatherCode, Data.IsDay);

    public string TemperatureText => Data is null ? "--" : $"{Math.Round(Data.Temperature):0}{UnitSuffix}";

    public string ConditionText => Data is null ? "" : WeatherCodes.Describe(Data.WeatherCode);

    public string FeelsLikeText =>
        Data?.ApparentTemperature is { } t ? $"체감 {Math.Round(t):0}°" : "체감 정보 없음";

    public string HumidityText => Data?.Humidity is { } h ? $"{h}%" : "--";

    public string PrecipitationLabel =>
        Data?.PrecipitationProbability is null && Data?.PrecipitationProbabilityMax is not null ? "강수(오늘 최대)" : "강수 확률";

    public string PrecipitationText =>
        Data?.PrecipitationProbability is { } p ? $"{p}%"
        : Data?.PrecipitationProbabilityMax is { } pm ? $"{pm}%"
        : "--";

    public string MaxText => Data?.TodayMax is { } v ? $"{Math.Round(v):0}°" : "--";
    public string MinText => Data?.TodayMin is { } v ? $"{Math.Round(v):0}°" : "--";

    public string UpdatedText
    {
        get
        {
            if (IsLoading && Data is null) return "불러오는 중…";
            if (Data is null) return HasError ? "업데이트 실패" : "";
            var time = Data.FetchedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
            return HasError ? $"업데이트 실패 · {time} 데이터" : $"{time} 업데이트";
        }
    }

    public string PlaceholderText =>
        IsLoading ? "날씨 정보를 불러오는 중…" : ErrorMessage ?? "날씨 정보가 아직 없습니다.";

    private string UnitSuffix => _settings.TemperatureUnit == TemperatureUnit.Fahrenheit ? "°F" : "°C";

    public async Task InitializeAsync()
    {
        // 마지막으로 성공한 데이터를 먼저 표시 (오프라인 시작 대비)
        var cached = _service.LoadCache();
        if (cached is not null && cached.Matches(_settings.Location, _settings.TemperatureUnit))
            Data = cached;

        await RefreshAsync();
        _timer.Start();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var location = _settings.Location;
        if (location is null)
        {
            Data = null;
            ErrorMessage = null;
            NotifyLocation();
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        IsLoading = true;
        try
        {
            var data = await _service.GetWeatherAsync(location, _settings.TemperatureUnit, cts.Token);
            if (cts != _cts) return; // 더 새로운 요청이 진행 중
            Data = data;
            ErrorMessage = null;
            _service.SaveCache(data);
        }
        catch (OperationCanceledException) when (cts != _cts)
        {
            // 새 요청으로 대체됨
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or WeatherApiException)
        {
            // 마지막 성공 데이터는 유지하고 오류만 표시
            ErrorMessage = ex switch
            {
                WeatherApiException api => api.Message,
                OperationCanceledException => "날씨 서버 응답 시간이 초과되었습니다.",
                _ => "네트워크에 연결할 수 없습니다.",
            };
            if (Data is not null && !Data.Matches(location, _settings.TemperatureUnit)) Data = null;
            Logger.Error("날씨 업데이트 실패", ex);
        }
        catch (Exception ex)
        {
            ErrorMessage = "날씨 정보를 처리하지 못했습니다.";
            Logger.Error("날씨 업데이트 중 예외", ex);
        }
        finally
        {
            if (cts == _cts) IsLoading = false;
            NotifyLocation();
        }
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    private void NotifyLocation()
    {
        OnPropertyChanged(nameof(HasLocation));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(LocationText));
        OnPropertyChanged(nameof(CompactText));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    private async void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.Location):
                case nameof(AppSettings.TemperatureUnit):
                    if (Data is not null && !Data.Matches(_settings.Location, _settings.TemperatureUnit)) Data = null;
                    NotifyLocation();
                    await RefreshAsync();
                    break;
                case nameof(AppSettings.WeatherRefreshMinutes):
                    _timer.Interval = TimeSpan.FromMinutes(_settings.WeatherRefreshMinutes);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("날씨 설정 변경 처리 실패", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
