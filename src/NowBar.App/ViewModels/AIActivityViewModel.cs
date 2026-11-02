using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Helpers;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>확장 화면에 표시하는 세션 한 개.</summary>
public partial class ClaudeSessionItem : ObservableObject
{
    private readonly AppSettings _settings;

    public ClaudeSession Session { get; }
    public string Id => Session.SessionId;

    [ObservableProperty] private bool _isSelected;

    public ClaudeSessionItem(ClaudeSession session, AppSettings settings)
    {
        Session = session;
        _settings = settings;
    }

    public ClaudeTaskState State => Session.State;
    public string ProjectName => Session.ProjectName;
    public string StateText => AIActivityViewModel.StateText(Session.State);
    public string ActivityText => string.IsNullOrEmpty(Session.LastActivity) ? StateText : Session.LastActivity;
    public string? SummaryText => _settings.ShowTaskSummary || Session.IsManual ? Session.TaskSummary : null;
    public string? ErrorText => Session.ErrorText;

    public string? NotificationText => _settings.ShowNotifications ? Session.LastNotification : null;

    public string StartedText => Session.TaskStartedAt is { } t
        ? $"시작 {t.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}"
        : "시작 시간 알 수 없음";

    public string ElapsedText
    {
        get
        {
            if (Session.TaskStartedAt is not { } start) return "";
            var end = Session.CompletedAt ?? DateTimeOffset.Now;
            return $"경과 {AIActivityViewModel.FormatDuration(end - start)}";
        }
    }

    public string LastEventText
    {
        get
        {
            var ago = DateTimeOffset.Now - Session.LastEventAt;
            var text = ago.TotalSeconds < 10 ? "방금" : $"{AIActivityViewModel.FormatDuration(ago, coarse: true)} 전";
            return $"마지막 이벤트 {text}";
        }
    }

    /// <summary>작업 중인데 오래 이벤트가 없으면, 사용자가 중단(Esc)해 Stop 이벤트가 오지 않았을 가능성을 알린다.</summary>
    public bool IsStale =>
        Session.State is ClaudeTaskState.Working or ClaudeTaskState.ToolRunning
        && DateTimeOffset.Now - Session.LastEventAt > TimeSpan.FromMinutes(10);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

public partial class AIActivityViewModel : ObservableObject, IDisposable
{
    private readonly ClaudeHookServer _server;
    private readonly ClaudeHooksSetupService _setup;
    private readonly AppSettings _settings;
    private readonly Action _openSettings;
    private readonly ClaudeSessionTracker _tracker = new();
    private readonly DispatcherTimer _tick;
    private string? _userSelectedId;

    public ObservableCollection<ClaudeSessionItem> Sessions { get; } = [];

    [ObservableProperty] private ClaudeSessionItem? _selectedSession;
    [ObservableProperty] private bool _isServerRunning;
    [ObservableProperty] private string? _serverError;
    [ObservableProperty] private HooksStatus? _hooksStatus;
    [ObservableProperty] private DateTimeOffset? _lastEventAt;
    [ObservableProperty] private DateTimeOffset? _lastTestAt;

    public AIActivityViewModel(ClaudeHookServer server, ClaudeHooksSetupService setup, AppSettings settings, Action openSettings)
    {
        _server = server;
        _setup = setup;
        _settings = settings;
        _openSettings = openSettings;

        _server.EventReceived += (_, e) => Ui(() => Handle(e));
        _server.StatusChanged += (_, _) => Ui(SyncServerStatus);
        _settings.PropertyChanged += OnSettingsChanged;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => OnTick();
        ApplyTrackerOptions();
    }

    public bool HooksInstalled => HooksStatus?.Installed == true;
    public bool HooksNeedUpdate => HooksStatus is { Installed: true, TokenMatches: false };
    /// <summary>최근 30분 안에 인증된 실제 이벤트를 받았는지 (프로젝트 설정 등 다른 위치에 Hooks가 있어도 연결로 판단).</summary>
    public bool RecentlyReceived => LastEventAt is { } t && DateTimeOffset.Now - t < TimeSpan.FromMinutes(30);
    public bool IsConnected => IsServerRunning && ((HooksInstalled && !HooksNeedUpdate) || RecentlyReceived);
    public bool HasSessions => Sessions.Count > 0;
    public bool ShowSessionChips => _settings.ShowSessions && Sessions.Count > 1;

    public ClaudeTaskState AggregateState => _tracker.GetPrimary()?.State ?? ClaudeTaskState.Idle;

    public string CompactText
    {
        get
        {
            if (!_settings.ClaudeIntegrationEnabled || !IsServerRunning) return "비활성";
            if (!HasSessions) return IsConnected ? "대기 중" : "비활성";
            return AggregateState switch
            {
                ClaudeTaskState.Working or ClaudeTaskState.ToolRunning => "작업 중",
                ClaudeTaskState.Waiting => "응답 필요",
                ClaudeTaskState.Completed => "완료",
                ClaudeTaskState.Error => "오류",
                _ => "대기 중",
            };
        }
    }

    public string ConnectionText
    {
        get
        {
            if (!_settings.ClaudeIntegrationEnabled) return "연동 꺼짐";
            if (!IsServerRunning) return ServerError ?? "로컬 서버 중지됨";
            if (RecentlyReceived && LastEventAt is { } t)
            {
                var ago = DateTimeOffset.Now - t;
                return ago.TotalSeconds < 10 ? "연결됨 · 방금 이벤트 수신" : $"연결됨 · {FormatDuration(ago, coarse: true)} 전 이벤트";
            }
            if (HooksStatus?.Error is { } err) return $"설정 파일 확인 불가: {err}";
            if (!HooksInstalled) return "Hooks 미설정 · 설정에서 연동하세요";
            if (HooksNeedUpdate) return "토큰 불일치 · 연동 설정을 다시 실행하세요";
            return "연결 준비됨 · 이벤트 대기 중";
        }
    }

    public string EmptyText => IsConnected || !_settings.ClaudeIntegrationEnabled
        ? "진행 중인 Claude Code 작업이 없습니다."
        : "Claude Code 연동이 아직 설정되지 않았습니다.";

    public async Task InitializeAsync()
    {
        await RestartServerAsync();
        RefreshHooksStatus();
    }

    public async Task RestartServerAsync()
    {
        if (_settings.ClaudeIntegrationEnabled)
            await _server.StartAsync(_settings.HookPort);
        else
            await _server.StopAsync();
        SyncServerStatus();
    }

    public void RefreshHooksStatus()
    {
        HooksStatus = _setup.GetStatus(ClaudeHookServer.BuildUrl(_settings.HookPort), _settings.HookToken);
        RaiseStatus();
    }

    private void SyncServerStatus()
    {
        IsServerRunning = _server.IsRunning;
        ServerError = _server.LastError;
        RaiseStatus();
    }

    private void Handle(ClaudeHookEvent e)
    {
        if (e.EventName == ClaudeHookEvent.ConnectionTestEventName)
        {
            LastTestAt = e.ReceivedAt;
            return;
        }

        LastEventAt = e.ReceivedAt;
        ApplyTrackerOptions();
        _tracker.Apply(e);
        if (e.EventName == "SessionEnd" && _userSelectedId == e.SessionId) _userSelectedId = null;
        SyncSessions();
    }

    // ── 수동 작업 (Hooks를 쓸 수 없는 경우) ─────────────
    public void StartManualTask(string? name)
    {
        _tracker.StartManual(name);
        _userSelectedId = ClaudeSessionTracker.ManualSessionId;
        SyncSessions();
    }

    public void CompleteManualTask()
    {
        _tracker.CompleteManual();
        SyncSessions();
    }

    public void ClearManualTask()
    {
        _tracker.RemoveManual();
        SyncSessions();
    }

    public bool HasManualTask => _tracker.Get(ClaudeSessionTracker.ManualSessionId) is not null;

    [RelayCommand]
    private void SelectSession(ClaudeSessionItem? item)
    {
        if (item is null) return;
        _userSelectedId = item.Id;
        SyncSessions();
    }

    [RelayCommand]
    private void OpenSettings() => _openSettings();

    private void SyncSessions()
    {
        var current = _tracker.Sessions.ToDictionary(s => s.SessionId);

        for (var i = Sessions.Count - 1; i >= 0; i--)
            if (!current.ContainsKey(Sessions[i].Id)) Sessions.RemoveAt(i);

        foreach (var s in current.Values.OrderByDescending(s => s.LastEventAt))
            if (Sessions.All(x => x.Id != s.SessionId)) Sessions.Add(new ClaudeSessionItem(s, _settings));

        if (_userSelectedId is not null && !current.ContainsKey(_userSelectedId)) _userSelectedId = null;
        var selectedId = _userSelectedId ?? _tracker.GetPrimary()?.SessionId;
        SelectedSession = Sessions.FirstOrDefault(x => x.Id == selectedId);

        foreach (var item in Sessions)
        {
            item.IsSelected = ReferenceEquals(item, SelectedSession);
            item.Refresh();
        }

        _tick.IsEnabled = Sessions.Count > 0;
        RaiseStatus();
    }

    private void OnTick()
    {
        // 12시간 이상 이벤트가 없는 세션은 종료된 것으로 간주 (SessionEnd를 못 받은 경우)
        if (_tracker.PruneInactive(TimeSpan.FromHours(12)) > 0)
        {
            SyncSessions();
            return;
        }
        foreach (var item in Sessions) item.Refresh();
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(HooksInstalled));
        OnPropertyChanged(nameof(HooksNeedUpdate));
        OnPropertyChanged(nameof(RecentlyReceived));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowSessionChips));
        OnPropertyChanged(nameof(AggregateState));
        OnPropertyChanged(nameof(CompactText));
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasManualTask));
    }

    partial void OnLastEventAtChanged(DateTimeOffset? value) => RaiseStatus();

    private void ApplyTrackerOptions()
    {
        _tracker.ShowSummary = _settings.ShowTaskSummary;
        _tracker.SummaryMaxLength = _settings.SummaryMaxLength;
        _tracker.ShowNotifications = _settings.ShowNotifications;
    }

    private async void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            switch (e.PropertyName)
            {
                case nameof(AppSettings.ClaudeIntegrationEnabled):
                case nameof(AppSettings.HookPort):
                    await RestartServerAsync();
                    RefreshHooksStatus();
                    break;
                case nameof(AppSettings.HookToken):
                    RefreshHooksStatus();
                    break;
                case nameof(AppSettings.ShowTaskSummary):
                    // 동의를 철회하면 메모리에 있는 요약도 즉시 지운다
                    if (!_settings.ShowTaskSummary) _tracker.ClearSummaries();
                    ApplyTrackerOptions();
                    SyncSessions();
                    break;
                case nameof(AppSettings.ShowNotifications):
                    if (!_settings.ShowNotifications) _tracker.ClearNotifications();
                    ApplyTrackerOptions();
                    SyncSessions();
                    break;
                case nameof(AppSettings.SummaryMaxLength):
                    ApplyTrackerOptions();
                    break;
                case nameof(AppSettings.ShowSessions):
                    OnPropertyChanged(nameof(ShowSessionChips));
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("AI 설정 변경 처리 실패", ex);
        }
    }

    public static string StateText(ClaudeTaskState state) => state switch
    {
        ClaudeTaskState.Idle => "대기 중",
        ClaudeTaskState.Working => "작업 중",
        ClaudeTaskState.ToolRunning => "도구 실행 중",
        ClaudeTaskState.Waiting => "응답 필요",
        ClaudeTaskState.Completed => "완료",
        ClaudeTaskState.Error => "오류",
        _ => "알 수 없음",
    };

    public static string FormatDuration(TimeSpan t, bool coarse = false)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}시간 {t.Minutes}분";
        if (t.TotalMinutes >= 1) return coarse ? $"{(int)t.TotalMinutes}분" : $"{(int)t.TotalMinutes}분 {t.Seconds}초";
        return $"{t.Seconds}초";
    }

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { Logger.Error("AI UI 갱신 실패", ex); }
        });
    }

    public void Dispose()
    {
        _tick.Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
