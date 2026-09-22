using XAUAT.LoginApi.Models;

namespace XAUAT.LoginApi.Endpoints;

/// <summary>
/// 统一的 JSON 响应构造。
/// <para>
/// 存在的意义是把 <c>Results.Json(value, ApiJson.Response&lt;T&gt;(), statusCode)</c>
/// 这个固定搭配收敛到一处，避免每个端点都写一遍类型参数——
/// 也避免有人图省事退回那个 AOT 不安全的 <c>JsonSerializerOptions</c> 重载。
/// </para>
/// </summary>
internal static class ApiResults
{
    public static IResult Json<T>(T value, int? statusCode = null) where T : class
        => Results.Json(value, ApiJson.Response<T>(), statusCode: statusCode);

    /// <summary><c>/Logs</c> 的错误响应，形状是 <c>{"message": ...}</c>（没有 success 字段）。</summary>
    public static IResult LogsError(string message, int statusCode)
        => Results.Json(new LogsErrorDto { Message = message }, ApiJson.Logs<LogsErrorDto>(), statusCode: statusCode);
}
