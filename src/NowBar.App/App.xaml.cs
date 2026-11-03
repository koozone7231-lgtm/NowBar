using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;
using NowBar.App.ViewModels;
using NowBar.App.Views;

namespace NowBar.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private SettingsService? _settingsService;
    private StartupService? _startupService;
    private WeatherService? _weatherService;
    private MediaService? _mediaService;
    private AudioMixerService? _audioMixer;
    private QuickSettingsService? _quickSettings;
    private ClipboardService? _clipboard;
    private SystemStatusService? _systemStatus;
    private ClaudeHookServer? _hookServer;
    private ClaudeHooksSetupService? _hooksSetup;
    private TrayIconService? _tray;
    private MainViewModel? _main;
    private MainWindow? _window;
    private bool _exiting;
    private bool _syncingStartup;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 업데이트 직후: 이전 버전 프로세스가 완전히 끝난 뒤에 시작 (단일 실행 잠금을 넘겨받기 위해)
        var afterUpdate = WaitForPreviousVersion(e.Args);

        _mutex = new Mutex(true, @"Local\NowBar.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        // 예외가 발생해도 앱 전체가 종료되지 않도록 처리
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Logger.Error("처리되지 않은 예외", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Logger.Error("관찰되지 않은 작업 예외", a.Exception);
            a.SetObserved();
        };

        try
        {
            Logger.Info("NowBar 시작");
            try
            {
                if (AppPaths.MigrateLegacyData()) Logger.Info($"이전 TOPBAR 설정을 가져왔습니다: {AppPaths.LegacyDataDir}");
            }
            catch (Exception ex)
            {
                Logger.Error("이전 TOPBAR 설정 가져오기 실패", ex);
            }
            _settingsService = new SettingsService();
            var settings = _settingsService.Load();
            ThemeService.Apply(settings);

            _startupService = new StartupService();
            try
            {
                // 이전 이름의 자동 실행 항목은 새 이름으로 옮긴다
                if (StartupService.RemoveLegacyEntry()) _startupService.SetEnabled(true);
            }
            catch (Exception ex)
            {
                Logger.Error("이전 자동 실행 항목 정리 실패", ex);
            }
            SyncStartupFromRegistry(settings);
            settings.PropertyChanged += OnSettingsChanged;

            _weatherService = new WeatherService();
            _mediaService = new MediaService();
            _hookServer = new ClaudeHookServer(() => settings.HookToken);
            _hooksSetup = new ClaudeHooksSetupService();

            // 탭의 "설정 열기" 버튼은 해당 섹션을 바로 연다
            var weather = new WeatherViewModel(_weatherService, settings, () => OpenSettings(SettingsSection.Weather));
            _audioMixer = new AudioMixerService();
            _systemStatus = new SystemStatusService();
            var mixer = new AudioMixerViewModel(_audioMixer);
            var status = new SystemStatusViewModel(_systemStatus, settings);
            var music = new MusicViewModel(_mediaService, settings);
            _quickSettings = new QuickSettingsService();
            _clipboard = new ClipboardService();
            var systemMonitor = new SystemMonitorViewModel(new SystemMonitorService(), settings);
            var tools = new ToolsViewModel(mixer, new QuickSettingsViewModel(_quickSettings, _audioMixer),
                new ClipboardViewModel(_clipboard, settings), systemMonitor);
            var ai = new AIActivityViewModel(_hookServer, _hooksSetup, settings, () => OpenSettings(SettingsSection.AI));
            var update = new UpdateViewModel(new UpdateService(), settings);
            var settingsPanel = new SettingsViewModel(_settingsService, ai, _weatherService, _hookServer, _hooksSetup, _startupService, update);
            _main = new MainViewModel(_settingsService, weather, music, ai, settingsPanel, status, tools);
            status.Start();
            systemMonitor.Start();
            tools.Clipboard.ApplyEnabled();

            ApplyCommandLine(e.Args, _main);

            _window = new MainWindow(_main);
            _window.Show();

            _tray = new TrayIconService(
                togglePinnedExpand: () => _main.IsPinned = !_main.IsPinned,
                toggleVisible: ToggleVisible,
                openSettings: () => OpenSettings(),
                exit: ExitApp);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            // 자동 업데이트: 사용 중이 아닐 때만 교체·재시작
            UpdateService.CleanupOldFiles();
            update.IsIdle = () => !_main.IsExpanded && !_main.IsSettingsOpen && !_main.AI.IsWorking;
            update.ApplyAndRestart = ApplyUpdateAndRestart;
            update.Start();
            if (afterUpdate)
                _tray.ShowNotice("NowBar 업데이트 완료", $"v{UpdateService.CurrentVersion.ToString(3)}으로 업데이트했습니다.");

            await Task.WhenAll(
                Safe(weather.InitializeAsync(), "날씨 초기화"),
                Safe(music.InitializeAsync(), "미디어 초기화"),
                Safe(ai.InitializeAsync(), "Claude 연동 초기화"));
        }
        catch (Exception ex)
        {
            Logger.Error("시작 중 오류", ex);
            MessageBox.Show($"NowBar를 시작하는 중 오류가 발생했습니다.\n\n{ex.Message}\n\n로그: {AppPaths.LogFile}",
                "NowBar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 선택 옵션: --tab weather|music|ai|tools (시작 탭), --pinned (확장·고정 상태로 시작).
    /// --startup 은 Windows 자동 실행 시 붙는 표시용 인자다.
    /// </summary>
    private static void ApplyCommandLine(string[] args, MainViewModel main)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--tab" when i + 1 < args.Length:
                    main.SelectedTab = args[++i].ToLowerInvariant() switch
                    {
                        "music" => NowBarTab.Music,
                        "ai" => NowBarTab.AI,
                        "tools" => NowBarTab.Tools,
                        _ => NowBarTab.Weather,
                    };
                    break;
                case "--pinned":
                    main.IsPinned = true;
                    break;
            }
        }
    }

    /// <summary>--after-update {pid}: 이전 버전이 종료될 때까지(최대 15초) 기다린다.</summary>
    private static bool WaitForPreviousVersion(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals("--after-update", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return false;
        if (index + 1 < args.Length && int.TryParse(args[index + 1], out var pid))
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(pid);
                previous.WaitForExit(15000);
            }
            catch (ArgumentException)
            {
                // 이미 종료됨
            }
        }
        return true;
    }

    /// <summary>받은 업데이트로 파일을 교체하고 새 버전을 실행한 뒤 종료한다.</summary>
    private bool ApplyUpdateAndRestart(string downloadedPath)
    {
        // 저장하지 않은 설정이 있으면 먼저 물어보고, 취소하면 업데이트하지 않는다
        if (_main is not null && !_main.TryCloseSettings()) return false;

        var exe = UpdateService.Apply(downloadedPath, UpdateService.DetectInstallKind());
        Logger.Info("새 버전으로 재시작");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
        {
            Arguments = $"--after-update {Environment.ProcessId}",
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();
        ExitApp();
        return true;
    }

    private static async Task Safe(Task task, string name)
    {
        try { await task; }
        catch (Exception ex) { Logger.Error($"{name} 실패", ex); }
    }

    /// <summary>NowBar 패널을 키워 그 안에서 설정을 연다 (별도 창 없음).</summary>
    public void OpenSettings(SettingsSection? section = null)
    {
        if (_main is null || _window is null) return;
        if (_window.UserHidden) ToggleVisible();
        if (section is { } s) _main.SettingsPanel.Section = s;
        _main.ShowSettings();
    }

    private void ToggleVisible()
    {
        if (_window is null) return;
        _window.UserHidden = !_window.UserHidden;
        _tray?.SetVisibleState(!_window.UserHidden);
    }

    public async void ExitApp()
    {
        if (_exiting) return;
        // 저장하지 않은 설정이 있으면 먼저 물어보고, 취소하면 종료하지 않는다
        if (_main is not null && !_main.TryCloseSettings()) return;
        _exiting = true;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _main?.Update.Dispose();
            _main?.SettingsPanel.Dispose();
            _main?.Tools.Mixer.Dispose();
            _main?.Tools.QuickSettings.Dispose();
            _main?.Tools.System.Dispose();
            _clipboard?.Dispose();
            _main?.Status.Dispose();
            _systemStatus?.Dispose();
            _audioMixer?.Dispose();
            _window?.Close();
            _main?.Weather.Dispose();
            _main?.Music.Dispose();
            _main?.AI.Dispose();
            _mediaService?.Dispose();
            if (_hookServer is not null) await _hookServer.DisposeAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("종료 처리 중 오류", ex);
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _settingsService?.Dispose(); // 대기 중인 설정 저장
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
        }
        Logger.Info("NowBar 종료");
        base.OnExit(e);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AppSettings s) return;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
            case nameof(AppSettings.BackgroundOpacity):
            case nameof(AppSettings.AccentColor):
                ThemeService.Apply(s);
                break;
            case nameof(AppSettings.StartWithWindows) when !_syncingStartup:
                try
                {
                    _startupService?.SetEnabled(s.StartWithWindows);
                }
                catch (Exception ex)
                {
                    Logger.Error("시작 프로그램 등록 변경 실패", ex);
                    MessageBox.Show($"시작 프로그램 설정을 변경하지 못했습니다.\n{ex.Message}", "NowBar",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    SyncStartupFromRegistry(s);
                }
                break;
        }
    }

    /// <summary>레지스트리의 실제 등록 상태를 설정에 반영하고, 실행 파일 위치가 바뀌었으면 경로를 갱신한다.</summary>
    private void SyncStartupFromRegistry(AppSettings settings)
    {
        if (_startupService is null) return;
        _syncingStartup = true;
        try
        {
            var registered = _startupService.GetRegisteredCommand();
            settings.StartWithWindows = registered is not null;
            // 설치 위치가 바뀌었으면 경로를 갱신한다. 단, 별도 데이터 폴더로 띄운 개발/테스트 실행은 실제 등록 경로를 건드리지 않는다.
            var isIsolatedInstance = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NOWBAR_DATA_DIR"));
            if (registered is not null && !isIsolatedInstance
                && registered != StartupService.BuildCommand(StartupService.CurrentExePath))
                _startupService.SetEnabled(true);
        }
        catch (Exception ex)
        {
            Logger.Error("시작 프로그램 상태 확인 실패", ex);
        }
        finally
        {
            _syncingStartup = false;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // 절전 해제 후 오래된 날씨 갱신
        Dispatcher.InvokeAsync(async () =>
        {
            if (_main is not null) await Safe(_main.Weather.RefreshAsync(), "절전 해제 후 날씨 갱신");
        });
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Error("UI 스레드 예외", e.Exception);
        e.Handled = true;
    }
}
