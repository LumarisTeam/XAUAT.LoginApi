using System.Text.Json.Serialization;
using XAUAT.LoginApi.Models;

namespace XAUAT.LoginApi.Endpoints;

/// <summary>
/// <c>/Logs</c> 的错误响应上下文。
/// <para>
/// 单独放一个上下文而不是复用 <c>LoginJsonContext</c>：<c>/Logs</c> 的错误形状是
/// <c>{"message": ...}</c>（**没有 success 字段**，Flask 原文如此），与登录系列不同。
/// 用 <see cref="MessageResponseDto"/> 会多出一个 <c>success</c> 键。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LogsErrorDto))]
internal partial class LogsUnauthorizedContext : JsonSerializerContext;

/// <summary><c>{"message": string}</c> —— <c>/Logs</c> 的唯一错误形状。</summary>
internal sealed class LogsErrorDto
{
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}
