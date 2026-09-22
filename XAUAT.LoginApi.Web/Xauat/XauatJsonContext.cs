using System.Text.Json;
using System.Text.Json.Serialization;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 发往教务系统的请求体。
/// <para>
/// <c>studentId</c> 是**字面量字符串 <c>"null"</c>**，不是 JSON null——上游如此，别改成 null。
/// </para>
/// </summary>
internal sealed class ScheduleTableRequest
{
    public string StudentId { get; set; } = "null";

    /// <summary>
    /// 课程 ID 列表。用 <see cref="JsonElement"/> 原样透传：<c>get-data</c> 返回的
    /// <c>lessonIds</c> 元素类型（数字还是字符串）由上游决定，原样回传最稳妥。
    /// </summary>
    public List<JsonElement> LessonIds { get; set; } = [];
}

/// <summary>本命名空间下请求体的源生成上下文。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ScheduleTableRequest))]
internal partial class XauatJsonContext : JsonSerializerContext;
