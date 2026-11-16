using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace NowBar.App.Helpers;

/// <summary>
/// 짧은 답·변경 기록을 TextBlock에 보여 주기 위한 아주 작은 마크다운: **굵게**, `코드`, [링크](주소),
/// "- " 목록, "#" 제목, ``` 코드 블록. 그 밖의 문법은 글자 그대로 둔다.
/// </summary>
public static partial class MarkdownLite
{
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Malgun Gothic");

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`|\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex InlineToken();

    /// <summary>한 줄 안의 굵게·코드·링크.</summary>
    public static IEnumerable<Inline> FormatInline(string text)
    {
        var last = 0;
        foreach (Match m in InlineToken().Matches(text))
        {
            if (m.Index > last) yield return new Run(text[last..m.Index]);
            if (m.Groups[1].Success) yield return new Bold(new Run(m.Groups[1].Value));
            else if (m.Groups[2].Success) yield return new Run(m.Groups[2].Value) { FontFamily = Mono };
            else yield return new Run(m.Groups[3].Value);
            last = m.Index + m.Length;
        }
        if (last < text.Length) yield return new Run(text[last..]);
    }

    /// <summary>여러 줄 글을 TextBlock 하나에 넣을 인라인으로 (줄바꿈 포함).</summary>
    public static List<Inline> ToInlines(string text)
    {
        var result = new List<Inline>();
        var inCode = false;
        var first = true;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (!first) result.Add(new LineBreak());
            first = false;

            if (inCode)
            {
                result.Add(new Run(line) { FontFamily = Mono });
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var indent = line.Length - trimmed.Length;
                result.Add(new Run(new string(' ', indent) + "• "));
                result.AddRange(FormatInline(trimmed[2..]));
            }
            else if (trimmed.StartsWith('#'))
            {
                result.Add(new Bold(new Run(trimmed.TrimStart('#').Trim())));
            }
            else
            {
                result.AddRange(FormatInline(line));
            }
        }
        return result;
    }
}
