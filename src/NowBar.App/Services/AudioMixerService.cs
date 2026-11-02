using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>기본 출력 장치의 전체 볼륨.</summary>
public sealed record AudioMasterInfo(string DeviceName, float Volume, bool Muted);

/// <summary>앱 하나의 오디오 세션(같은 앱의 여러 세션은 하나로 묶음).</summary>
public sealed record AudioAppInfo(string Key, string Name, string? ExePath, bool IsSystem, bool IsActive, float Volume, bool Muted);

/// <summary>
/// Windows Core Audio(NAudio.Wasapi)로 기본 출력 장치의 전체 볼륨과 앱별 볼륨을 읽고 바꾼다 — Windows 볼륨 믹서와 같은 대상.
/// 기본 장치는 캐시해 두고, 기본 장치가 바뀌었을 때(헤드폰 연결 등)만 교체한다.
/// (장치 볼륨 객체 생성·해제와 장치 이름 읽기가 각각 수백 ms 걸리므로 재사용한다)
/// COM 객체를 다루므로 UI(STA) 스레드에서 호출한다.
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    private const string SystemKey = "system-sounds";
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<uint, (string Name, string? Path)> _processNames = new();
    private MMDevice? _device;
    private string? _deviceId;
    private string _deviceName = "";

    public AudioMasterInfo? GetMaster()
    {
        var device = DefaultDevice();
        if (device is null) return null;
        var vol = device.AudioEndpointVolume;
        return new AudioMasterInfo(_deviceName, vol.MasterVolumeLevelScalar, vol.Mute);
    }

    public void SetMasterVolume(float volume)
    {
        var device = DefaultDevice();
        if (device is null) return;
        device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
    }

    public void SetMasterMute(bool mute)
    {
        var device = DefaultDevice();
        if (device is not null) device.AudioEndpointVolume.Mute = mute;
    }

    public IReadOnlyList<AudioAppInfo> GetApps()
    {
        var device = DefaultDevice();
        if (device is null) return [];

        var groups = new Dictionary<string, (AudioAppInfo Info, int Count)>(StringComparer.OrdinalIgnoreCase);
        ForEachSession(device, (session, key, name, path, isSystem) =>
        {
            var simple = session.SimpleAudioVolume;
            var info = new AudioAppInfo(key, name, path, isSystem,
                session.State == AudioSessionState.AudioSessionStateActive, simple.Volume, simple.Mute);
            if (groups.TryGetValue(key, out var existing))
            {
                // 같은 앱의 세션이 여럿이면 하나로: 하나라도 재생 중이면 활성, 볼륨은 가장 큰 값
                info = existing.Info with
                {
                    IsActive = existing.Info.IsActive || info.IsActive,
                    Volume = Math.Max(existing.Info.Volume, info.Volume),
                    Muted = existing.Info.Muted && info.Muted,
                };
                groups[key] = (info, existing.Count + 1);
            }
            else
            {
                groups[key] = (info, 1);
            }
        });

        return groups.Values
            .Select(g => g.Info)
            .OrderBy(a => a.IsSystem)              // 시스템 소리는 맨 아래
            .ThenByDescending(a => a.IsActive)     // 재생 중인 앱 먼저
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void SetAppVolume(string key, float volume) =>
        Apply(key, s => s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f));

    public void SetAppMute(string key, bool mute) =>
        Apply(key, s => s.SimpleAudioVolume.Mute = mute);

    private void Apply(string key, Action<AudioSessionControl> action)
    {
        var device = DefaultDevice();
        if (device is null) return;
        ForEachSession(device, (session, k, _, _, _) =>
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) action(session);
        });
    }

    private void ForEachSession(MMDevice device, Action<AudioSessionControl, string, string, string?, bool> visit)
    {
        var manager = device.AudioSessionManager;
        manager.RefreshSessions();
        var sessions = manager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            try
            {
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;
                if (session.IsSystemSoundsSession)
                {
                    visit(session, SystemKey, "시스템 소리", null, true);
                    continue;
                }
                var pid = session.GetProcessID;
                if (pid == 0 || pid == Environment.ProcessId) continue;
                var (name, path) = ResolveProcess(pid, session.DisplayName);
                // 같은 앱(예: 브라우저의 여러 프로세스)은 이름으로 묶는다
                visit(session, name, name, path, false);
            }
            catch (Exception ex)
            {
                Logger.Error("오디오 세션 처리 실패", ex);
            }
        }
    }

    private (string Name, string? Path) ResolveProcess(uint pid, string? displayName)
    {
        if (_processNames.TryGetValue(pid, out var cached)) return cached;
        string name = string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith('@') ? "" : displayName;
        string? path = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            try
            {
                path = process.MainModule?.FileName;
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(description)) name = description!;
            }
            catch
            {
                // 권한이 높은 프로세스는 모듈 정보를 읽을 수 없다 → 프로세스 이름 사용
            }
            if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
        }
        catch
        {
            if (string.IsNullOrWhiteSpace(name)) name = $"앱 {pid}";
        }
        var result = (name.Trim(), path);
        _processNames[pid] = result;
        return result;
    }

    /// <summary>현재 기본 출력 장치 (캐시). 기본 장치가 바뀌었으면 새 장치로 교체한다.</summary>
    private MMDevice? DefaultDevice()
    {
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            {
                ReplaceDevice(null);
                return null;
            }
            var current = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_device is not null && current.ID == _deviceId)
            {
                current.Dispose(); // 볼륨 객체를 만들지 않은 핸들은 가볍게 해제된다
                return _device;
            }
            ReplaceDevice(current);
            return _device;
        }
        catch (Exception ex)
        {
            Logger.Error("기본 오디오 장치를 가져올 수 없습니다", ex);
            ReplaceDevice(null);
            return null;
        }
    }

    private void ReplaceDevice(MMDevice? device)
    {
        if (_device is not null && !ReferenceEquals(_device, device))
        {
            try { _device.Dispose(); } catch { /* 이미 사라진 장치 */ }
        }
        _device = device;
        _deviceId = device?.ID;
        // 장치 이름은 속성 저장소를 읽어 호출마다 수백 ms가 걸리므로 장치가 바뀔 때 한 번만 읽는다
        try { _deviceName = device?.FriendlyName ?? ""; }
        catch { _deviceName = ""; }
    }

    public void Dispose()
    {
        ReplaceDevice(null);
        _enumerator.Dispose();
    }
}
