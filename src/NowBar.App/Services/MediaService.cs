using System.Diagnostics;
using NowBar.App.Helpers;
using NowBar.App.Models;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GsmtcStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace NowBar.App.Services;

/// <summary>
/// Windows Global System Media Transport Controls(GSMTC) 연동.
/// 다른 프로그램(Spotify, 미디어 플레이어, 브라우저 등)이 Windows에 제공하는 미디어 세션을 읽고 제어한다.
/// 모든 이벤트는 백그라운드 스레드에서 발생하므로 구독자는 UI 스레드로 마샬링해야 한다.
/// </summary>
public sealed class MediaService : IDisposable
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GsmtcSession? _session;
    private string? _sessionKey;
    private string? _manualKey;

    private volatile bool _mediaDirty = true;
    private volatile bool _retryingAfterFailure;
    private string? _title, _artist, _album;
    private byte[]? _thumbnail;
    private bool _disposed;

    public bool IsInitialized => _manager is not null;
    public string? InitError { get; private set; }
    public string? CurrentKey => _sessionKey;

    /// <summary>true: Windows가 지정한 현재 세션을 자동으로 따라감. false: 마지막 선택 세션 유지.</summary>
    public bool AutoSelect { get; set; } = true;

    public event EventHandler? SessionsChanged;
    /// <summary>선택된 세션의 최신 스냅샷. 세션이 없으면 null.</summary>
    public event EventHandler<MediaSessionData?>? Updated;

    public async Task<bool> InitializeAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_lock)
            {
                _manager = manager;
                _manager.SessionsChanged += OnManagerSessionsChanged;
                _manager.CurrentSessionChanged += OnManagerCurrentSessionChanged;
            }
            InitError = null;
            Reselect();
            return true;
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            Logger.Error("Windows 미디어 세션 관리자 요청 실패", ex);
            Updated?.Invoke(this, null);
            return false;
        }
    }

    public IReadOnlyList<MediaSessionInfo> GetSessions()
    {
        var manager = _manager;
        if (manager is null) return [];
        try
        {
            return BuildKeys(manager.GetSessions())
                .Select(k => new MediaSessionInfo(k.Key, k.Session.SourceAppUserModelId ?? "",
                    AppNameResolver.Resolve(k.Session.SourceAppUserModelId)))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error("미디어 세션 목록 조회 실패", ex);
            return [];
        }
    }

    public void SelectSession(string key)
    {
        _manualKey = key;
        Reselect();
    }

    // ── 제어 ─────────────────────────────────────────────
    public Task<bool> TogglePlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync());
    public Task<bool> SkipNextAsync() => RunAsync(s => s.TrySkipNextAsync());
    public Task<bool> SkipPreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync());

    public Task<bool> SeekAsync(TimeSpan position) => RunAsync(s =>
    {
        var start = s.GetTimelineProperties().StartTime;
        return s.TryChangePlaybackPositionAsync((start + position).Ticks);
    });

    private async Task<bool> RunAsync(Func<GsmtcSession, IAsyncOperation<bool>> operation)
    {
        var session = _session;
        if (session is null) return false;
        try
        {
            return await operation(session);
        }
        catch (Exception ex)
        {
            Logger.Error("미디어 제어 명령 실패", ex);
            return false;
        }
    }

    /// <summary>재생 중인 프로그램 창을 앞으로 가져오거나(실행 중인 경우) 앱을 연다.</summary>
    public bool TryOpenSourceApp()
    {
        var aumid = _session?.SourceAppUserModelId;
        return !string.IsNullOrEmpty(aumid) && AppNameResolver.TryActivate(aumid);
    }

    // ── 세션 선택/구독 ───────────────────────────────────
    private static List<(string Key, GsmtcSession Session)> BuildKeys(IReadOnlyList<GsmtcSession> sessions)
    {
        // 같은 앱이 세션을 여러 개 제공할 수 있으므로 순번을 붙여 구분
        var counts = new Dictionary<string, int>();
        var list = new List<(string, GsmtcSession)>(sessions.Count);
        foreach (var s in sessions)
        {
            var id = s.SourceAppUserModelId ?? "";
            counts.TryGetValue(id, out var n);
            counts[id] = n + 1;
            list.Add((n == 0 ? id : $"{id}#{n}", s));
        }
        return list;
    }

    private void Reselect()
    {
        lock (_lock)
        {
            if (_manager is null || _disposed) return;
            try
            {
                var keyed = BuildKeys(_manager.GetSessions());
                (string Key, GsmtcSession Session)? target = null;

                if (_manualKey is not null)
                {
                    var idx = keyed.FindIndex(k => k.Key == _manualKey);
                    if (idx >= 0) target = keyed[idx];
                    else _manualKey = null; // 선택했던 세션이 사라짐 → 자동 선택으로 복귀
                }

                if (target is null && !AutoSelect && _sessionKey is not null)
                {
                    var idx = keyed.FindIndex(k => k.Key == _sessionKey);
                    if (idx >= 0) target = keyed[idx];
                }

                if (target is null)
                {
                    var current = _manager.GetCurrentSession();
                    if (current is not null)
                    {
                        var idx = keyed.FindIndex(k => k.Session.SourceAppUserModelId == current.SourceAppUserModelId);
                        if (idx >= 0) target = keyed[idx];
                    }
                }

                if (target is null && keyed.Count > 0) target = keyed[0];

                Attach(target?.Session, target?.Key);
            }
            catch (Exception ex)
            {
                Logger.Error("미디어 세션 선택 실패", ex);
            }
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshAsync();
    }

    private void Attach(GsmtcSession? session, string? key)
    {
        if (_session is not null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch (Exception ex) { Logger.Error("이전 미디어 세션 구독 해제 실패", ex); }
        }

        if (key != _sessionKey) _mediaDirty = true;
        _session = session;
        _sessionKey = key;

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }
    }

    private void OnManagerSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Reselect();

    private void OnManagerCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        if (AutoSelect && _manualKey is null) Reselect();
    }

    private void OnMediaPropertiesChanged(GsmtcSession sender, MediaPropertiesChangedEventArgs args)
    {
        _mediaDirty = true;
        _ = RefreshAsync();
    }

    private void OnPlaybackInfoChanged(GsmtcSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();

    private void OnTimelinePropertiesChanged(GsmtcSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    // ── 상태 읽기 ────────────────────────────────────────
    public async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var session = _session;
            var key = _sessionKey;
            if (session is null || key is null)
            {
                Updated?.Invoke(this, null);
                return;
            }

            var aumid = session.SourceAppUserModelId ?? "";
            try
            {
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();

                if (_mediaDirty)
                {
                    _mediaDirty = false;
                    var props = await session.TryGetMediaPropertiesAsync();
                    _title = NullIfEmpty(props?.Title);
                    _artist = NullIfEmpty(props?.Artist) ?? NullIfEmpty(props?.AlbumArtist);
                    _album = NullIfEmpty(props?.AlbumTitle);
                    _thumbnail = await ReadThumbnailAsync(props?.Thumbnail).ConfigureAwait(false);
                }

                var controls = playback?.Controls;
                var data = new MediaSessionData
                {
                    Key = key,
                    SourceAppId = aumid,
                    AppName = AppNameResolver.Resolve(aumid),
                    Title = _title,
                    Artist = _artist,
                    AlbumTitle = _album,
                    ThumbnailBytes = _thumbnail,
                    MediaKey = $"{key}|{_title}|{_artist}|{_album}|{_thumbnail?.Length}",
                    State = MapStatus(playback?.PlaybackStatus),
                    PlaybackRate = playback?.PlaybackRate ?? 1.0,
                    Position = timeline.Position - timeline.StartTime,
                    Duration = timeline.EndTime - timeline.StartTime,
                    TimelineUpdatedAt = timeline.LastUpdatedTime,
                    CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? false,
                    CanNext = controls?.IsNextEnabled ?? false,
                    CanPrevious = controls?.IsPreviousEnabled ?? false,
                    CanSeek = (controls?.IsPlaybackPositionEnabled ?? false) && timeline.EndTime > timeline.StartTime,
                };
                Updated?.Invoke(this, data);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException && !_retryingAfterFailure)
            {
                // 재생 프로그램이 닫히는 중이면 세션이 사라지며 읽기가 실패한다 → 잠시 후 남은 세션으로 다시 선택
                Logger.Info($"미디어 세션을 읽을 수 없어 다시 선택합니다 (0x{ex.HResult:X8})");
                _retryingAfterFailure = true;
                _ = Task.Delay(500).ContinueWith(_ =>
                {
                    Reselect();
                    _retryingAfterFailure = false;
                }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Logger.Error("미디어 정보 읽기 실패", ex);
                Updated?.Invoke(this, new MediaSessionData
                {
                    Key = key,
                    SourceAppId = aumid,
                    AppName = AppNameResolver.Resolve(aumid),
                    ReadFailed = true,
                });
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var ras = await reference.OpenReadAsync();
            if (ras.Size == 0 || ras.Size > 10 * 1024 * 1024) return null;
            await using var stream = ras.AsStreamForRead();
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms).ConfigureAwait(false);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Error("앨범 이미지 읽기 실패", ex);
            return null;
        }
    }

    private static MediaPlaybackState MapStatus(GsmtcStatus? status) => status switch
    {
        GsmtcStatus.Playing => MediaPlaybackState.Playing,
        GsmtcStatus.Paused => MediaPlaybackState.Paused,
        GsmtcStatus.Stopped => MediaPlaybackState.Stopped,
        GsmtcStatus.Closed => MediaPlaybackState.Closed,
        GsmtcStatus.Opened => MediaPlaybackState.Opened,
        GsmtcStatus.Changing => MediaPlaybackState.Changing,
        _ => MediaPlaybackState.Unknown,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            Attach(null, null);
            if (_manager is not null)
            {
                try
                {
                    _manager.SessionsChanged -= OnManagerSessionsChanged;
                    _manager.CurrentSessionChanged -= OnManagerCurrentSessionChanged;
                }
                catch { /* 종료 중 */ }
                _manager = null;
            }
        }
    }
}

/// <summary>AppUserModelId → 사람이 읽을 수 있는 앱 이름, 그리고 앱 활성화.</summary>
public static class AppNameResolver
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["spotify"] = "Spotify",
        ["chrome"] = "Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["vlc"] = "VLC",
        ["whale"] = "Naver Whale",
        ["opera"] = "Opera",
        ["brave"] = "Brave",
        ["itunes"] = "iTunes",
        ["foobar2000"] = "foobar2000",
        ["potplayermini64"] = "PotPlayer",
        ["melon"] = "Melon",
    };

    public static string Resolve(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "알 수 없는 앱";
        lock (Cache)
        {
            if (Cache.TryGetValue(aumid, out var cached)) return cached;
        }

        string? name = null;
        if (aumid.Contains('!'))
        {
            // 패키지 앱(UWP/MSIX): Windows에 등록된 표시 이름 사용
            try
            {
                name = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(aumid)?.DisplayInfo?.DisplayName;
            }
            catch (Exception)
            {
                name = null;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            var raw = aumid;
            if (raw.Contains('!')) raw = raw[(raw.LastIndexOf('!') + 1)..];
            if (raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) raw = raw[..^4];
            raw = raw.Split('\\', '/').Last();
            name = Known.TryGetValue(raw, out var known) ? known
                : raw.Length > 0 ? char.ToUpperInvariant(raw[0]) + raw[1..] : aumid;
        }

        lock (Cache) Cache[aumid] = name;
        return name;
    }

    public static bool TryActivate(string aumid)
    {
        try
        {
            // 1) 실행 중인 Win32 프로세스(예: Spotify.exe, chrome)의 창을 앞으로
            var exe = aumid.Contains('!') ? null : Path.GetFileNameWithoutExtension(aumid.Split('\\', '/').Last());
            if (!string.IsNullOrEmpty(exe))
            {
                foreach (var p in Process.GetProcessesByName(exe))
                {
                    using (p)
                    {
                        var hwnd = p.MainWindowHandle;
                        if (hwnd == IntPtr.Zero) continue;
                        if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(hwnd);
                        return true;
                    }
                }
            }

            // 2) 패키지 앱은 셸을 통해 실행/활성화
            if (aumid.Contains('!'))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{aumid}") { UseShellExecute = true })?.Dispose();
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("재생 프로그램 열기 실패", ex);
        }
        return false;
    }
}
