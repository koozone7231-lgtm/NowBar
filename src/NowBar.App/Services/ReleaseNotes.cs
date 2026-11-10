using System.Reflection;
using System.Text.RegularExpressions;

namespace NowBar.App.Services;

/// <summary>변경 기록의 한 버전 (예: "## v1.5.0 — 2026-11-06" 아래 내용).</summary>
public sealed record ReleaseNoteSection(Version Version, string Date, IReadOnlyList<string> Lines);

/// <summary>
/// 업데이트 후 "새로운 기능" 창에 보여 줄 내용. 앱에 함께 넣은 CHANGELOG.md에서 읽으므로 인터넷이 없어도 된다.
/// 다운로드 표·자동 업데이트 안내처럼 이미 설치한 사람에게 필요 없는 부분은 뺀다.
/// </summary>
public static partial class ReleaseNotes
{
    /// <summary>한 번에 보여 줄 최대 버전 수 (오래 업데이트하지 않았을 때).</summary>
    public const int MaxSections = 5;

    [GeneratedRegex(@"^##\s+v(?<ver>\d+\.\d+(\.\d+)?)\s*(—|-)?\s*(?<date>.*)$")]
    private static partial Regex VersionHeading();

    public static IReadOnlyList<ReleaseNoteSection> Parse(string changelog)
    {
        var sections = new List<ReleaseNoteSection>();
        Version? version = null;
        var date = "";
        var lines = new List<string>();
        var skipping = false;

        void Flush()
        {
            if (version is null) return;
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            sections.Add(new ReleaseNoteSection(version, date, lines.ToArray()));
        }

        foreach (var raw in changelog.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var heading = VersionHeading().Match(line);
            if (heading.Success)
            {
                Flush();
                version = Version.Parse(heading.Groups["ver"].Value);
                date = heading.Groups["date"].Value.Trim();
                lines = [];
                skipping = false;
                continue;
            }
            if (version is null) continue;

            if (line.StartsWith("### "))
            {
                // 설치한 사람에게는 다운로드 표가 필요 없다
                skipping = line.Contains("다운로드");
                if (skipping) continue;
            }
            if (skipping || line.Contains("자동으로 업데이트됩니다")) continue;
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        Flush();
        return sections;
    }

    /// <summary>previous(제외) 이후부터 current(포함)까지의 버전, 새 버전이 위로. previous를 모르면 current만.</summary>
    public static IReadOnlyList<ReleaseNoteSection> Between(IReadOnlyList<ReleaseNoteSection> all, Version? previous, Version current)
    {
        var picked = all
            .Where(s => Normalize(s.Version) <= Normalize(current)
                        && (previous is null ? Normalize(s.Version) == Normalize(current) : Normalize(s.Version) > Normalize(previous)))
            .OrderByDescending(s => s.Version)
            .Take(MaxSections)
            .ToList();
        return picked;
    }

    /// <summary>"1.5" 와 "1.5.0" 을 같게 비교.</summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>앱에 넣어 둔 CHANGELOG.md. 없으면 빈 문자열.</summary>
    public static string LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 업데이트 후 처음 실행인지 판단해 보여 줄 내용을 돌려준다 (없으면 빈 목록).
    /// lastSeen이 비어 있으면(이 기능 이전 버전에서 올라옴) 업데이트 직후 실행일 때만 현재 버전 내용을 보여 준다.
    /// </summary>
    public static IReadOnlyList<ReleaseNoteSection> ToShow(string changelog, string? lastSeen, Version current, bool afterUpdate)
    {
        Version? previous = null;
        if (!string.IsNullOrWhiteSpace(lastSeen) && Version.TryParse(lastSeen, out var parsed)) previous = parsed;

        if (previous is null && !afterUpdate) return []; // 새로 설치: 보여 줄 "변경"이 없다
        if (previous is not null && Normalize(previous) >= Normalize(current)) return []; // 이미 봤거나 이전 버전으로 되돌림
        return Between(Parse(changelog), previous, current);
    }
}
