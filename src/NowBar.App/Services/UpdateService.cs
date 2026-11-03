using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NowBar.App.Helpers;

namespace NowBar.App.Services;

/// <summary>NowBar가 설치된 형태. 받을 파일과 교체 방식이 다르다.</summary>
public enum InstallKind
{
    /// <summary>NowBar.exe 하나 (자체 포함 단일 파일) → 릴리스의 .exe를 받는다</summary>
    SingleFile,
    /// <summary>NowBar.exe + DLL 폴더 (.NET 런타임 사용) → 릴리스의 .zip을 받는다</summary>
    Folder,
}

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size, string? Sha256);

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>설치 형태에 맞는 파일 (단일 파일 → .exe, 폴더 → 런타임 필요 .zip).</summary>
    public ReleaseAsset? AssetFor(InstallKind kind) => Assets.FirstOrDefault(a => kind == InstallKind.SingleFile
        ? a.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)
        : a.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// GitHub 릴리스로 자동 업데이트.
/// 1) releases/latest API로 새 버전 확인 2) 설치 형태에 맞는 파일 다운로드
/// 3) SHA-256 확인 (GitHub가 제공하는 digest, 없으면 SHA256SUMS.txt)
/// 4) 실행 중인 파일은 덮어쓸 수 없지만 이름은 바꿀 수 있으므로, 기존 파일을 *.old로 바꾸고 새 파일을 넣는다.
///    실패하면 원래대로 되돌린다. *.old는 다음 실행 때 지운다.
/// </summary>
public sealed class UpdateService
{
    public const string Repository = "koozone7231-lgtm/NowBar";
    private const string OldSuffix = ".nowbar-old";

    private readonly HttpClient _http;

    public UpdateService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"NowBar/{CurrentVersion.ToString(3)} (+update-check)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static Version CurrentVersion =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>폴더 설치는 exe 옆에 NowBar.dll이 있고, 단일 파일은 exe 하나뿐이다.</summary>
    public static InstallKind DetectInstallKind() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "NowBar.dll")) ? InstallKind.Folder : InstallKind.SingleFile;

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    /// <summary>새 버전이 있으면 그 정보를, 없으면 null.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // 아직 릴리스 없음
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);
        var info = Parse(doc.RootElement);
        return info is not null && info.Version > CurrentVersion ? info : null;
    }

    public static UpdateInfo? Parse(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
        var tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || url is null) continue;
                var size = a.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                assets.Add(new ReleaseAsset(name, url, size, sha));
            }
        }

        return new UpdateInfo(Normalize(version), tag,
            release.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
            release.TryGetProperty("body", out var b) ? b.GetString() : null,
            assets);
    }

    /// <summary>설치 형태에 맞는 파일을 받고 SHA-256을 확인한다. 받은 파일 경로를 반환.</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, InstallKind kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var asset = info.AssetFor(kind) ?? throw new InvalidOperationException(
            kind == InstallKind.SingleFile ? "이 릴리스에는 실행 파일(.exe)이 없습니다." : "이 릴리스에는 zip 파일이 없습니다.");
        if (!asset.DownloadUrl.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("예상하지 못한 다운로드 주소입니다.");

        var dir = Path.Combine(Path.GetTempPath(), "NowBarUpdate", info.Version.ToString(3));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, asset.Name);

        using (var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        var expected = asset.Sha256 ?? await ReadSumAsync(info, asset.Name, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("파일 검증 정보(SHA-256)를 찾을 수 없어 업데이트를 중단했습니다.");
        var actual = await HashAsync(path, ct).ConfigureAwait(false);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("받은 파일이 손상되었습니다 (SHA-256 불일치). 업데이트를 중단했습니다.");
        }
        return path;
    }

    private async Task<string?> ReadSumAsync(UpdateInfo info, string fileName, CancellationToken ct)
    {
        var sums = info.Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (sums is null) return null;
        var text = await _http.GetStringAsync(sums.DownloadUrl, ct).ConfigureAwait(false);
        return ParseSums(text, fileName);
    }

    public static string? ParseSums(string text, string fileName)
    {
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 받은 파일로 설치본을 교체하고 다시 실행할 exe 경로를 반환한다.
    /// 앱이 실행 중이어도 동작한다 (기존 파일 이름을 바꾼 뒤 새 파일을 넣음). 실패하면 원래대로 되돌리고 예외를 던진다.
    /// </summary>
    public static string Apply(string downloadedPath, InstallKind kind, string? installDir = null, string? exePath = null)
    {
        installDir ??= AppContext.BaseDirectory;
        exePath ??= Environment.ProcessPath ?? Path.Combine(installDir, "NowBar.exe");

        var replacements = new List<(string Target, string Source)>();
        string? extractDir = null;
        if (kind == InstallKind.SingleFile)
        {
            replacements.Add((exePath, downloadedPath));
        }
        else
        {
            extractDir = Path.Combine(Path.GetDirectoryName(downloadedPath)!, "extracted");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
            ZipFile.ExtractToDirectory(downloadedPath, extractDir);
            if (!File.Exists(Path.Combine(extractDir, "NowBar.exe")))
                throw new InvalidOperationException("업데이트 파일에 NowBar.exe가 없습니다.");
            foreach (var file in Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories))
                replacements.Add((Path.Combine(installDir, Path.GetRelativePath(extractDir, file)), file));
        }

        var done = new List<(string Target, bool HadOriginal)>();
        try
        {
            foreach (var (target, source) in replacements)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var old = target + OldSuffix;
                if (File.Exists(old)) File.Delete(old);
                var hadOriginal = File.Exists(target);
                if (hadOriginal) File.Move(target, old); // 실행 중인 파일도 이름은 바꿀 수 있다
                done.Add((target, hadOriginal));
                File.Copy(source, target, overwrite: true);
            }
        }
        catch
        {
            // 되돌리기
            foreach (var (target, hadOriginal) in Enumerable.Reverse(done))
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    if (hadOriginal) File.Move(target + OldSuffix, target);
                }
                catch (Exception ex) { Logger.Error($"업데이트 되돌리기 실패: {target}", ex); }
            }
            throw;
        }
        finally
        {
            if (extractDir is not null)
                try { Directory.Delete(extractDir, recursive: true); } catch { /* 임시 폴더 */ }
        }

        Logger.Info($"업데이트 파일 교체 완료 ({replacements.Count}개)");
        return exePath;
    }

    /// <summary>이전 업데이트에서 남은 *.old 파일 정리 (새 버전이 시작된 뒤 호출).</summary>
    public static void CleanupOldFiles(string? installDir = null)
    {
        installDir ??= AppContext.BaseDirectory;
        try
        {
            foreach (var file in Directory.EnumerateFiles(installDir, "*" + OldSuffix, SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* 아직 사용 중이면 다음에 */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("이전 업데이트 파일 정리 실패", ex);
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
