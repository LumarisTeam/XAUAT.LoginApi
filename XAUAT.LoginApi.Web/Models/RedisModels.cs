using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace XAUAT.LoginApi.Models;

// ---------------------------------------------------------------------------------
// Redis 存储模型。
//
// 这些对象的键名是 **snake_case**，且必须与 Flask 写下的字节一致——新服务与 Flask
// 共用同一个 Redis 实例（见 LoginCacheKeys 的说明），Flask 在灰度期间仍会读写同一批键。
// 用 RedisJsonContext 的 SnakeCaseLower 策略统一处理，不要在这里手写 JsonPropertyName。
// ---------------------------------------------------------------------------------

/// <summary>
/// <c>security:ban:{school}:{username}</c> 的值。
/// 对应 Flask 的 <c>ban_data</c>：<c>{"account","reason","banned_at","unban_at"}</c>。
/// </summary>
internal sealed class BanInfo
{
    public string Account { get; set; } = "";
    public string Reason { get; set; } = "";
    public long BannedAt { get; set; }
    public long UnbanAt { get; set; }
}

/// <summary>
/// <c>security:ban_logs</c> 列表里的一条。
/// <para>
/// 这个键里同时存在两种形状（Flask 就写了两种，历史条目不能改）：
/// 自动封禁 <c>{"type":"auto_ban","created_at":N,"data":{...}}</c>
/// 手动解封 <c>{"type":"manual_unban","created_at":N,"account":S,"reason":S,"previous":{...}}</c>
/// 因此可选字段用 WhenWritingNull 忽略，保证新写入的条目形状与 Flask 逐字相同。
/// </para>
/// </summary>
internal sealed class BanLogEntry
{
    public string Type { get; set; } = "";
    public long CreatedAt { get; set; }

    /// <summary>仅 auto_ban 有。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BanInfo? Data { get; set; }

    /// <summary>仅 manual_unban 有。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; set; }

    /// <summary>仅 manual_unban 有。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    /// <summary>仅 manual_unban 有：解封前的封禁快照。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BanInfo? Previous { get; set; }
}

/// <summary>
/// Redis 值的源生成上下文。snake_case 是与 Flask 的存储契约，不要改成 camelCase。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BanInfo))]
[JsonSerializable(typeof(BanLogEntry))]
internal partial class RedisJsonContext : JsonSerializerContext;

/// <summary>
/// Redis 值的序列化入口。
/// <para>
/// 与 <c>ApiJson</c> 同一个理由：源生成上下文自带的编码器会把中文转义成 <c>\uXXXX</c>，
/// 而 Flask 存的时候用的是 <c>json.dumps(..., ensure_ascii=False)</c>，写的是原始 UTF-8。
/// 两种写法互相都读得懂（转义是合法 JSON），但既然目标是与 Flask 共用同一批键，
/// 就让字节形态也一致——顺带在 redis-cli 里直接看这些值时可读。
/// </para>
/// </summary>
internal static class RedisJson
{
    private static readonly JsonSerializerOptions Options =
        new(RedisJsonContext.Default.Options) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonTypeInfo<T> TypeInfo<T>() where T : class
        => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
}
