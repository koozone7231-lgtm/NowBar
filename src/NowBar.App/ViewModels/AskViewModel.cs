using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NowBar.App.Services;

namespace NowBar.App.ViewModels;

/// <summary>
/// 단축키로 여는 빠른 질문 창의 상태. 얼굴 캡슐이 입력창으로 바뀌고, 답은 그 아래로 흘러나온다.
/// 질문·답은 이 창이 열려 있는 동안만 메모리에 있고 저장하지 않는다.
/// </summary>
public partial class AskViewModel : ObservableObject
{
    private readonly ClaudeAskService _service;
    private CancellationTokenSource? _cts;
    /// <summary>지금 보이는 답의 질문 (입력창은 새 질문으로 바뀔 수 있으므로 따로 둔다).</summary>
    private string _answeredQuestion = "";

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _question = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnswer), nameof(ShowResult))]
    private string _answer = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResult), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool _isBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowResult))]
    private string? _error;
    [ObservableProperty] private string _hotkeyText = "Ctrl+Alt+Space";

    public AskViewModel(ClaudeAskService service) => _service = service;

    /// <summary>답 끝의 실행 요청(@open / @app)을 실행한 결과. 누르면 다시 실행.</summary>
    public ObservableCollection<QuickActionItem> Actions { get; } = [];
    public bool HasActions => Actions.Count > 0;
    /// <summary>답을 받은 뒤 Claude Code 작업창에서 이어 갈 수 있는지.</summary>
    public bool CanContinue => !IsBusy && HasAnswer;

    public bool HasAnswer => Answer.Length > 0;
    public bool HasError => !string.IsNullOrEmpty(Error);
    /// <summary>입력창 아래 결과 영역(생각 중 / 답 / 오류)을 보일지.</summary>
    public bool ShowResult => IsBusy || HasAnswer || HasError || HasActions;
    public string StatusText => IsBusy ? (HasAnswer ? "답하는 중…" : "생각 중…") : "";

    /// <summary>열 때마다 결과 영역 크기를 다시 맞추도록 창에 알린다.</summary>
    public event EventHandler? LayoutChanged;

    public void Open()
    {
        IsOpen = true;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>닫기: 진행 중이면 취소하고, 질문·답을 지운다.</summary>
    [RelayCommand]
    public void Close()
    {
        _cts?.Cancel();
        IsOpen = false;
        Question = "";
        Answer = "";
        Error = null;
        ClearActions();
    }

    private void ClearActions()
    {
        Actions.Clear();
        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(ShowResult));
    }

    private bool CanSubmit() => !IsBusy && !string.IsNullOrWhiteSpace(Question);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var question = Question.Trim();
        if (question.Length == 0) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Answer = "";
        Error = null;
        ClearActions();
        _answeredQuestion = question;
        IsBusy = true;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            var result = await _service.AskAsync(question,
                text => dispatcher.InvokeAsync(() =>
                {
                    if (!IsBusy || !IsOpen) return;
                    Answer = QuickActions.HidePending(text); // 실행 요청 줄은 쓰는 중에 보이지 않게
                    OnPropertyChanged(nameof(StatusText));
                    LayoutChanged?.Invoke(this, EventArgs.Empty);
                }, DispatcherPriority.Background),
                _cts.Token);

            if (!IsOpen) return; // 기다리는 사이 닫음
            var (text, actions) = QuickActions.Extract(result.Text);
            Answer = text;
            Error = result.Ok ? null : result.Error;
            if (result.Ok) await RunActionsAsync(actions);
        }
        finally
        {
            IsBusy = false;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Claude가 요청한 링크 열기·앱 실행 (안전한 것만, QuickActions 참고).</summary>
    private async Task RunActionsAsync(IReadOnlyList<QuickAction> actions)
    {
        foreach (var action in actions)
        {
            var item = new QuickActionItem(action);
            Actions.Add(item);
            OnPropertyChanged(nameof(HasActions));
            OnPropertyChanged(nameof(ShowResult));
            await item.RunAsync();
        }
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task RunActionAsync(QuickActionItem? item)
    {
        if (item is not null) await item.RunAsync();
    }

    /// <summary>질문과 답을 첫 메시지로 넣어 Claude Code 작업창(터미널)을 연다.</summary>
    [RelayCommand]
    public void ContinueInClaudeCode()
    {
        if (!CanContinue) return;
        var error = ClaudeCodeLauncher.Launch(_answeredQuestion, Answer);
        if (error is not null)
        {
            Error = error;
            return;
        }
        Close();
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanContinue));
    partial void OnAnswerChanged(string value) => OnPropertyChanged(nameof(CanContinue));

    /// <summary>진행 중인 질문만 멈춘다 (창은 그대로).</summary>
    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void CopyAnswer()
    {
        if (!HasAnswer) return;
        try { Clipboard.SetText(Answer); }
        catch (System.Runtime.InteropServices.COMException) { }
    }
}

/// <summary>실행 요청 하나의 화면 표시 (✓ 열었어요 / 실패 이유).</summary>
public partial class QuickActionItem : ObservableObject
{
    public QuickAction Action { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string? _failure;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private bool _done;

    public QuickActionItem(QuickAction action) => Action = action;

    public string Icon => Action.Kind == QuickActionKind.OpenLink ? "ExternalLink" : "Play";
    public string Title => Action.Kind == QuickActionKind.OpenLink ? $"{Action.Label} 열기" : $"{Action.Label} 실행";
    public string Status => Failure is { } f ? f : Done ? "✓" : "…";
    public bool Failed => Failure is not null;

    public async Task RunAsync()
    {
        Failure = await QuickActions.RunAsync(Action);
        Done = Failure is null;
        OnPropertyChanged(nameof(Failed));
    }
}
