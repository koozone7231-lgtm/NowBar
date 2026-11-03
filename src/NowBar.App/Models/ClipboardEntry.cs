using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace NowBar.App.Models;

public enum ClipboardKind { Text, Link, Image, Files }

/// <summary>클립보드 기록 한 항목. 메모리에만 보관한다.</summary>
public sealed class ClipboardEntry
{
    public required ClipboardKind Kind { get; init; }
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public BitmapSource? Thumbnail { get; init; }
    public StringCollection? Files { get; init; }
    /// <summary>같은 내용인지 비교하는 키 (텍스트 그대로 / 이미지 크기+해시 / 파일 목록).</summary>
    public required string Key { get; init; }
    public DateTime CopiedAt { get; set; } = DateTime.Now;
    public bool Pinned { get; set; }

    public string Preview => Kind switch
    {
        ClipboardKind.Image => Image is { } img ? $"이미지 {img.PixelWidth}×{img.PixelHeight}" : "이미지",
        ClipboardKind.Files => Files is { Count: > 0 } f ? (f.Count == 1 ? Path.GetFileName(f[0]) ?? "파일" : $"{Path.GetFileName(f[0])} 외 {f.Count - 1}개") : "파일",
        _ => OneLine(Text ?? "", 90),
    };

    public string KindIcon => Kind switch
    {
        ClipboardKind.Link => "ExternalLink",
        ClipboardKind.Files => "Folder",
        _ => "Clipboard",
    };

    public string TimeText => FormatAge(DateTime.Now - CopiedAt);

    public static string FormatAge(TimeSpan age) => age.TotalSeconds switch
    {
        < 60 => "방금",
        < 3600 => $"{(int)age.TotalMinutes}분 전",
        < 86400 => $"{(int)age.TotalHours}시간 전",
        _ => $"{(int)age.TotalDays}일 전",
    };

    /// <summary>검색어가 미리보기·전체 텍스트·파일 경로에 들어 있는지 (대소문자 무시).</summary>
    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        return (Text?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false)
            || (Files?.Cast<string>().Any(f => f.Contains(q, StringComparison.CurrentCultureIgnoreCase)) ?? false)
            || Preview.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string OneLine(string text, int max)
    {
        var s = Regex.Replace(text, @"\s+", " ").Trim();
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }
}

/// <summary>
/// 기록 목록 규칙 (UI와 분리된 순수 로직):
/// 최근 것이 위, 같은 내용은 하나로(위로 올림), 고정 항목은 개수 제한·지우기에서 제외.
/// </summary>
public sealed class ClipboardHistory
{
    public const int MaxUnpinned = 20;
    public const int MaxUnpinnedImages = 5;
    private readonly List<ClipboardEntry> _items = [];

    public IReadOnlyList<ClipboardEntry> Items => _items;

    /// <summary>추가하고, 이미 있던 내용이면 위로 올린다. 목록이 바뀌었으면 true.</summary>
    public bool Add(ClipboardEntry entry)
    {
        var existing = _items.FindIndex(e => e.Key == entry.Key);
        if (existing >= 0)
        {
            var old = _items[existing];
            _items.RemoveAt(existing);
            entry.Pinned = old.Pinned;
        }
        _items.Insert(0, entry);

        // 고정하지 않은 항목은 최근 MaxUnpinned개만, 그중 이미지는 메모리를 많이 쓰므로 MaxUnpinnedImages개만 유지
        int unpinned = 0, images = 0;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Pinned) continue;
            var tooManyImages = _items[i].Kind == ClipboardKind.Image && ++images > MaxUnpinnedImages;
            if (++unpinned > MaxUnpinned || tooManyImages)
            {
                _items.RemoveAt(i--);
                unpinned--;
            }
        }
        return true;
    }

    public void TogglePin(ClipboardEntry entry) => entry.Pinned = !entry.Pinned;

    public void Remove(ClipboardEntry entry) => _items.Remove(entry);

    /// <summary>고정하지 않은 항목만 지운다.</summary>
    public void ClearUnpinned() => _items.RemoveAll(e => !e.Pinned);

    public void ClearAll() => _items.Clear();

    // API 키·토큰·비밀번호처럼 보이는 문자열 — 기록하지 않는다
    private static readonly Regex SecretLike = new(
        @"^(sk|pk|rk|ghp|gho|ghs|github_pat|xox[abprs]|AKIA|AIza|eyJ)[A-Za-z0-9_\-\.]{10,}$|" +
        @"(?i)\b(password|passwd|pwd|secret|api[_-]?key|token)\s*[:=]\s*\S+",
        RegexOptions.Compiled);

    /// <summary>비밀값처럼 보이는지: 알려진 키 형식, "password=..." 형태, 공백 없는 긴 무작위 문자열.</summary>
    public static bool LooksSecret(string text)
    {
        var s = text.Trim();
        if (s.Length is < 12 or > 2048) return s.Length >= 12 && SecretLike.IsMatch(s);
        if (SecretLike.IsMatch(s)) return true;
        if (s.Contains(' ') || s.Contains('\n') || s.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (s.Length < 24) return false;
        // 대문자·소문자·숫자를 모두 섞은 24자 이상 한 덩어리 → 키/토큰일 가능성이 높다
        var upper = s.Any(char.IsUpper);
        var lower = s.Any(char.IsLower);
        var digit = s.Any(char.IsDigit);
        return upper && lower && digit;
    }

    public static bool IsLink(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !text.Trim().Contains(' ');
}
