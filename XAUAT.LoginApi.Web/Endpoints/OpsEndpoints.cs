using System.Security.Cryptography;
using System.Text;
using XAUAT.LoginApi.Extensions;
using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Ops;

namespace XAUAT.LoginApi.Endpoints;

/// <summary>
/// 运维端点：应用日志查询。
/// <para>
/// 鉴权与参数校验逐字移植 Flask 的 <c>LogsResource</c>，包括那几条中文 400 文案
/// （管理端 lumaris_admin 未必解析它们，但保持一字不差最省心）。
/// </para>
/// </summary>
internal static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        // RequireCors 用的是 Program 里注册的具名策略，白名单逻辑见 LogsCors
        app.MapGet("/Logs", GetLogsAsync).RequireCors(LogsCors.PolicyName);

        return app;
    }

    private static IResult GetLogsAsync(
        HttpContext context,
        string? page,
        string? pageSize,
        string? level,
        string? search,
        InMemoryLogStore store,
        ServiceConfiguration configuration)
    {
        if (!IsAuthorized(context, configuration))
        {
            return ApiResults.LogsError("Unauthorized", StatusCodes.Status401Unauthorized);
        }

        var pageNumber = 1;
        var pageSizeNumber = 50;

        if (!string.IsNullOrEmpty(page) && !int.TryParse(page, out pageNumber) ||
            !string.IsNullOrEmpty(pageSize) && !int.TryParse(pageSize, out pageSizeNumber))
        {
            return BadRequest("page 和 pageSize 必须是数字");
        }

        if (pageNumber < 1 || pageSizeNumber is < 1 or > 200)
        {
            return BadRequest("page 必须大于等于 1，pageSize 必须在 1-200 之间");
        }

        // Flask: level.title() —— 接受 trace/TRACE/Trace 三种写法
        string? normalizedLevel = null;
        if (!string.IsNullOrEmpty(level))
        {
            normalizedLevel = System.Globalization.CultureInfo.InvariantCulture.TextInfo
                .ToTitleCase(level.ToLowerInvariant());

            if (!LogLevels.IsKnown(normalizedLevel))
            {
                return BadRequest("level 必须是 Trace、Debug、Information、Warning、Error 或 Fatal");
            }
        }

        var (total, items) = store.Query(pageNumber, pageSizeNumber, normalizedLevel, search);

        return ApiResults.Json(
            new LogsResponseDto
            {
                Page = pageNumber,
                PageSize = pageSizeNumber,
                Total = total,
                // Flask: math.ceil(total / page_size) if total else 0
                TotalPages = total == 0 ? 0 : (int)Math.Ceiling((double)total / pageSizeNumber),
                Items = [.. items.Select(ToDto)]
            });
    }

    private static LogEntryDto ToDto(LogRecord record) => new()
    {
        Timestamp = record.Timestamp,
        Level = record.Level,
        Message = record.Message,
        Source = record.Source,
        Exception = record.Exception
    };

    /// <summary>
    /// 令牌校验。逐字移植 Flask 的 <c>_authorized</c>：
    /// <c>LOG_VIEW_TOKEN</c> 未设置或为空白 → 完全开放（这是既有行为，便于本地开发）；
    /// 否则先看 <c>X-Log-Token</c>，再看 <c>Authorization: Bearer</c>，最后做定长时间比较。
    /// </summary>
    private static bool IsAuthorized(HttpContext context, ServiceConfiguration configuration)
    {
        var expected = configuration.LogViewToken;
        if (string.IsNullOrWhiteSpace(expected)) return true;

        var supplied = context.Request.Headers["X-Log-Token"].ToString();
        if (string.IsNullOrEmpty(supplied))
        {
            var authorization = context.Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase))
            {
                supplied = authorization[7..];
            }
        }

        if (string.IsNullOrEmpty(supplied)) return false;

        var expectedBytes = Encoding.UTF8.GetBytes(expected.Trim());
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());

        // FixedTimeEquals 在长度不等时直接返回 false，与 secrets.compare_digest 行为一致
        return CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private static IResult BadRequest(string message)
        => ApiResults.LogsError(message, StatusCodes.Status400BadRequest);
}
