using System.Text.Json;
using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Redis;
using XAUAT.LoginApi.Services;

namespace XAUAT.LoginApi.Endpoints;

/// <summary>
/// 登录与封禁相关端点。
/// <para>
/// 响应形状全部照搬 Flask（含字段名与中文文案）——<c>POST /auth/login</c> 的
/// <c>{success, cookies}</c> 是 XAUAT.EduApi 的硬依赖，也是日后把
/// <c>schedule.xauat.site</c> 切到本服务的接缝。
/// </para>
/// </summary>
internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", PostLoginAsync);
        app.MapGet("/login/{username}/{password}", GetLegacyLoginAsync);

        app.MapGet("/security/ban_status", GetBanStatusAsync);
        app.MapPost("/security/ban_status", PostUnbanAsync);
        app.MapGet("/security/ban_logs", GetBanLogsAsync);

        return app;
    }

    /// <summary>
    /// 标准登录入口。失败用 401，封禁用 403。
    /// </summary>
    private static async Task<IResult> PostLoginAsync(
        HttpContext context, AuthService authService, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AuthEndpoints");

        LoginRequestDto? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                LoginJsonContext.Default.LoginRequestDto, context.RequestAborted);
        }
        catch (JsonException)
        {
            // Flask 这里因为 get_json(force=True) 抛异常而被兜底 except 变成 500；
            // 但同一段代码里的 400 分支（message = "Invalid JSON body"）显然才是作者的意图，
            // 客户端发坏 JSON 属于 4xx。这里按 400 处理。
            return ApiResults.Json(
                new LoginResponseDto { Success = false, Message = "Invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (request is null)
        {
            return ApiResults.Json(
                new LoginResponseDto { Success = false, Message = "Invalid JSON body" }, statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return ApiResults.Json(
                new LoginResponseDto { Success = false, Message = "Username and password required" }, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await authService.LoginAsync(
                request.School ?? "xauat", request.Username, request.Password, context.RequestAborted);

            return ToLoginResult(result, failureStatusCode: StatusCodes.Status401Unauthorized);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "登录接口异常");
            return ApiResults.Json(
                new LoginResponseDto { Success = false, Message = ex.Message },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// 遗留登录路由 <c>GET /login/{username}/{password}</c>。学校硬编码为 xauat。
    /// <para>
    /// <b>注意：登录失败也返回 200</b>——这是 Flask 的历史怪癖
    /// （<c>return result, 200</c> 与成功分支写在一起）。刻意保留：老客户端可能就是按
    /// "200 + success 字段"来判断的，改成 401 会让它们把失败当成功。
    /// </para>
    /// </summary>
    private static async Task<IResult> GetLegacyLoginAsync(
        string username, string password,
        HttpContext context, AuthService authService, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AuthEndpoints");

        try
        {
            var result = await authService.LoginAsync("xauat", username, password, context.RequestAborted);

            // 与上面唯一的差别：非封禁的失败是 200 而不是 401
            return ToLoginResult(result, failureStatusCode: StatusCodes.Status200OK);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "遗留登录接口异常");
            return ApiResults.Json(
                new LoginResponseDto { Success = false, Message = ex.Message },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static IResult ToLoginResult(AuthResult result, int failureStatusCode)
    {
        if (result.Banned)
        {
            return ApiResults.Json(
                new LoginResponseDto
                {
                    Success = false,
                    Message = result.Message,
                    Banned = true,
                    BanReason = result.BanReason,
                    BanUntil = result.BanUntil
                },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (result.Success)
        {
            return ApiResults.Json(
                new LoginResponseDto { Success = true, Cookies = result.Cookies },
                statusCode: StatusCodes.Status200OK);
        }

        return ApiResults.Json(
            new LoginResponseDto { Success = false, Message = result.Message },
            statusCode: failureStatusCode);
    }

    private static async Task<IResult> GetBanStatusAsync(
        string? school, string? username, BanService banService, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(username))
        {
            return ApiResults.Json(
                new BanStatusResponseDto { Success = false, Message = "缺少用户名" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var schoolKey = (school ?? "xauat").ToLowerInvariant();
        var bans = await banService.FindActiveBansAsync(schoolKey, username);

        return ApiResults.Json(
            new BanStatusResponseDto
            {
                Success = true,
                Banned = bans.Count > 0,
                Items = bans
            },
            statusCode: StatusCodes.Status200OK);
    }

    private static async Task<IResult> PostUnbanAsync(
        HttpContext context, BanService banService, CancellationToken cancellationToken)
    {
        BanStatusRequestDto? request = null;
        try
        {
            request = await context.Request.ReadFromJsonAsync(
                LoginJsonContext.Default.BanStatusRequestDto, cancellationToken);
        }
        catch (JsonException)
        {
            // Flask 在这里把解析失败吞掉当作空 dict 处理，随后因缺 username 返回 400
        }

        if (string.IsNullOrEmpty(request?.Username))
        {
            return ApiResults.Json(
                new MessageResponseDto { Success = false, Message = "缺少用户名" },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var schoolKey = (request.School ?? "xauat").ToLowerInvariant();
        var removed = await banService.UnbanByUsernameAsync(schoolKey, request.Username);

        return ApiResults.Json(
            removed
                ? new MessageResponseDto { Success = true, Message = "用户已解封" }
                : new MessageResponseDto { Success = false, Message = "用户当前未被封禁或解封失败" },
            statusCode: removed ? StatusCodes.Status200OK : StatusCodes.Status400BadRequest);
    }

    /// <summary>
    /// 封禁日志。这是本项目**新增的 JSON 接口**，替代 Flask 那个 Tailwind HTML 页
    /// （<c>/admin/ban_logs</c>）；字段沿用该页面的视图模型，前端可以零改动接管。
    /// </summary>
    private static async Task<IResult> GetBanLogsAsync(
        string? school, string? username, BanService banService, CancellationToken cancellationToken)
    {
        var logs = await banService.ListBanLogsAsync(
            string.IsNullOrEmpty(school) ? null : school.ToLowerInvariant(),
            string.IsNullOrEmpty(username) ? null : username,
            limit: 200);

        return ApiResults.Json(
            new BanLogsResponseDto { Total = logs.Count, Items = logs },
            statusCode: StatusCodes.Status200OK);
    }
}
