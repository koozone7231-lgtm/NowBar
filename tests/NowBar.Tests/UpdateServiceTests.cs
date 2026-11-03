using System.IO.Compression;
using System.Text.Json;
using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nowbar-update-" + Guid.NewGuid().ToString("N"));

    public UpdateServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.0", "draft": false, "prerelease": false,
          "html_url": "https://github.com/koozone7231-lgtm/NowBar/releases/tag/v1.2.0",
          "body": "notes",
          "assets": [
            { "name": "NowBar-1.2.0-win-x64.exe", "size": 10, "digest": "sha256:AABB",
              "browser_download_url": "https://github.com/koozone7231-lgtm/NowBar/releases/download/v1.2.0/NowBar-1.2.0-win-x64.exe" },
            { "name": "NowBar-1.2.0-win-x64-standalone.zip", "size": 10,
              "browser_download_url": "https://github.com/koozone7231-lgtm/NowBar/releases/download/v1.2.0/NowBar-1.2.0-win-x64-standalone.zip" },
            { "name": "NowBar-1.2.0-win-x64.zip", "size": 10,
              "browser_download_url": "https://github.com/koozone7231-lgtm/NowBar/releases/download/v1.2.0/NowBar-1.2.0-win-x64.zip" }
          ]
        }
        """;

    [Fact]
    public void Parses_release_and_picks_asset_by_install_kind()
    {
        using var doc = JsonDocument.Parse(ReleaseJson);
        var info = UpdateService.Parse(doc.RootElement)!;
        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.Equal("NowBar-1.2.0-win-x64.exe", info.AssetFor(InstallKind.SingleFile)!.Name);
        Assert.Equal("aabb", info.AssetFor(InstallKind.SingleFile)!.Sha256);
        // 폴더 설치는 런타임 필요 zip (standalone zip 아님)
        Assert.Equal("NowBar-1.2.0-win-x64.zip", info.AssetFor(InstallKind.Folder)!.Name);
    }

    [Fact]
    public void Drafts_and_prereleases_are_ignored()
    {
        using var doc = JsonDocument.Parse("""{ "tag_name": "v9.0.0", "prerelease": true, "assets": [] }""");
        Assert.Null(UpdateService.Parse(doc.RootElement));
    }

    [Fact]
    public void Parses_sha256sums_file()
    {
        const string sums = "abc123  NowBar-1.2.0-win-x64.zip\r\ndef456  NowBar-1.2.0-win-x64.exe\n";
        Assert.Equal("def456", UpdateService.ParseSums(sums, "NowBar-1.2.0-win-x64.exe"));
        Assert.Null(UpdateService.ParseSums(sums, "missing.zip"));
    }

    [Fact]
    public void Folder_update_replaces_files_and_old_files_are_cleaned_up()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "NowBar.exe"), "old exe");
        File.WriteAllText(Path.Combine(install, "NowBar.dll"), "old dll");
        File.WriteAllText(Path.Combine(install, "keep.txt"), "user file");

        var staging = Path.Combine(_dir, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "NowBar.exe"), "new exe");
        File.WriteAllText(Path.Combine(staging, "NowBar.dll"), "new dll");
        File.WriteAllText(Path.Combine(staging, "NAudio.Core.dll"), "added");
        var zip = Path.Combine(_dir, "dl", "NowBar-1.2.0-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        ZipFile.CreateFromDirectory(staging, zip);

        // 실행 중인 exe처럼 열어 둔 상태에서도 교체되어야 한다 (이름 바꾸기는 허용됨)
        using (new FileStream(Path.Combine(install, "NowBar.exe"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            var exe = UpdateService.Apply(zip, InstallKind.Folder, install, Path.Combine(install, "NowBar.exe"));
            Assert.Equal(Path.Combine(install, "NowBar.exe"), exe);
        }

        Assert.Equal("new exe", File.ReadAllText(Path.Combine(install, "NowBar.exe")));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(install, "NowBar.dll")));
        Assert.Equal("added", File.ReadAllText(Path.Combine(install, "NAudio.Core.dll")));
        Assert.Equal("user file", File.ReadAllText(Path.Combine(install, "keep.txt")));
        Assert.NotEmpty(Directory.GetFiles(install, "*.nowbar-old"));

        UpdateService.CleanupOldFiles(install);
        Assert.Empty(Directory.GetFiles(install, "*.nowbar-old"));
    }

    [Fact]
    public void Single_file_update_replaces_exe()
    {
        var install = Path.Combine(_dir, "single");
        Directory.CreateDirectory(install);
        var exe = Path.Combine(install, "NowBar.exe");
        File.WriteAllText(exe, "old");
        var downloaded = Path.Combine(_dir, "NowBar-1.2.0-win-x64.exe");
        File.WriteAllText(downloaded, "new");

        UpdateService.Apply(downloaded, InstallKind.SingleFile, install, exe);
        Assert.Equal("new", File.ReadAllText(exe));
    }

    [Fact]
    public void Zip_without_exe_is_rejected_and_install_is_untouched()
    {
        var install = Path.Combine(_dir, "install2");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "NowBar.exe"), "old exe");
        var staging = Path.Combine(_dir, "bad");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "readme.txt"), "not an app");
        var zip = Path.Combine(_dir, "bad.zip");
        ZipFile.CreateFromDirectory(staging, zip);

        Assert.Throws<InvalidOperationException>(() => UpdateService.Apply(zip, InstallKind.Folder, install, Path.Combine(install, "NowBar.exe")));
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "NowBar.exe")));
    }

    [Fact]
    public void Failed_copy_rolls_back_already_replaced_files()
    {
        var install = Path.Combine(_dir, "install3");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "NowBar.exe"), "old exe");
        File.WriteAllText(Path.Combine(install, "z.dll"), "old z");

        var staging = Path.Combine(_dir, "staging3");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "NowBar.exe"), "new exe");
        File.WriteAllText(Path.Combine(staging, "z.dll"), "new z");
        var zip = Path.Combine(_dir, "dl3", "u.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        ZipFile.CreateFromDirectory(staging, zip);

        // z.dll을 이름 바꾸기조차 못 하게 잠가서 중간 실패를 만든다
        using (new FileStream(Path.Combine(install, "z.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => UpdateService.Apply(zip, InstallKind.Folder, install, Path.Combine(install, "NowBar.exe")));
        }
        Assert.Equal("old exe", File.ReadAllText(Path.Combine(install, "NowBar.exe")));
        Assert.Equal("old z", File.ReadAllText(Path.Combine(install, "z.dll")));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Reads_latest_release_from_github()
    {
        // 현재 버전 이하만 있으면 null, 더 새로우면 정보 — 어느 쪽이든 예외 없이 응답해야 한다
        var svc = new UpdateService();
        var info = await svc.CheckAsync();
        if (info is not null) Assert.True(info.Version > new Version(0, 0, 0));
    }
}
