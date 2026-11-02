using System.Text.RegularExpressions;

namespace NowBar.App.Helpers;

/// <summary>화면 표시용 텍스트를 한 줄로 줄이고, 비밀값처럼 보이는 문자열을 가린다.</summary>
public static partial class TextSanitizer
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // API 키·토큰·비밀번호 형태로 보이는 값
    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|authorization)\b\s*[:=]\s*\S+|\b(sk|pk|ghp|gho|xox[abp])[-_][A-Za-z0-9_\-]{8,}|\b[A-Za-z0-9_\-]{32,}\b")]
    private static partial Regex SecretLike();

    public static string? OneLine(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = Whitespace().Replace(text, " ").Trim();
        s = SecretLike().Replace(s, "•••");
        if (maxLength > 1 && s.Length > maxLength) s = s[..(maxLength - 1)].TrimEnd() + "…";
        return s;
    }
}
