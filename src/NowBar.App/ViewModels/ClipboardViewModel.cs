using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>
/// 도구 탭의 클립보드 기록. 감지는 항상 하되(일시 정지 제외) 목록 화면은 보일 때만 다시 만든다.
/// 클릭 = 다시 복사, Shift+클릭 = 패널을 열기 전에 쓰던 창에 바로 붙여넣기.
/// </summary>
public partial class ClipboardViewModel : ObservableObject
{
    private readonly ClipboardService _service;
    private readonly AppSettings _settings;
    private readonly ClipboardHistory _history = new();
    private bool _active;
    private bool _dirty = true;

    /// <summary>패널을 펼치기 직전에 사용자가 쓰던 창 (Shift+클릭 붙여넣기 대상).</summary>
    public IntPtr PasteTarget { get; set; }

    public ObservableCollection<ClipboardEntry> Items { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string? _message;

    public bool HasItems => Items.Count > 0;
    public bool HasHistory => _history.Items.Count > 0;
    public string EmptyText =>
        !IsEnabled ? "설정에서 클립보드 기록이 꺼져 있습니다."
        : HasHistory ? "검색 결과가 없습니다."
        : IsPaused ? "기록이 일시 정지되어 있습니다."
        : "복사한 내용이 여기에 쌓입니다.";
    public string PauseIcon => IsPaused ? "Play" : "Pause";
    public string PauseToolTip => IsPaused ? "기록 다시 시작" : "기록 일시 정지";

    public ClipboardViewModel(ClipboardService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
        _service.Captured += (_, entry) =>
        {
            _history.Add(entry);
            MarkDirty();
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ClipboardHistoryEnabled)) ApplyEnabled();
        };
    }

    public bool IsEnabled => _settings.ClipboardHistoryEnabled;

    /// <summary>설정에 따라 감지를 시작/중지. 끄면 고정 항목까지 모두 비운다.</summary>
    public void ApplyEnabled()
    {
        if (IsEnabled)
        {
            _service.Start();
        }
        else
        {
            _service.Stop();
            _history.ClearAll();
        }
        OnPropertyChanged(nameof(IsEnabled));
        MarkDirty();
    }

    partial void OnSearchChanged(string value) => MarkDirty();

    partial void OnIsPausedChanged(bool value)
    {
        _service.Paused = value;
        OnPropertyChanged(nameof(PauseIcon));
        OnPropertyChanged(nameof(PauseToolTip));
        OnPropertyChanged(nameof(EmptyText));
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
        {
            _dirty = true; // "n분 전" 같은 시간 표시도 새로
            Rebuild();
        }
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (_active) Rebuild();
    }

    /// <summary>고정 항목을 위로, 나머지는 최근 순. 검색어로 거른다.</summary>
    private void Rebuild()
    {
        if (!_dirty) return;
        _dirty = false;
        Items.Clear();
        foreach (var e in _history.Items.Where(e => e.Pinned).Concat(_history.Items.Where(e => !e.Pinned)))
        {
            if (e.Matches(Search)) Items.Add(e);
        }
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>항목 클릭: 다시 복사하고, paste면 원래 창에 붙여넣는다.</summary>
    public async Task UseAsync(ClipboardEntry entry, bool paste)
    {
        if (!_service.SetClipboard(entry))
        {
            Message = "클립보드를 쓰지 못했습니다. 잠시 후 다시 시도하세요.";
            return;
        }
        Message = null;
        if (paste) await ClipboardService.PasteIntoAsync(PasteTarget);
    }

    [RelayCommand]
    private void TogglePin(ClipboardEntry entry)
    {
        _history.TogglePin(entry);
        MarkDirty();
    }

    [RelayCommand]
    private void Remove(ClipboardEntry entry)
    {
        _history.Remove(entry);
        MarkDirty();
    }

    [RelayCommand]
    private void Clear()
    {
        _history.ClearUnpinned();
        MarkDirty();
    }

    [RelayCommand]
    private void TogglePause() => IsPaused = !IsPaused;
}
