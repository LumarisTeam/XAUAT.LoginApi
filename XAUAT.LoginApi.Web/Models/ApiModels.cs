using System.Text.Json.Serialization;

namespace XAUAT.LoginApi.Models;

// ---------------------------------------------------------------------------------
// HTTP 契约模型。
//
// 命名规则：默认 camelCase（见 LoginJsonContext），但凡 Flask 发出的键名不是属性名的
// camelCase 形式（例如 snake_case 的 page_size / banned_at），一律用显式
// [JsonPropertyName] 钉死。这些字段是既有客户端（EduApi、iOS、lumaris_admin）的
// 硬依赖，改名等于破坏线上。
// ---------------------------------------------------------------------------------

/// <summary>
/// <c>POST /auth/login</c> 与遗留 <c>GET /login/{u}/{p}</c> 的响应。
/// <para>
/// 这就是 EduApi <c>SSOLoginService</c> 消费的那个契约：它只读 <c>success</c> 与 <c>cookies</c>。
/// 字段的可空性对应 Flask 的"按情况省略键"行为——成功时没有 message，失败时没有 cookies。
/// </para>
/// </summary>
internal sealed class LoginResponseDto
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("cookies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cookies { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }

    [JsonPropertyName("banned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Banned { get; set; }

    [JsonPropertyName("ban_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BanReason { get; set; }

    [JsonPropertyName("ban_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? BanUntil { get; set; }
}

/// <summary>
/// <c>GET /security/ban_status</c> 的响应。
/// 成功时 <see cref="Banned"/> 与 <see cref="Items"/> 必有；缺用户名时是
/// <c>{"success": false, "message": "缺少用户名"}</c>。
/// </summary>
internal sealed class BanStatusResponseDto
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("banned")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Banned { get; set; }

    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BanItemDto>? Items { get; set; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}

/// <summary>
/// 单条封禁记录。键名与 Flask <c>get_ban_status</c> / <c>list_bans_for_user</c>
/// 返回的 dict 完全一致（含运行时注入的 <c>ttl</c> 与 <c>key</c>）。
/// </summary>
internal sealed class BanItemDto
{
    [JsonPropertyName("account")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Account { get; set; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; set; }

    [JsonPropertyName("banned_at")] public long BannedAt { get; set; }

    [JsonPropertyName("unban_at")] public long UnbanAt { get; set; }

    /// <summary>剩余 TTL 秒数（Redis TTL 原样透出，-1 表示永不过期，-2 表示键不存在）。</summary>
    [JsonPropertyName("ttl")] public long Ttl { get; set; }

    /// <summary>完整 Redis 键名，仅 <c>list_bans_for_user</c> 路径带。</summary>
    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; set; }

    /// <summary>
    /// Redis 里的值不是合法 JSON 时的兜底：Flask 会把它原样塞进 <c>raw</c> 键并照常返回
    /// （见 <c>redis_client.get_ban_status</c> 的 <c>except: data = {"raw": raw}</c>）。
    /// </summary>
    [JsonPropertyName("raw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Raw { get; set; }
}

/// <summary><c>{"success": bool, "message": string}</c> —— 解封接口与各类简短回执。</summary>
internal sealed class MessageResponseDto
{
    [JsonPropertyName("success")] public bool Success { get; set; }

    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

/// <summary><c>{"error": string}</c> —— 日历接口专用的错误形状（注意 key 是 error 不是 message）。</summary>
internal sealed class ErrorResponseDto(string error)
{
    [JsonPropertyName("error")] public string Error { get; } = error;
}

/// <summary><c>{"count": int}</c> —— 活跃用户数。</summary>
internal sealed class CountResponseDto(int count)
{
    [JsonPropertyName("count")] public int Count { get; } = count;
}

/// <summary>
/// <c>GET /Logs</c> 的分页响应。
/// 键名是 snake_case，与 Flask 一致；管理端 lumaris_admin 直接读这几个字段。
/// </summary>
internal sealed class LogsResponseDto
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("page_size")] public int PageSize { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
    [JsonPropertyName("items")] public List<LogEntryDto> Items { get; set; } = [];
}

/// <summary>
/// 单条日志。刻意不加 WhenWritingNull：Flask 的 <c>to_dict()</c> 无条件输出
/// <c>source</c>/<c>exception</c> 两个键（值为 null），前端按"键存在但为 null"处理。
/// </summary>
internal sealed class LogEntryDto
{
    [JsonPropertyName("timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("level")] public string Level { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("exception")] public string? Exception { get; set; }
}

/// <summary>
/// <c>GET /security/ban_logs</c> 的响应。
/// 这是本项目新增的 JSON 接口，替代 Flask 那个 Tailwind HTML 页
/// （<c>/admin/ban_logs</c>），字段沿用页面的视图模型以便前端零改动接管。
/// </summary>
internal sealed class BanLogsResponseDto
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("items")] public List<BanLogItemDto> Items { get; set; } = [];
}

/// <summary>封禁日志条目（已归一化，抹平 auto_ban / manual_unban 两种存储形状的差异）。</summary>
internal sealed class BanLogItemDto
{
    /// <summary><c>auto_ban</c> 或 <c>manual_unban</c>。</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "";

    /// <summary>Flask 页面里的中文标签：自动封禁 / 手动解封。</summary>
    [JsonPropertyName("type_label")] public string TypeLabel { get; set; } = "";

    [JsonPropertyName("created_at")] public long CreatedAt { get; set; }

    /// <summary><c>yyyy-MM-dd HH:mm:ss</c>（本地时区），与 Flask 的 _fmt_ts 一致。</summary>
    [JsonPropertyName("created_at_human")] public string CreatedAtHuman { get; set; } = "";

    /// <summary>仅自动封禁有值；手动解封为 null。</summary>
    [JsonPropertyName("unban_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UnbanAt { get; set; }

    [JsonPropertyName("unban_at_human")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UnbanAtHuman { get; set; }

    [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    [JsonPropertyName("account")] public string Account { get; set; } = "";
}

// ---------------------------------------------------------------------------------
// 请求体
// ---------------------------------------------------------------------------------

/// <summary><c>POST /auth/login</c> 的请求体。字段可空——为空时走 400 参数校验分支。</summary>
internal sealed class LoginRequestDto
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }

    /// <summary>默认 <c>xauat</c>（与 Flask 的 <c>json_data.get('school', 'xauat')</c> 一致）。</summary>
    [JsonPropertyName("school")] public string? School { get; set; }
}

/// <summary><c>POST /security/ban_status</c> 的请求体。</summary>
internal sealed class BanStatusRequestDto
{
    [JsonPropertyName("school")] public string? School { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}
