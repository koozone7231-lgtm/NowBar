using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NowBar.App.Helpers;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>
/// 설정을 %AppData%\NowBar\settings.json 에 JSON으로 저장/복원한다.
/// 속성이 바뀌면 400ms 디바운스 후 임시 파일 → 교체 방식으로 저장하여 갑작스러운 종료에도 파일이 깨지지 않게 한다.
/// </summary>
public sealed class SettingsService : IDisposable
{
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
        if (string.IsNullOrWhiteSpace(Settings.HookToken))
            Settings.HookToken = TokenGenerator.Create();

        Settings.PropertyChanged += (_, _) => ScheduleSave();
        Save();
        return Settings;
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
                var json = JsonSerializer.Serialize(Settings, JsonOptions);
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
