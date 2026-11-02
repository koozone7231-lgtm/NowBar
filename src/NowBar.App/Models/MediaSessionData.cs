namespace NowBar.App.Models;

public enum MediaPlaybackState { Unknown, Closed, Opened, Changing, Stopped, Playing, Paused }

/// <summary>세션 선택 목록에 표시하는 미디어 세션 정보.</summary>
public sealed record MediaSessionInfo(string Key, string SourceAppId, string AppName);

/// <summary>Windows 시스템 미디어 세션(GSMTC)에서 읽어온 현재 상태의 스냅샷.</summary>
public sealed class MediaSessionData
{
    public string Key { get; init; } = "";
    public string SourceAppId { get; init; } = "";
    public string AppName { get; init; } = "";

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumTitle { get; init; }
    public byte[]? ThumbnailBytes { get; init; }
    /// <summary>제목/아티스트가 바뀌었는지 판단하는 키(썸네일 재생성 여부 결정).</summary>
    public string MediaKey { get; init; } = "";

    public MediaPlaybackState State { get; init; }
    public double PlaybackRate { get; init; } = 1.0;

    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset TimelineUpdatedAt { get; init; }

    public bool CanPlayPause { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanSeek { get; init; }

    /// <summary>세션은 있으나 정보를 읽는 데 실패한 경우.</summary>
    public bool ReadFailed { get; init; }

    /// <summary>
    /// 앱이 타임라인을 자주 갱신하지 않으므로(예: 일부 플레이어는 곡 변경 시에만 갱신)
    /// 마지막 갱신 시각과 재생 속도로 현재 위치를 추정한다.
    /// </summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var pos = Position;
        if (State == MediaPlaybackState.Playing && TimelineUpdatedAt.Year > 2000)
        {
            var elapsed = now - TimelineUpdatedAt;
            if (elapsed > TimeSpan.Zero)
                pos += TimeSpan.FromTicks((long)(elapsed.Ticks * (PlaybackRate <= 0 ? 1.0 : PlaybackRate)));
        }
        if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
        return pos;
    }
}
