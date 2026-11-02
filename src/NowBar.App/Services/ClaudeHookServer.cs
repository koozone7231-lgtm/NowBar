using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NowBar.App.Helpers;
using NowBar.App.Models;

namespace NowBar.App.Services;

/// <summary>
/// Claude Code의 HTTP Hook을 받는 로컬 서버 (Kestrel Minimal API).
/// - 127.0.0.1 에만 바인딩 (외부 네트워크 노출 없음)
/// - Authorization: Bearer {토큰} 검사 (고정 시간 비교)
/// - 브라우저발 요청(Origin 헤더 포함)은 거부
/// - 항상 빈 본문의 2xx로 응답 → Claude Code는 "결정 없음/성공"으로 처리하므로 Claude 동작에 영향을 주지 않는다
/// </summary>
public sealed class ClaudeHookServer : IAsyncDisposable
{
    public const string HookPath = "/api/claude-hook";
    public const string HealthPath = "/api/health";

    private readonly Func<string> _tokenProvider;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }

    public event EventHandler<ClaudeHookEvent>? EventReceived;
    public event EventHandler? StatusChanged;

    public ClaudeHookServer(Func<string> tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public static string BuildUrl(int port) => $"http://127.0.0.1:{port}{HookPath}";

    public async Task<bool> StartAsync(int port)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            try
            {
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    ContentRootPath = AppContext.BaseDirectory,
                    EnvironmentName = "Production",
                    Args = [],
                });
                builder.Logging.ClearProviders();
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, port);
                    options.AddServerHeader = false;
                    // PostToolUse에는 도구 결과 전체가 포함될 수 있어 여유 있게 허용 (읽은 뒤 바로 폐기)
                    options.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
                });

                var app = builder.Build();
                app.MapGet(HealthPath, () => Results.Json(new { app = "NowBar", ok = true }));
                // Delegate로 캐스팅해야 반환한 IResult(상태 코드)가 실제 응답에 기록된다
                app.MapPost(HookPath, (Delegate)HandleHookAsync);

                await app.StartAsync().ConfigureAwait(false);
                _app = app;
                Port = port;
                IsRunning = true;
                LastError = null;
                Logger.Info($"Claude Hook 서버 시작: {BuildUrl(port)}");
                return true;
            }
            catch (Exception ex)
            {
                IsRunning = false;
                LastError = IsAddressInUse(ex)
                    ? $"포트 {port}을(를) 다른 프로그램이 사용 중입니다. 설정에서 포트를 변경하세요."
                    : $"서버를 시작할 수 없습니다: {ex.Message}";
                Logger.Error("Claude Hook 서버 시작 실패", ex);
                return false;
            }
        }
        finally
        {
            _lifecycle.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            LastError = null;
        }
        finally
        {
            _lifecycle.Release();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task StopCoreAsync()
    {
        var app = _app;
        _app = null;
        IsRunning = false;
        if (app is null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await app.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { Logger.Error("Claude Hook 서버 중지 중 오류", ex); }
        finally { await app.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task<IResult> HandleHookAsync(HttpContext ctx)
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (remote is null || !IPAddress.IsLoopback(remote))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        // 웹 페이지가 로컬 서버로 요청을 보내는 것을 차단 (Claude Code는 Origin을 보내지 않음)
        if (ctx.Request.Headers.ContainsKey("Origin"))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!IsAuthorized(ctx.Request.Headers.Authorization.ToString()))
            return Results.StatusCode(StatusCodes.Status401Unauthorized);

        ClaudeHookEvent? evt;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, default, ctx.RequestAborted).ConfigureAwait(false);
            evt = ClaudeHookEvent.FromJson(doc.RootElement);
        }
        catch (JsonException)
        {
            return Results.StatusCode(StatusCodes.Status400BadRequest);
        }

        if (evt is null)
            return Results.StatusCode(StatusCodes.Status400BadRequest);

        LastEventAt = evt.ReceivedAt;
        // 진단용: 이벤트 이름/도구 이름만 기록 (프롬프트·명령·파일 내용은 기록하지 않음)
        Logger.Info($"Hook 수신: {evt.EventName}{(evt.ToolName is null ? "" : " / " + evt.ToolName)} (세션 {evt.SessionId[..Math.Min(8, evt.SessionId.Length)]})");
        try { EventReceived?.Invoke(this, evt); }
        catch (Exception ex) { Logger.Error("Hook 이벤트 처리 중 오류", ex); }

        // 빈 본문 200 = Claude Code에 아무 결정도 전달하지 않음
        return Results.Ok();
    }

    private bool IsAuthorized(string header)
    {
        const string prefix = "Bearer ";
        var token = _tokenProvider();
        if (string.IsNullOrEmpty(token) || !header.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var given = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        var expected = Encoding.UTF8.GetBytes(token);
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.GetType().Name == "AddressInUseException") return true;
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }) return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }
}
