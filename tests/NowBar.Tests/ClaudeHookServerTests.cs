using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using NowBar.App.Models;
using NowBar.App.Services;

namespace NowBar.Tests;

public sealed class ClaudeHookServerTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static HttpRequestMessage Post(int port, string body, string? token, bool withOrigin = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, ClaudeHookServer.BuildUrl(port))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (withOrigin) req.Headers.Add("Origin", "https://evil.example");
        return req;
    }

    [Fact]
    public async Task Accepts_authorized_events_and_rejects_others()
    {
        var port = FreePort();
        await using var server = new ClaudeHookServer(() => "good-token");
        var received = new List<ClaudeHookEvent>();
        server.EventReceived += (_, e) => { lock (received) received.Add(e); };

        Assert.True(await server.StartAsync(port), server.LastError);
        using var http = new HttpClient();
        const string body = """{"hook_event_name":"Stop","session_id":"s1","cwd":"C:\\p\\demo","stop_hook_active":false}""";

        using (var ok = await http.SendAsync(Post(port, body, "good-token")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            // 빈 본문이어야 Claude Code가 '결정 없음'으로 처리한다
            Assert.Equal("", await ok.Content.ReadAsStringAsync());
        }

        using (var noAuth = await http.SendAsync(Post(port, body, null)))
            Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        using (var wrong = await http.SendAsync(Post(port, body, "bad-token")))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using (var browser = await http.SendAsync(Post(port, body, "good-token", withOrigin: true)))
            Assert.Equal(HttpStatusCode.Forbidden, browser.StatusCode);

        using (var bad = await http.SendAsync(Post(port, "not json", "good-token")))
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        Assert.Single(received);
        Assert.Equal("Stop", received[0].EventName);
        Assert.Equal("s1", received[0].SessionId);
    }

    [Fact]
    public async Task Reports_port_in_use()
    {
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            await using var server = new ClaudeHookServer(() => "t");
            Assert.False(await server.StartAsync(port));
            Assert.Contains(port.ToString(), server.LastError);
            Assert.False(server.IsRunning);
        }
        finally
        {
            blocker.Stop();
        }
    }
}
