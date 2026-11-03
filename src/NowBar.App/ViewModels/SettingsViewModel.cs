using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>확장 패널 안 설정 화면의 섹션.</summary>
public enum SettingsSection { General, Design, Weather, Music, AI }

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly WeatherService _weather;
    private readonly ClaudeHookServer _server;
    private readonly ClaudeHooksSetupService _setup;
    private readonly StartupService _startup;

    /// <summary>settings.json 변경 승인 창을 띄우는 함수 (View가 제공). true = 승인.</summary>
    public Func<HooksPlan, string, bool>? ConfirmPlan { get; set; }

    public AppSettings Settings { get; }
    public AIActivityViewModel AI { get; }
    public IReadOnlyList<string> AccentPresets => ThemeService.AccentPresets;

    public ObservableCollection<GeoLocation> LocationResults { get; } = [];
    public ObservableCollection<string> SetupLog { get; } = [];

    [ObservableProperty] private string _locationQuery = "";
    [ObservableProperty] private GeoLocation? _selectedResult;
    [ObservableProperty] private string? _locationStatus;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isSetupRunning;
    [ObservableProperty] private string _manualTaskName = "";
    [ObservableProperty] private string _hookPortText;
    [ObservableProperty] private string? _startupStatus;

    private readonly SettingsService _settingsService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _hasUnsavedChanges;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveStatusText))]
    private DateTime? _lastSavedAt;

    public UpdateViewModel Update { get; }

    public SettingsViewModel(SettingsService settingsService, AIActivityViewModel ai, WeatherService weather,
        ClaudeHookServer server, ClaudeHooksSetupService setup, StartupService startup, UpdateViewModel update)
    {
        Update = update;
        _settingsService = settingsService;
        Settings = settingsService.Settings;
        AI = ai;
        _weather = weather;
        _server = server;
        _setup = setup;
        _startup = startup;
        _hookPortText = Settings.HookPort.ToString();
        Settings.PropertyChanged += OnSettingsChanged;
        _settingsService.UnsavedStateChanged += OnUnsavedStateChanged;
        UpdateStartupStatus();
    }

    // ── 섹션 / 열기·닫기 ─────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneralSection), nameof(IsDesignSection), nameof(IsWeatherSection),
        nameof(IsMusicSection), nameof(IsAISection))]
    private SettingsSection _section = SettingsSection.General;

    public bool IsGeneralSection => Section == SettingsSection.General;
    public bool IsDesignSection => Section == SettingsSection.Design;
    public bool IsWeatherSection => Section == SettingsSection.Weather;
    public bool IsMusicSection => Section == SettingsSection.Music;
    public bool IsAISection => Section == SettingsSection.AI;

    [RelayCommand]
    private void SelectSection(SettingsSection section) => Section = section;

    /// <summary>패널의 ✕ 버튼. 실제 닫기(저장 확인 포함)는 MainViewModel이 처리한다.</summary>
    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>설정 패널이 열릴 때마다 호출: 상태 문구 초기화.</summary>
    public void OnOpened()
    {
        LastSavedAt = null;
        HasUnsavedChanges = false;
        UpdateStartupStatus();
        HookPortText = Settings.HookPort.ToString();
    }

    // ── 저장 / 되돌리기 ──────────────────────────────────
    public string SaveStatusText =>
        HasUnsavedChanges ? "저장하지 않은 변경 사항이 있습니다 (미리보기 중)"
        : LastSavedAt is { } t ? $"저장되었습니다 · {t:HH:mm:ss}"
        : "모든 설정이 저장되어 있습니다";

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Save()
    {
        _settingsService.CommitEdit();
        LastSavedAt = DateTime.Now;
    }

    [RelayCommand(CanExecute = nameof(HasUnsavedChanges))]
    private void Revert()
    {
        _settingsService.RevertEdit();
        HookPortText = Settings.HookPort.ToString();
    }

    private void OnUnsavedStateChanged(object? sender, EventArgs e) =>
        HasUnsavedChanges = _settingsService.HasUnsavedChanges;

    public string HookUrl => ClaudeHookServer.BuildUrl(Settings.HookPort);
    public string MaskedToken => TokenGenerator.Mask(Settings.HookToken);
    public string ClaudeSettingsPath => _setup.SettingsPath;
    public string DataFolder => AppPaths.DataDir;
    public string CurrentLocationText => Settings.Location?.DisplayName ?? "설정된 지역 없음";

    // ── 날씨 ─────────────────────────────────────────────
    [RelayCommand]
    private async Task SearchLocationAsync()
    {
        var q = LocationQuery.Trim();
        if (q.Length < 2)
        {
            LocationStatus = "두 글자 이상 입력하세요.";
            return;
        }
        IsSearching = true;
        LocationStatus = "검색 중…";
        LocationResults.Clear();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var results = await _weather.SearchLocationsAsync(q, cts.Token);
            foreach (var r in results) LocationResults.Add(r);
            LocationStatus = results.Count == 0
                ? "검색 결과가 없습니다. 영문 이름(예: Seoul)이나 전체 이름(예: 서울특별시)으로 검색해 보세요."
                : $"{results.Count}개 지역을 찾았습니다. 목록에서 선택하세요.";
        }
        catch (Exception ex)
        {
            LocationStatus = ex is WeatherApiException ? ex.Message : "지역 검색에 실패했습니다. 인터넷 연결을 확인하세요.";
            Logger.Error("지역 검색 실패", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    partial void OnSelectedResultChanged(GeoLocation? value)
    {
        if (value is null) return;
        Settings.Location = value;
        LocationStatus = $"'{value.DisplayName}'(으)로 설정했습니다.";
    }

    [RelayCommand]
    private void ClearLocation()
    {
        Settings.Location = null;
        LocationStatus = "지역 설정을 지웠습니다.";
    }

    // ── 디자인/일반 ──────────────────────────────────────
    [RelayCommand]
    private void SetAccent(string? hex)
    {
        if (ThemeService.TryHex(hex) is not null) Settings.AccentColor = hex!;
    }

    [RelayCommand]
    private void ResetLayout()
    {
        Settings.TopOffset = 8;
        Settings.HorizontalOffset = 0;
        Settings.CompactWidth = 200;
        Settings.CapsuleHeight = 48;
        Settings.ExpandedWidth = 480;
        Settings.ExpandedHeight = 270;
        Settings.CornerRadius = 20;
        Settings.AnimationMs = 200;
        Settings.CollapseDelayMs = 500;
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.DataDir);

    private void UpdateStartupStatus()
    {
        try
        {
            var cmd = _startup.GetRegisteredCommand();
            StartupStatus = cmd is null ? "시작 프로그램에 등록되어 있지 않습니다." : $"등록됨: {cmd}";
        }
        catch (Exception ex)
        {
            StartupStatus = $"상태 확인 실패: {ex.Message}";
        }
    }

    // ── Claude Code 연동 ─────────────────────────────────
    [RelayCommand]
    private async Task RunClaudeSetupAsync()
    {
        if (IsSetupRunning) return;
        IsSetupRunning = true;
        SetupLog.Clear();
        try
        {
            // 1. 설치 및 설정 위치 확인
            var info = _setup.Detect();
            Log($"1. Claude Code 설정 파일: {info.SettingsPath}" + (info.SettingsExists ? "" : " (없음 → 새로 만듦)"));
            Log(info.CliPath is not null
                ? $"   Claude Code CLI 확인: {info.CliPath}"
                : "   Claude Code CLI를 PATH에서 찾지 못했습니다. 설치되어 있다면 그대로 진행해도 됩니다.");

            // 2. 로컬 서버 상태 확인
            if (!Settings.ClaudeIntegrationEnabled)
            {
                Settings.ClaudeIntegrationEnabled = true; // 설정 변경 처리기에서 서버 시작
                await Task.Delay(300);
            }
            if (!_server.IsRunning) await AI.RestartServerAsync();
            if (!_server.IsRunning)
            {
                Log($"2. 로컬 서버를 시작할 수 없습니다: {_server.LastError}");
                Log("→ 연동을 중단했습니다.");
                return;
            }
            Log($"2. 로컬 서버 실행 중: {HookUrl} (127.0.0.1 전용)");

            // 3. 인증 토큰
            if (string.IsNullOrWhiteSpace(Settings.HookToken)) Settings.HookToken = TokenGenerator.Create();
            Log($"3. 인증 토큰 준비됨 ({MaskedToken})");

            // 4~6. 백업 → 병합 → 저장 (사용자 승인 후)
            var plan = _setup.BuildInstallPlan(HookUrl, Settings.HookToken);
            foreach (var w in plan.Warnings) Log($"   주의: {w}");
            if (!plan.HasChanges)
            {
                Log("4~6. 이미 최신 NowBar Hooks 설정이 있어 파일을 변경하지 않았습니다.");
            }
            else
            {
                if (ConfirmPlan is null || !ConfirmPlan(plan, "Claude Code 연동 설정"))
                {
                    Log("→ 사용자가 변경을 취소했습니다. 설정 파일은 바뀌지 않았습니다.");
                    return;
                }
                var backup = _setup.Apply(plan);
                Log(backup is null ? "4. 기존 설정 파일이 없어 백업을 건너뜀" : $"4. 기존 설정 백업: {backup}");
                Log($"5. 기존 설정을 유지하며 Hooks 병합 ({plan.Changes.Count}개 변경)");
                Log("6. 설정 저장 완료");
            }

            // 7. 연동 테스트 (NowBar 서버가 인증 토큰으로 요청을 받는지 확인)
            var (ok, detail) = await SelfTestAsync();
            Log(ok ? "7. 로컬 수신 테스트 성공" : $"7. 로컬 수신 테스트 실패: {detail}");

            // 8. 결과
            AI.RefreshHooksStatus();
            if (ok && AI.HooksInstalled && !AI.HooksNeedUpdate)
            {
                Log("8. 연동 완료. Claude Code에서 다음 요청을 보내면 AI 작업 탭에 상태가 표시됩니다.");
                Log("   (이미 실행 중인 Claude Code 세션에는 재시작 후 적용될 수 있습니다.)");
            }
            else
            {
                Log("8. 연동을 완료하지 못했습니다. 위 메시지를 확인하세요.");
            }
        }
        catch (Exception ex)
        {
            Log($"오류: {ex.Message}");
            Logger.Error("Claude Code 연동 설정 실패", ex);
        }
        finally
        {
            IsSetupRunning = false;
        }
    }

    [RelayCommand]
    private void DisconnectClaude()
    {
        SetupLog.Clear();
        try
        {
            var plan = _setup.BuildRemovePlan();
            if (!plan.HasChanges)
            {
                Log("settings.json에 NowBar Hooks가 없습니다.");
            }
            else if (ConfirmPlan is not null && ConfirmPlan(plan, "Claude Code 연동 해제"))
            {
                var backup = _setup.Apply(plan);
                Log($"NowBar Hooks를 제거했습니다 ({plan.Changes.Count}개). 다른 Hooks와 설정은 유지됩니다.");
                if (backup is not null) Log($"백업: {backup}");
            }
            else
            {
                Log("연동 해제를 취소했습니다.");
            }
        }
        catch (Exception ex)
        {
            Log($"오류: {ex.Message}");
            Logger.Error("Claude Code 연동 해제 실패", ex);
        }
        AI.RefreshHooksStatus();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        SetupLog.Clear();
        if (!_server.IsRunning)
        {
            Log($"로컬 서버가 실행 중이 아닙니다. {_server.LastError}");
            return;
        }
        var (ok, detail) = await SelfTestAsync();
        Log(ok ? $"로컬 서버가 인증된 요청을 정상 수신했습니다 ({HookUrl})." : $"테스트 실패: {detail}");
        AI.RefreshHooksStatus();
        Log(AI.HooksInstalled
            ? (AI.HooksNeedUpdate ? "Claude Code 설정의 토큰/포트가 현재 값과 다릅니다. 연동 설정을 다시 실행하세요." : "Claude Code 설정에 NowBar Hooks가 등록되어 있습니다.")
            : "Claude Code 설정에 NowBar Hooks가 없습니다.");
        if (AI.LastEventAt is { } t)
            Log($"마지막 실제 Claude Code 이벤트: {t.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    [RelayCommand]
    private void RegenerateToken()
    {
        Settings.HookToken = TokenGenerator.Create();
        SetupLog.Clear();
        Log($"새 토큰을 만들었습니다 ({MaskedToken}). [Claude Code 연동 설정]을 다시 실행해야 Claude Code에 반영됩니다.");
    }

    [RelayCommand]
    private void CopyManualJson()
    {
        try
        {
            Clipboard.SetText(ClaudeHooksSetupService.BuildManualSnippet(HookUrl, Settings.HookToken));
            SetupLog.Clear();
            Log("수동 설정용 hooks JSON을 클립보드에 복사했습니다 (인증 토큰 포함 — 다른 곳에 공유하지 마세요).");
            Log($"{ClaudeSettingsPath} 의 기존 \"hooks\" 항목과 직접 병합하세요.");
        }
        catch (Exception ex)
        {
            Log($"클립보드 복사 실패: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenClaudeFolder() => OpenFolder(Path.GetDirectoryName(ClaudeSettingsPath)!);

    [RelayCommand]
    private void ApplyPort()
    {
        if (int.TryParse(HookPortText, out var port) && port is >= 1024 and <= 65535)
        {
            if (port != Settings.HookPort)
            {
                Settings.HookPort = port;
                SetupLog.Clear();
                Log($"포트를 {port}(으)로 변경했습니다. [Claude Code 연동 설정]을 다시 실행해야 합니다.");
            }
        }
        else
        {
            HookPortText = Settings.HookPort.ToString();
            SetupLog.Clear();
            Log("포트는 1024~65535 사이 숫자여야 합니다.");
        }
    }

    // ── 수동 작업 등록 ───────────────────────────────────
    [RelayCommand]
    private void StartManualTask()
    {
        AI.StartManualTask(ManualTaskName);
        ManualTaskName = "";
    }

    [RelayCommand]
    private void CompleteManualTask() => AI.CompleteManualTask();

    [RelayCommand]
    private void ClearManualTask() => AI.ClearManualTask();

    private async Task<(bool Ok, string Detail)> SelfTestAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, HookUrl)
            {
                Content = new StringContent(
                    $"{{\"hook_event_name\":\"{ClaudeHookEvent.ConnectionTestEventName}\",\"session_id\":\"nowbar-self-test\"}}",
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.HookToken);
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode ? (true, "") : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private void Log(string line) => SetupLog.Add(line);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error("폴더 열기 실패", ex);
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.HookPort):
                HookPortText = Settings.HookPort.ToString();
                OnPropertyChanged(nameof(HookUrl));
                break;
            case nameof(AppSettings.HookToken):
                OnPropertyChanged(nameof(MaskedToken));
                break;
            case nameof(AppSettings.Location):
                OnPropertyChanged(nameof(CurrentLocationText));
                break;
            case nameof(AppSettings.StartWithWindows):
                // App이 레지스트리를 갱신한 뒤 상태를 다시 읽는다
                Application.Current?.Dispatcher.InvokeAsync(UpdateStartupStatus);
                break;
        }
    }

    public void Dispose()
    {
        Settings.PropertyChanged -= OnSettingsChanged;
        _settingsService.UnsavedStateChanged -= OnUnsavedStateChanged;
    }
}
