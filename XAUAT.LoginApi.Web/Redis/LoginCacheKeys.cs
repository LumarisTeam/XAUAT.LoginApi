namespace XAUAT.LoginApi.Redis;

/// <summary>
/// Redis 键名。
/// <para>
/// <b>本类刻意不加任何前缀</b> —— 这与 PaymentAPI（<c>CacheKeys.Prefix = "paymentapi"</c>）
/// 和 EduApi（<c>"eduapi"</c>）的做法**相反**，且是有意为之：
/// 本服务是 <c>schedule.xauat.site</c> 上那套 Flask 的替代品，两者要**共用同一批键**，
/// 才能做到灰度期间缓存不冷、封禁状态延续、两个实现可以并行跑着对拍。
/// 一旦加上前缀，Flask 写的 cookie 缓存本服务就读不到，切换瞬间全体用户重新登录。
/// </para>
/// <para>
/// 因此这里的键名是**跨实现的存储契约**，不是内部细节：
/// 改名等于破坏与 Flask 的互通。配套的守卫测试见
/// <c>XAUAT.LoginApi.Tests/Compatibility/LoginCacheKeysTests.cs</c>。
/// </para>
/// <para>
/// 唯一的例外是封禁键：Flask 用 <c>security:ban:{school}:{username}:{password_hash}</c>，
/// 本服务改为 <c>security:ban:{school}:{username}</c>。原因是原粒度下换个错误密码
/// 就能绕过封禁。代价是 Flask 期间产生的旧封禁不会延续——这是已知且已确认的取舍。
/// </para>
/// </summary>
public static class LoginCacheKeys
{
    /// <summary>eduCookie 缓存。对应 Flask 的 <c>login-cookies-{username}-{hash}</c>。</summary>
    public static string EduCookies(string username, string passwordHash)
        => $"login-cookies-{username}-{passwordHash}";

    /// <summary>SSO 票据缓存。对应 Flask 的 <c>sso-cookies-{username}-{hash}</c>。</summary>
    public static string SsoCookies(string username, string passwordHash)
        => $"sso-cookies-{username}-{passwordHash}";

    /// <summary>登录失败计数。对应 Flask 的 <c>login-failed-{username}-{hash}</c>。</summary>
    public static string LoginFailed(string username, string passwordHash)
        => $"login-failed-{username}-{passwordHash}";

    /// <summary>当前学期 id，全局单键（Flask 如此，不按用户区分）。</summary>
    public const string CurrentSemester = "current_semester";

    /// <summary>
    /// 活跃用户统计的 SCAN 前缀。
    /// <para>
    /// Flask 用 Redis keyspace 通知 + 计数器维护 <c>user:count:total</c>，但有 4 个 gunicorn
    /// worker 各跑一个监听，键过期时被重复扣减，计数系统性偏低。本服务改为请求时 SCAN 现算，
    /// 因此不再读写 <c>user:count:total</c>（该键将随 TTL 自然消亡）。
    /// </para>
    /// </summary>
    public const string SsoCookiesPrefix = "sso-cookies-";

    /// <summary>
    /// 账号标识，封禁相关键的中间段。
    /// 注意：Flask 用三段式 <c>{school}:{username}:{password_hash}</c>，本服务是两段式。
    /// </summary>
    public static string AccountKey(string school, string username) => $"{school}:{username}";

    /// <summary>封禁键。</summary>
    public static string Ban(string accountKey) => $"security:ban:{accountKey}";

    /// <summary>限流事件 ZSET 键。</summary>
    public static string RateLimitEvents(string accountKey) => $"security:rate_limit_events:{accountKey}";

    /// <summary>封禁日志列表（LPUSH，最新在前）。</summary>
    public const string BanLogs = "security:ban_logs";

    /// <summary>
    /// 封禁键前缀，用于 SCAN 历史（三段式）条目。
    /// 读路径要同时查新键与这个前缀下的旧键，见 <see cref="BanService"/>。
    /// </summary>
    public static string LegacyBanPattern(string school, string username)
        => $"security:ban:{school}:{username}:*";
}

/// <summary>
/// 缓存 TTL。数值全部来自 Flask，改动会改变既有客户端的行为预期。
/// </summary>
public static class LoginCacheTtl
{
    /// <summary>eduCookie：20 分钟。Flask 写死 <c>60*20</c>。</summary>
    public static readonly TimeSpan EduCookies = TimeSpan.FromSeconds(60 * 20);

    /// <summary>SSO 票据：7 天。<c>60*60*24*7</c>。</summary>
    public static readonly TimeSpan SsoCookies = TimeSpan.FromSeconds(60 * 60 * 24 * 7);

    /// <summary>登录失败窗口：20 分钟。</summary>
    public static readonly TimeSpan LoginFailed = TimeSpan.FromSeconds(1200);

    /// <summary>当前学期：7 天。</summary>
    public static readonly TimeSpan CurrentSemester = TimeSpan.FromSeconds(60 * 60 * 24 * 7);

    /// <summary>封禁时长：24 小时。</summary>
    public static readonly TimeSpan Ban = TimeSpan.FromSeconds(86400);

    /// <summary>限流事件 ZSET 保留时长，Flask 取 <c>max(window, ban)</c> = 86400s。</summary>
    public static readonly TimeSpan RateLimitEvents = TimeSpan.FromSeconds(86400);

    /// <summary>
    /// <c>security:ban_logs</c> 的长度上限。
    /// Flask 从不 LTRIM，该列表会无限增长（读只取前 201 条）；这里补上封顶。
    /// </summary>
    public const long BanLogsMaxLength = 10000;
}
