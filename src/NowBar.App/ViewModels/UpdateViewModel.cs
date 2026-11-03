using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>
/// 자동 업데이트: 시작 30초 뒤와 6시간마다 확인 → (자동 설치가 켜져 있으면) 받아서 검증 →
/// 사용자가 NowBar를 쓰고 있지 않을 때 교체 후 재시작.
/// </summary>
public partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly UpdateService _service;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _checkTimer;
    private readonly DispatcherTimer _installRetry;
    private string? _downloadedPath;

    /// <summary>지금 재시작해도 되는지 (패널을 쓰고 있지 않은지) — App이 제공.</summary>
    public Func<bool> IsIdle { get; set; } = () => true;

    /// <summary>파일을 교체하고 재시작 (App이 제공). 설정 저장 확인 등으로 취소되면 false.</summary>
    public Func<string, bool>? ApplyAndRestart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(LatestVersionText))]
    [NotifyCanExecuteChangedFor(nameof(InstallNowCommand))]
    private UpdateInfo? _available;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(InstallNowCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;

    public UpdateViewModel(UpdateService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        _checkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _checkTimer.Tick += async (_, _) =>
        {
            _checkTimer.Interval = TimeSpan.FromHours(6);
            if (_settings.AutoCheckUpdates) await CheckAsync(userInitiated: false);
        };
        _installRetry = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _installRetry.Tick += (_, _) => TryInstallWhenIdle();
        _settings.PropertyChanged += OnSettingsChanged;
        StatusText = "아직 확인하지 않았습니다";
    }

    public string CurrentVersionText => $"v{UpdateService.CurrentVersion.ToString(3)}";
    public string InstallKindText => UpdateService.DetectInstallKind() == InstallKind.SingleFile ? "단일 실행 파일" : "폴더 설치 (.NET 런타임)";
    public bool HasUpdate => Available is not null;
    public string LatestVersionText => Available is { } a ? $"v{a.Version.ToString(3)}" : "";

    public void Start() => _checkTimer.Start();

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => CheckAsync(userInitiated: true);

    private bool CanCheck() => !IsBusy;

    private async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusText = "업데이트 확인 중…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await _service.CheckAsync(cts.Token);
            if (info is null)
            {
                Available = null;
                StatusText = $"최신 버전입니다 · {DateTime.Now:HH:mm} 확인";
                return;
            }

            if (Available?.Version != info.Version) _downloadedPath = null;
            Available = info;
            StatusText = $"새 버전 v{info.Version.ToString(3)}이 있습니다";
            Logger.Info($"새 버전 발견: {info.Tag}");

            if (_settings.AutoInstallUpdates && !userInitiated)
            {
                IsBusy = false;
                await DownloadAsync();
                TryInstallWhenIdle();
            }
        }
        catch (Exception ex)
        {
            StatusText = userInitiated ? "업데이트를 확인할 수 없습니다 (인터넷 연결 확인)" : StatusText;
            Logger.Error("업데이트 확인 실패", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallNowAsync()
    {
        if (await DownloadAsync()) Install();
    }

    private bool CanInstall() => !IsBusy && HasUpdate;

    private async Task<bool> DownloadAsync()
    {
        if (Available is not { } info) return false;
        if (_downloadedPath is not null && File.Exists(_downloadedPath)) return true;
        IsBusy = true;
        try
        {
            var kind = UpdateService.DetectInstallKind();
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                StatusText = $"v{info.Version.ToString(3)} 받는 중… {p:P0}";
            });
            _downloadedPath = await _service.DownloadAsync(info, kind, progress);
            StatusText = $"v{info.Version.ToString(3)} 설치 준비 완료 (확인됨)";
            return true;
        }
        catch (Exception ex)
        {
            _downloadedPath = null;
            StatusText = ex is InvalidOperationException ? ex.Message : "업데이트 파일을 받지 못했습니다.";
            Logger.Error("업데이트 다운로드 실패", ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>자동 설치: 사용 중이 아닐 때만 교체·재시작, 아니면 1분 뒤 다시 시도.</summary>
    private void TryInstallWhenIdle()
    {
        if (_downloadedPath is null || !_settings.AutoInstallUpdates)
        {
            _installRetry.Stop();
            return;
        }
        if (!IsIdle())
        {
            _installRetry.Start();
            return;
        }
        _installRetry.Stop();
        Install();
    }

    private void Install()
    {
        if (_downloadedPath is null || ApplyAndRestart is null) return;
        try
        {
            StatusText = "설치 중… NowBar가 잠시 후 다시 시작됩니다";
            if (!ApplyAndRestart(_downloadedPath)) StatusText = "업데이트를 취소했습니다";
        }
        catch (UnauthorizedAccessException)
        {
            StatusText = "설치 폴더에 쓸 권한이 없습니다. 릴리스 페이지에서 직접 받아 주세요.";
        }
        catch (Exception ex)
        {
            StatusText = "설치에 실패해 원래 버전을 유지했습니다.";
            Logger.Error("업데이트 설치 실패", ex);
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = Available?.PageUrl ?? UpdateService.ReleasesPage;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Logger.Error("릴리스 페이지 열기 실패", ex); }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.AutoInstallUpdates) && _settings.AutoInstallUpdates && _downloadedPath is not null)
            TryInstallWhenIdle();
    }

    public void Dispose()
    {
        _checkTimer.Stop();
        _installRetry.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
