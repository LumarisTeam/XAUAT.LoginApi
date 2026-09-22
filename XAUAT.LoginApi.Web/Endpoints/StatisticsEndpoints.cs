using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Services;

namespace XAUAT.LoginApi.Endpoints;

/// <summary>
/// 活跃用户统计。<c>/statistics/users</c> 与 <c>/user_count</c> 是等价别名。
/// </summary>
internal static class StatisticsEndpoints
{
    public static IEndpointRouteBuilder MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/statistics/users", GetUserCountAsync);
        app.MapGet("/user_count", GetUserCountAsync);

        return app;
    }

    private static async Task<IResult> GetUserCountAsync(
        StatisticsService statisticsService, CancellationToken cancellationToken)
    {
        var count = await statisticsService.GetUserCountAsync();
        return ApiResults.Json(new CountResponseDto(count));
    }
}
