using System.Text.Json.Serialization;

namespace XAUAT.LoginApi.Models;

/// <summary>
/// HTTP 契约的源生成 JSON 上下文。
/// Native AOT 下反射式序列化不可用，所有对外响应都必须在此注册。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LoginResponseDto))]
[JsonSerializable(typeof(BanStatusResponseDto))]
[JsonSerializable(typeof(BanItemDto))]
[JsonSerializable(typeof(MessageResponseDto))]
[JsonSerializable(typeof(ErrorResponseDto))]
[JsonSerializable(typeof(CountResponseDto))]
[JsonSerializable(typeof(LogsResponseDto))]
[JsonSerializable(typeof(LogEntryDto))]
[JsonSerializable(typeof(BanLogsResponseDto))]
[JsonSerializable(typeof(BanLogItemDto))]
[JsonSerializable(typeof(LoginRequestDto))]
[JsonSerializable(typeof(BanStatusRequestDto))]
internal partial class LoginJsonContext : JsonSerializerContext;
