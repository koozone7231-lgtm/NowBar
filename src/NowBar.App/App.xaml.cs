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
    private ClaudeHookServer? _hookServer;
    private ClaudeHooksSetupService? _hooksSetup;
    private TrayIconService? _tray;
    private MainViewModel? _main;
    private MainWindow? _window;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;
    private bool _syncingStartup;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

            var weather = new WeatherViewModel(_weatherService, settings, OpenSettings);
            var music = new MusicViewModel(_mediaService, settings);
            var ai = new AIActivityViewModel(_hookServer, _hooksSetup, settings, OpenSettings);
            _main = new MainViewModel(settings, weather, music, ai);
            _main.OpenSettingsRequested += (_, _) => OpenSettings();

            ApplyCommandLine(e.Args, _main);

            _window = new MainWindow(_main);
            _window.Show();

            _tray = new TrayIconService(
                togglePinnedExpand: () => _main.IsPinned = !_main.IsPinned,
                toggleVisible: ToggleVisible,
                openSettings: OpenSettings,
                exit: ExitApp);

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

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
    /// 선택 옵션: --tab weather|music|ai (시작 탭), --pinned (확장·고정 상태로 시작).
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
                        _ => NowBarTab.Weather,
                    };
                    break;
                case "--pinned":
                    main.IsPinned = true;
                    break;
            }
        }
    }

    private static async Task Safe(Task task, string name)
    {
        try { await task; }
        catch (Exception ex) { Logger.Error($"{name} 실패", ex); }
    }

    public void OpenSettings()
    {
        if (_main is null || _settingsService is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        // 설정 창이 열려 있는 동안은 [저장]할 때만 파일에 기록 (편집 세션)
        _settingsService.BeginEdit();
        var vm = new SettingsViewModel(_settingsService, _main.AI, _weatherService!, _hookServer!, _hooksSetup!, _startupService!);
        _settingsWindow = new SettingsWindow(vm);
        _settingsWindow.Closed += (_, _) =>
        {
            // 창의 Closing에서 저장/되돌리기를 이미 결정했으므로 남은 변경은 없다
            _settingsService.EndEdit(save: false);
            vm.Dispose();
            _settingsWindow = null;
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
        _exiting = true;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _settingsWindow?.Close();
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
            if (registered is not null && registered != StartupService.BuildCommand(StartupService.CurrentExePath))
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
