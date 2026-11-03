using NowBar.App.Models;
using NowBar.App.Services;
using Xunit.Abstractions;

namespace NowBar.Tests;

public sealed class ClipboardAndMonitorTests(ITestOutputHelper output)
{
    private static ClipboardEntry Text(string s) => new() { Kind = ClipboardKind.Text, Text = s, Key = "text:" + s };
    private static ClipboardEntry Image(int n) => new() { Kind = ClipboardKind.Image, Key = "image:" + n };

    [Fact]
    public void History_dedupes_and_moves_to_top()
    {
        var h = new ClipboardHistory();
        h.Add(Text("a"));
        h.Add(Text("b"));
        h.Add(Text("a"));
        Assert.Equal(["text:a", "text:b"], h.Items.Select(e => e.Key));
    }

    [Fact]
    public void History_keeps_twenty_unpinned_and_all_pinned()
    {
        var h = new ClipboardHistory();
        h.Add(Text("pinned"));
        h.TogglePin(h.Items[0]);
        for (var i = 0; i < 30; i++) h.Add(Text($"t{i}"));

        Assert.Equal(ClipboardHistory.MaxUnpinned, h.Items.Count(e => !e.Pinned));
        Assert.Contains(h.Items, e => e.Key == "text:pinned");
        Assert.Equal("text:t29", h.Items[0].Key);
        Assert.DoesNotContain(h.Items, e => e.Key == "text:t9"); // 오래된 것부터 빠진다

        // 같은 내용을 다시 복사해도 고정은 유지
        h.Add(Text("pinned"));
        Assert.True(h.Items[0].Pinned);

        h.ClearUnpinned();
        Assert.Single(h.Items);
    }

    [Fact]
    public void History_limits_unpinned_images()
    {
        var h = new ClipboardHistory();
        for (var i = 0; i < 8; i++) h.Add(Image(i));
        h.Add(Text("x"));
        Assert.Equal(ClipboardHistory.MaxUnpinnedImages, h.Items.Count(e => e.Kind == ClipboardKind.Image));
        Assert.Equal("image:7", h.Items[1].Key);
    }

    [Theory]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789", true)]
    [InlineData("ghp_ABCdef1234567890abcdefGHIJKL123456", true)]
    [InlineData("AKIAIOSFODNN7EXAMPLE", true)]
    [InlineData("password: hunter2!!", true)]
    [InlineData("api_key=12345abcdef", true)]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc", true)]
    [InlineData("Xk9mQ2vL7pR4tW8zB3nC6yH1", true)] // 공백 없는 무작위 24자
    [InlineData("안녕하세요 오늘 회의는 3시입니다", false)]
    [InlineData("https://github.com/koozone7231-lgtm/NowBar", false)]
    [InlineData("ClipboardHistory", false)]
    [InlineData("C:\\Users\\User\\Documents\\report.docx", false)]
    [InlineData("hello", false)]
    public void Secret_detection(string text, bool secret) => Assert.Equal(secret, ClipboardHistory.LooksSecret(text));

    [Theory]
    [InlineData("https://example.com/a?b=1", true)]
    [InlineData("  http://localhost:3000  ", true)]
    [InlineData("example.com", false)]
    [InlineData("see https://example.com", false)]
    [InlineData("file:///C:/x.txt", false)]
    public void Link_detection(string text, bool link) => Assert.Equal(link, ClipboardHistory.IsLink(text));

    [Fact]
    public void Entry_preview_search_and_age()
    {
        var e = Text("첫 줄\n\n   둘째   줄");
        Assert.Equal("첫 줄 둘째 줄", e.Preview);
        Assert.True(e.Matches("둘째"));
        Assert.True(e.Matches(""));
        Assert.False(e.Matches("셋째"));
        Assert.Equal("방금", ClipboardEntry.FormatAge(TimeSpan.FromSeconds(5)));
        Assert.Equal("3분 전", ClipboardEntry.FormatAge(TimeSpan.FromMinutes(3.5)));
        Assert.Equal("2시간 전", ClipboardEntry.FormatAge(TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Load_tracker_needs_sustained_cpu_and_releases_with_hysteresis()
    {
        var t = new LoadTracker();
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);
        Assert.False(t.Add(95, 50, t0));
        Assert.False(t.Add(95, 50, t0.AddSeconds(20)));
        Assert.False(t.IsStrained);
        Assert.True(t.Add(95, 50, t0.AddSeconds(30)));
        Assert.True(t.IsStrained);
        Assert.True(t.IsCpuStrained);

        // 85%는 아직 부담 (80% 아래로 내려가야 풀림)
        Assert.False(t.Add(85, 50, t0.AddSeconds(40)));
        Assert.True(t.IsStrained);
        Assert.True(t.Add(70, 50, t0.AddSeconds(50)));
        Assert.False(t.IsStrained);

        // 잠깐 튄 CPU는 무시, 연속이 끊기면 처음부터
        t.Add(95, 50, t0.AddSeconds(60));
        t.Add(85, 50, t0.AddSeconds(75));
        t.Add(95, 50, t0.AddSeconds(80));
        t.Add(95, 50, t0.AddSeconds(100));
        Assert.False(t.IsStrained);

        // 메모리 반응은 기본 꺼짐
        t.Add(10, 99, t0.AddSeconds(102));
        Assert.False(t.Add(10, 99, t0.AddSeconds(108)));
        Assert.False(t.IsStrained);
        t = new LoadTracker { IncludeMemory = true };

        // 메모리: 90%대 초반은 평범한 상태, 95% 이상이 30초 이어져야 부담
        Assert.False(t.Add(10, 93, t0.AddSeconds(110)));
        t.Add(10, 96, t0.AddSeconds(120));
        Assert.False(t.IsStrained);
        Assert.True(t.Add(10, 96, t0.AddSeconds(150)));
        Assert.False(t.IsCpuStrained);
        Assert.False(t.Add(10, 92, t0.AddSeconds(160))); // 90% 아래로 내려가야 풀림
        Assert.True(t.Add(10, 85, t0.AddSeconds(170)));
    }

    [Fact]
    public void Formatting()
    {
        Assert.Equal("512 B/s", SystemMonitorService.FormatRate(512));
        Assert.Equal("2 KB/s", SystemMonitorService.FormatRate(2048));
        Assert.Equal("1.5 MB/s", SystemMonitorService.FormatRate(1.5 * 1024 * 1024));
        Assert.Equal("16 GB", SystemMonitorService.FormatGb(16d * 1024 * 1024 * 1024));
    }

    /// <summary>실제 PC 값을 읽어 범위만 확인 (읽기 전용).</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Real_system_samples_are_in_range()
    {
        var svc = new SystemMonitorService();
        svc.Sample();
        svc.TopApps(2);
        await Task.Delay(1000);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var s = svc.Sample();
        var sampleMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var top = svc.TopApps(2);
        var topMs = sw.Elapsed.TotalMilliseconds;
        var disk = SystemMonitorService.GetSystemDisk();

        output.WriteLine($"CPU {s.CpuPercent:0.0}% · 메모리 {s.MemoryPercent:0.0}% ({SystemMonitorService.FormatGb(s.MemoryUsedBytes)}/{SystemMonitorService.FormatGb(s.MemoryTotalBytes)}) · " +
                         $"↓{SystemMonitorService.FormatRate(s.DownBytesPerSec)} ↑{SystemMonitorService.FormatRate(s.UpBytesPerSec)}");
        output.WriteLine($"디스크 {disk?.Name} {disk?.UsedPercent:0.0}% · 상위 앱: {string.Join(", ", top.Select(a => $"{a.Name} {a.CpuPercent:0.0}%"))}");
        output.WriteLine($"측정 시간: Sample {sampleMs:0.0}ms · TopApps {topMs:0.0}ms");

        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.InRange(s.MemoryPercent, 1, 100);
        Assert.True(s.MemoryTotalBytes > 0);
        Assert.True(s.DownBytesPerSec >= 0 && s.UpBytesPerSec >= 0);
        Assert.NotNull(disk);
        Assert.InRange(disk!.Value.UsedPercent, 0, 100);
        Assert.All(top, a => Assert.InRange(a.CpuPercent, 0, 100));
    }
}
