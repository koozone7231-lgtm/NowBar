using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

public partial class MediaSessionChoice : ObservableObject
{
    public required string Key { get; init; }
    public required string AppName { get; init; }
    [ObservableProperty] private bool _isSelected;
}

public partial class MusicViewModel : ObservableObject, IDisposable
{
    private readonly MediaService _media;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _positionTimer;
    private MediaSessionData? _snapshot;
    private string? _thumbnailKey;
    private bool _isSeeking;
    /// <summary>
    /// 재생 위치 변경 후, 앱이 새 타임라인을 보고하기 전까지 사용할 위치.
    /// 일부 앱은 실제로 이동한 뒤에도 타임라인 갱신이 늦어 슬라이더가 이전 위치로 튀는 것을 막는다.
    /// </summary>
    private (TimeSpan Position, DateTimeOffset At, string Key)? _seekOverride;

    public ObservableCollection<MediaSessionChoice> Sessions { get; } = [];

    [ObservableProperty] private bool _isServiceAvailable = true;
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private string _appName = "";
    [ObservableProperty] private MediaPlaybackState _state;
    [ObservableProperty] private ImageSource? _thumbnail;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _sliderPosition;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private bool _canSeek;
    [ObservableProperty] private bool _canNext;
    [ObservableProperty] private bool _canPrevious;
    [ObservableProperty] private bool _canPlayPause;
    [ObservableProperty] private string? _message = "음악을 재생하고 있지 않습니다.";
    [ObservableProperty] private string? _commandError;

    public MusicViewModel(MediaService media, AppSettings settings)
    {
        _media = media;
        _settings = settings;
        _media.AutoSelect = settings.AutoSelectMediaSession;
        _media.Updated += (_, data) => Ui(() => Apply(data));
        _media.SessionsChanged += (_, _) => Ui(RefreshSessions);
        _settings.PropertyChanged += OnSettingsChanged;

        _positionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _positionTimer.Tick += (_, _) => UpdatePosition();
    }

    public bool IsPlaying => State == MediaPlaybackState.Playing;
    public string PlayPauseIcon => IsPlaying ? "Pause" : "Play";
    public bool ShowThumbnail => _settings.ShowAlbumArt && Thumbnail is not null;
    public bool ShowSessionPicker => _settings.AllowSessionPicker && Sessions.Count > 1;

    public string StatusText => !IsServiceAvailable ? "사용 불가"
        : !HasSession ? "재생 없음"
        : State switch
        {
            MediaPlaybackState.Playing => "재생 중",
            MediaPlaybackState.Paused => "일시정지",
            MediaPlaybackState.Stopped => "정지됨",
            MediaPlaybackState.Changing => "전환 중",
            MediaPlaybackState.Opened => "준비됨",
            MediaPlaybackState.Closed => "닫힘",
            _ => "상태 알 수 없음",
        };

    /// <summary>캡슐에 표시하는 짧은 상태.</summary>
    public string CompactText => !IsServiceAvailable ? "정보 없음"
        : !HasSession ? "재생 없음"
        : State switch
        {
            MediaPlaybackState.Playing => "재생 중",
            MediaPlaybackState.Paused => "일시정지",
            _ => "정지",
        };

    public string DisplayTitle => Title ?? (HasSession ? "제목 정보 없음" : "");
    public string DisplayArtist => Artist ?? "";

    public async Task InitializeAsync()
    {
        var ok = await _media.InitializeAsync();
        IsServiceAvailable = ok;
        if (!ok)
            Message = "현재 미디어 정보를 가져올 수 없습니다.";
        RefreshSessions();
    }

    private void Apply(MediaSessionData? data)
    {
        _snapshot = data;
        HasSession = data is not null;

        if (data is null)
        {
            Title = Artist = null;
            AppName = "";
            State = MediaPlaybackState.Unknown;
            Thumbnail = null;
            _thumbnailKey = null;
            DurationSeconds = 0;
            CanSeek = CanNext = CanPrevious = CanPlayPause = false;
            Message = IsServiceAvailable ? "음악을 재생하고 있지 않습니다." : "현재 미디어 정보를 가져올 수 없습니다.";
        }
        else if (data.ReadFailed)
        {
            AppName = data.AppName;
            Title = Artist = null;
            State = MediaPlaybackState.Unknown;
            CanSeek = CanNext = CanPrevious = CanPlayPause = false;
            Message = "현재 미디어 정보를 가져올 수 없습니다.";
        }
        else
        {
            Message = null;
            Title = data.Title;
            Artist = data.Artist;
            AppName = data.AppName;
            State = data.State;
            CanPlayPause = data.CanPlayPause;
            CanNext = data.CanNext;
            CanPrevious = data.CanPrevious;
            CanSeek = data.CanSeek;
            DurationSeconds = Math.Max(0, data.Duration.TotalSeconds);
            DurationText = FormatTime(data.Duration);
            if (data.MediaKey != _thumbnailKey)
            {
                _thumbnailKey = data.MediaKey;
                Thumbnail = CreateImage(data.ThumbnailBytes);
            }
        }

        UpdatePosition();
        _positionTimer.IsEnabled = data is not null && data.State == MediaPlaybackState.Playing && data.Duration > TimeSpan.Zero;
        RaiseDerived();
    }

    private void UpdatePosition()
    {
        if (_snapshot is null || _snapshot.ReadFailed)
        {
            if (!_isSeeking) SliderPosition = 0;
            PositionText = "0:00";
            DurationText = _snapshot is null ? "0:00" : DurationText;
            return;
        }
        if (_isSeeking) return;
        var pos = EstimatePosition(_snapshot, DateTimeOffset.Now);
        SliderPosition = pos.TotalSeconds;
        PositionText = FormatTime(pos);
    }

    private TimeSpan EstimatePosition(MediaSessionData snapshot, DateTimeOffset now)
    {
        if (_seekOverride is { } o)
        {
            if (o.Key == snapshot.Key && snapshot.TimelineUpdatedAt < o.At && now - o.At < TimeSpan.FromSeconds(30))
            {
                var pos = o.Position;
                if (snapshot.State == MediaPlaybackState.Playing)
                    pos += TimeSpan.FromTicks((long)((now - o.At).Ticks * (snapshot.PlaybackRate <= 0 ? 1 : snapshot.PlaybackRate)));
                return snapshot.Duration > TimeSpan.Zero && pos > snapshot.Duration ? snapshot.Duration : pos;
            }
            _seekOverride = null; // 앱이 새 위치를 보고했거나 곡이 바뀜
        }
        return snapshot.EstimatePosition(now);
    }

    private void RefreshSessions()
    {
        var list = _media.GetSessions();
        var current = _media.CurrentKey;
        Sessions.Clear();
        foreach (var s in list)
            Sessions.Add(new MediaSessionChoice { Key = s.Key, AppName = s.AppName, IsSelected = s.Key == current });
        OnPropertyChanged(nameof(ShowSessionPicker));
    }

    // ── 제어 명령 ────────────────────────────────────────
    [RelayCommand]
    private Task PlayPauseAsync() => Run(_media.TogglePlayPauseAsync, "재생/일시정지");

    [RelayCommand]
    private Task NextAsync() => Run(_media.SkipNextAsync, "다음 곡");

    [RelayCommand]
    private Task PreviousAsync() => Run(_media.SkipPreviousAsync, "이전 곡");

    [RelayCommand]
    private void OpenSourceApp()
    {
        if (!_media.TryOpenSourceApp()) CommandError = "재생 프로그램을 열 수 없습니다.";
    }

    [RelayCommand]
    private void SelectSession(MediaSessionChoice? choice)
    {
        if (choice is null) return;
        _media.SelectSession(choice.Key);
        foreach (var s in Sessions) s.IsSelected = s.Key == choice.Key;
    }

    public void BeginSeek()
    {
        if (CanSeek) _isSeeking = true;
    }

    public async Task CommitSeekAsync(double seconds)
    {
        if (!_isSeeking) return;
        _isSeeking = false;
        var target = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, DurationSeconds));
        PositionText = FormatTime(target);
        var ok = await _media.SeekAsync(target);
        CommandError = ok ? null : "이 프로그램은 재생 위치 변경을 허용하지 않습니다.";
        if (ok && _snapshot is not null)
            _seekOverride = (target, DateTimeOffset.Now, _snapshot.Key);
        UpdatePosition();
        await _media.RefreshAsync();
    }

    partial void OnSliderPositionChanged(double value)
    {
        if (_isSeeking) PositionText = FormatTime(TimeSpan.FromSeconds(value));
    }

    private async Task Run(Func<Task<bool>> action, string name)
    {
        var ok = await action();
        CommandError = ok ? null : $"{name} 명령을 이 프로그램이 지원하지 않습니다.";
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayPauseIcon));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CompactText));
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(DisplayArtist));
        OnPropertyChanged(nameof(ShowThumbnail));
    }

    partial void OnIsServiceAvailableChanged(bool value) => RaiseDerived();

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.AutoSelectMediaSession):
                _media.AutoSelect = _settings.AutoSelectMediaSession;
                break;
            case nameof(AppSettings.AllowSessionPicker):
                OnPropertyChanged(nameof(ShowSessionPicker));
                break;
            case nameof(AppSettings.ShowAlbumArt):
                OnPropertyChanged(nameof(ShowThumbnail));
                break;
        }
    }

    private static ImageSource? CreateImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Logger.Error("앨범 이미지 디코딩 실패", ex);
            return null;
        }
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { Logger.Error("음악 UI 갱신 실패", ex); }
        });
    }

    public void Dispose()
    {
        _positionTimer.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
