using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using XAUAT.LoginApi.Endpoints;

namespace XAUAT.LoginApi.Models;

/// <summary>
/// 对外 JSON 响应的序列化选项。
/// <para>
/// <b>为什么需要这一层</b>：<c>Results.Json(data, jsonSerializerOptions, ...)</c> 那个重载
/// 带 <c>RequiresDynamicCode</c>/<c>RequiresUnreferencedCode</c>，会直接触发本项目的
/// IL2026/IL3050 编译错误（有意为之的 AOT 守卫）；而 <c>Results.Json(data, jsonTypeInfo)</c>
/// 只认类型信息、**不读** <c>ConfigureHttpJsonOptions</c> 里配的编码器，
/// 于是中文会被转义成 <c>\u767B\u5F55</c>。
/// </para>
/// <para>
/// 解法：在本类里新建一份沿用源生成上下文的选项（带 Relaxed 编码器），
/// 再用它 <c>GetTypeInfo</c> 取回 <see cref="JsonTypeInfo{T}"/>——类型信息仍来自源生成器，
/// 因此 AOT 安全，同时拿到了原始 UTF-8 输出，与 Flask 的 <c>ensure_ascii = False</c> 一致。
/// </para>
/// <para>
/// 编码器用 <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>：名字里的 Unsafe 指
/// 不再转义 <c>&lt;</c> <c>&gt;</c> <c>&amp;</c>。对纯 JSON API 这正是要的行为，
/// 且响应体不会被内联进 HTML。
/// </para>
/// </summary>
internal static class ApiJson
{
    private static readonly JsonSerializerOptions ResponseOptions =
        new(LoginJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions LogsOptions =
        new(LogsUnauthorizedContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>取主契约上下文的类型信息（务必保证 <typeparamref name="T"/> 已在上下文中注册）。</summary>
    public static JsonTypeInfo<T> Response<T>() where T : class
        => (JsonTypeInfo<T>)ResponseOptions.GetTypeInfo(typeof(T));

    /// <summary>取 <c>/Logs</c> 错误形状上下文的类型信息。</summary>
    public static JsonTypeInfo<T> Logs<T>() where T : class
        => (JsonTypeInfo<T>)LogsOptions.GetTypeInfo(typeof(T));
}
