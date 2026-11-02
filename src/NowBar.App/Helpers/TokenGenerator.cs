using System.Security.Cryptography;

namespace NowBar.App.Helpers;

public static class TokenGenerator
{
    /// <summary>256비트 난수를 URL-safe Base64로 인코딩한 인증 토큰을 만든다.</summary>
    public static string Create() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Mask(string? token) =>
        string.IsNullOrEmpty(token) ? "(없음)" : token.Length <= 4 ? "••••" : "••••" + token[^4..];
}
