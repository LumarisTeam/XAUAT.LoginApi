using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Services;

namespace XAUAT.LoginApi.Endpoints;

/// <summary>
/// ICS 日历订阅端点。<c>/calendars</c> 与 <c>/class</c> 是等价别名
/// （前者是新命名，后者是移动端深链与 <c>index.html</c> 里在用的老名字）。
/// <para>
/// 兼容性要点：XAUAT.EduApi 的 <c>v1/course/Calendar</c> 会 302 到
/// <c>schedule.xauat.site/class?school=&amp;username=&amp;password=</c>，
/// 所以这个路由的查询参数名与响应头不能改。
/// </para>
/// </summary>
internal static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/calendars", GetCalendarAsync).RequireRateLimiting("EduCrawler");
        app.MapGet("/class", GetCalendarAsync).RequireRateLimiting("EduCrawler");

        return app;
    }

    private static async Task<IResult> GetCalendarAsync(
        string? school,
        string? username,
        string? password,
        string? passwd,
        string? filter,
        HttpContext context,
        CalendarService calendarService,
        CancellationToken cancellationToken)
    {
        // Flask: password or passwd —— 两个参数名都被老客户端用过
        var effectivePassword = string.IsNullOrEmpty(password) ? passwd : password;

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(effectivePassword))
        {
            return Error("缺少用户名或密码", StatusCodes.Status400BadRequest);
        }

        var result = await calendarService.GenerateAsync(
            school ?? "xauat", username, effectivePassword, filter, cancellationToken);

        switch (result.Outcome)
        {
            case CalendarOutcome.Success:
                // Flask: mimetype='text/calendar' + Content-Disposition 附件
                context.Response.Headers.ContentDisposition = "attachment; filename=calendar.ics";
                return Results.Bytes(result.Content!, "text/calendar; charset=utf-8");

            case CalendarOutcome.AuthFailed:
                return Error("认证失败或数据获取错误", StatusCodes.Status401Unauthorized);

            case CalendarOutcome.Banned:
                return Error("账户已被暂时封禁，请稍后重试或联系管理员", StatusCodes.Status403Forbidden);

            default:
                return Error(result.Error ?? "", StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>日历接口的错误形状是 <c>{"error": ...}</c>，注意 key 不是 message。</summary>
    private static IResult Error(string message, int statusCode)
        => ApiResults.Json(new ErrorResponseDto(message),
            statusCode: statusCode);
}
