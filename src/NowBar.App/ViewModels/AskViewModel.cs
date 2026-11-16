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

    public bool HasAnswer => Answer.Length > 0;
    public bool HasError => !string.IsNullOrEmpty(Error);
    /// <summary>입력창 아래 결과 영역(생각 중 / 답 / 오류)을 보일지.</summary>
    public bool ShowResult => IsBusy || HasAnswer || HasError;
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
        IsBusy = true;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var dispatcher = Application.Current.Dispatcher;
            var result = await _service.AskAsync(question,
                text => dispatcher.InvokeAsync(() =>
                {
                    if (!IsBusy || !IsOpen) return;
                    Answer = text;
                    OnPropertyChanged(nameof(StatusText));
                    LayoutChanged?.Invoke(this, EventArgs.Empty);
                }, DispatcherPriority.Background),
                _cts.Token);

            if (!IsOpen) return; // 기다리는 사이 닫음
            Answer = result.Text;
            Error = result.Ok ? null : result.Error;
        }
        finally
        {
            IsBusy = false;
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

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
