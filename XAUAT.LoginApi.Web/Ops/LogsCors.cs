namespace XAUAT.LoginApi.Ops;

/// <summary>
/// <c>/Logs</c> 的跨域白名单。
/// <para>
/// 逐字移植 Flask 的 <c>_is_allowed_cors_origin</c>：命中 <c>CORS_ALLOWED_ORIGINS</c> 环境变量，
/// 或者是 <c>localhost</c>/<c>127.0.0.1</c>/<c>*.luckyfishes.site</c>/<c>*.xauat.site</c>。
/// </para>
/// <para>
/// <b>注意</b>：Flask 的 README 声称也放行 <c>*.zeabur.app</c>，但代码里**并没有**——这里按代码实现，
/// 不按文档。管理端 lumaris_admin 部署在 Vercel 域名上，必须靠 <c>CORS_ALLOWED_ORIGINS</c> 放行，
/// 否则会被静默拦掉（表现为浏览器里请求失败、服务端无异常日志）。
/// </para>
/// </summary>
internal static class LogsCors
{
    public const string PolicyName = "LogsCors";

    public static bool IsAllowedOrigin(string origin, string? extraOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        var normalized = origin.TrimEnd('/');

        if (!string.IsNullOrWhiteSpace(extraOrigins))
        {
            foreach (var candidate in extraOrigins.Split(
                         ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(candidate.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("http" or "https")) return false;

        var host = uri.Host;
        return host is "localhost" or "127.0.0.1"
               || host.EndsWith(".luckyfishes.site", StringComparison.OrdinalIgnoreCase)
               || host.EndsWith(".xauat.site", StringComparison.OrdinalIgnoreCase);
    }
}
