using System.Text.Json;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

/// <summary>설정 창의 [저장] / [되돌리기] / 저장 안 하고 닫기 동작.</summary>
public sealed class SettingsEditSessionTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nowbar-edit-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }

    private AppSettings OnDisk() =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), SettingsService.JsonOptions)!;

    private static async Task WaitForDebounce() => await Task.Delay(700);

    [Fact]
    public async Task Edits_are_written_only_on_save()
    {
        using var svc = new SettingsService(_path);
        var s = svc.Load();
        svc.BeginEdit();

        s.Theme = AppTheme.Light;
        s.CompactWidth = 260;
        await WaitForDebounce();
        Assert.True(svc.HasUnsavedChanges);
        Assert.Equal(AppTheme.Dark, OnDisk().Theme); // 아직 파일에는 반영 안 됨

        svc.CommitEdit();
        Assert.False(svc.HasUnsavedChanges);
        Assert.Equal(AppTheme.Light, OnDisk().Theme);
        Assert.Equal(260, OnDisk().CompactWidth);
    }

    [Fact]
    public void Revert_restores_last_saved_values()
    {
        using var svc = new SettingsService(_path);
        var s = svc.Load();
        svc.BeginEdit();

        s.CornerRadius = 12;
        s.Location = new GeoLocation("Busan", null, null, 35.1, 129.0, null);
        svc.RevertEdit();

        Assert.Equal(20, s.CornerRadius);
        Assert.Null(s.Location);
        Assert.False(svc.HasUnsavedChanges);
    }

    [Fact]
    public void Closing_without_save_discards_changes()
    {
        using var svc = new SettingsService(_path);
        var s = svc.Load();
        svc.BeginEdit();
        s.AnimationMs = 400;
        svc.EndEdit(save: false);

        Assert.Equal(200, s.AnimationMs);
        Assert.Equal(200, OnDisk().AnimationMs);
        Assert.False(svc.IsEditing);
    }

    [Fact]
    public async Task Immediate_keys_are_saved_even_while_editing_and_survive_revert()
    {
        using var svc = new SettingsService(_path);
        var s = svc.Load();
        svc.BeginEdit();

        s.HookToken = "new-token";   // Claude 설정 파일에 이미 기록된 값과 어긋나면 안 되는 항목
        s.Theme = AppTheme.Light;    // 일반 항목
        await WaitForDebounce();

        Assert.Equal("new-token", OnDisk().HookToken);
        Assert.Equal(AppTheme.Dark, OnDisk().Theme);

        svc.RevertEdit();
        Assert.Equal("new-token", s.HookToken);
        Assert.Equal(AppTheme.Dark, s.Theme);
    }

    [Fact]
    public async Task Outside_edit_session_changes_autosave()
    {
        using var svc = new SettingsService(_path);
        var s = svc.Load();
        s.LastSelectedTab = NowBarTab.Music;
        await WaitForDebounce();
        Assert.Equal(NowBarTab.Music, OnDisk().LastSelectedTab);
    }
}
