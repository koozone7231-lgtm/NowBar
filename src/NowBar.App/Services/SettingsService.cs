using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NowBar.App.Helpers;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>
/// 설정을 %AppData%\NowBar\settings.json 에 JSON으로 저장/복원한다.
/// 속성이 바뀌면 400ms 디바운스 후 임시 파일 → 교체 방식으로 저장하여 갑작스러운 종료에도 파일이 깨지지 않게 한다.
///
/// 설정 창이 열려 있는 동안은 "편집 세션"으로 동작한다:
/// 화면에는 즉시 반영(미리보기)하지만 파일에는 [저장] 시에만 기록하고, [되돌리기]/저장 안 함이면 창을 열 때 상태로 복원한다.
/// 단, 외부(Claude 설정 파일·레지스트리·로컬 서버)에 바로 영향을 주는 항목(<see cref="ImmediateKeys"/>)은 어긋나지 않도록 즉시 저장한다.
/// </summary>
public sealed class SettingsService : IDisposable
{
    /// <summary>편집 세션 중에도 즉시 저장되는 항목.</summary>
    public static readonly IReadOnlySet<string> ImmediateKeys = new HashSet<string>
    {
        nameof(AppSettings.StartWithWindows),
        nameof(AppSettings.ClaudeIntegrationEnabled),
        nameof(AppSettings.HookPort),
        nameof(AppSettings.HookToken),
        nameof(AppSettings.LastSelectedTab),
        nameof(AppSettings.HiddenMode),
        nameof(AppSettings.LastSeenVersion),
    };

    /// <summary>편집 세션 중 파일에 기록되는 "저장된" 상태. null이면 편집 세션이 아님.</summary>
    private AppSettings? _committed;

    public bool IsEditing => _committed is not null;

    /// <summary>편집 세션에서 아직 저장하지 않은 변경이 있는지.</summary>
    public bool HasUnsavedChanges =>
        _committed is not null && JsonSerializer.Serialize(Settings, JsonOptions) != JsonSerializer.Serialize(_committed, JsonOptions);

    public event EventHandler? UnsavedStateChanged;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private Timer? _debounce;

    public string FilePath { get; }
    public AppSettings Settings { get; private set; } = new();

    public SettingsService(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.SettingsFile;
    }

    public AppSettings Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex)
        {
            Logger.Error("설정 파일을 읽을 수 없어 기본값을 사용합니다", ex);
            BackupCorruptFile();
        }

        Settings = loaded ?? new AppSettings();
        Settings.Normalize();
        Settings.MigrateDesignDefaults();
        if (string.IsNullOrWhiteSpace(Settings.HookToken))
            Settings.HookToken = TokenGenerator.Create();

        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Save();
        return Settings;
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_committed is null)
        {
            ScheduleSave();
            return;
        }

        if (e.PropertyName is { } name && ImmediateKeys.Contains(name))
        {
            // 즉시 적용 항목은 저장된 상태에도 반영해 바로 기록
            CopyProperty(Settings, _committed, name);
            ScheduleSave();
        }
        UnsavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>설정 창을 열 때 호출. 현재 상태를 "저장된 상태"로 기억한다.</summary>
    public void BeginEdit()
    {
        lock (_gate)
        {
            Save(); // 대기 중인 자동 저장을 먼저 반영
            _committed = Clone(Settings);
        }
        UnsavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>[저장]: 현재 화면의 설정을 파일에 기록한다. 편집 세션은 계속된다.</summary>
    public void CommitEdit()
    {
        Settings.Normalize();
        lock (_gate)
        {
            _committed = Clone(Settings);
            Save();
        }
        UnsavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>[되돌리기]: 마지막으로 저장한 상태로 복원한다. 편집 세션은 계속된다.</summary>
    public void RevertEdit()
    {
        if (_committed is not { } committed) return;
        foreach (var prop in typeof(AppSettings).GetProperties())
            if (prop.CanWrite && prop.CanRead) CopyProperty(committed, Settings, prop.Name);
        UnsavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>설정 창을 닫을 때 호출. save=false면 저장하지 않은 변경을 버린다.</summary>
    public void EndEdit(bool save)
    {
        if (_committed is null) return;
        if (save) CommitEdit();
        else RevertEdit();
        lock (_gate)
        {
            _committed = null;
            Save();
        }
        UnsavedStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Clone(AppSettings source) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions)!;

    private static void CopyProperty(AppSettings from, AppSettings to, string name)
    {
        var prop = typeof(AppSettings).GetProperty(name);
        if (prop is null || !prop.CanRead || !prop.CanWrite) return;
        var value = prop.GetValue(from);
        if (!Equals(prop.GetValue(to), value)) prop.SetValue(to, value);
    }

    public void ScheduleSave()
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(400, Timeout.Infinite);
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // 편집 세션 중에는 마지막으로 저장한 상태만 파일에 기록
                var json = JsonSerializer.Serialize(_committed ?? Settings, JsonOptions);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Error("설정 저장 실패", ex);
            }
        }
    }

    private void BackupCorruptFile()
    {
        try { File.Move(FilePath, $"{FilePath}.corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true); }
        catch (Exception ex) { Logger.Error("손상된 설정 파일 백업 실패", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
        }
        Save();
    }
}
