using System.Text.Json;
using XAUAT.LoginApi.Models;

namespace XAUAT.LoginApi.Redis;

/// <summary>
/// 登录限流与封禁。
/// <para>
/// 这是 Flask <c>RedisClient</c> 中限流/封禁部分 + <c>xauat_login</c> 中策略常量的合并移植。
/// 两级策略（数值与 Flask 逐字一致）：
/// </para>
/// <list type="number">
/// <item>20 分钟内失败 5 次 → 命中当次即拒（401「登录失败次数过多」），窗口内持续有效。</item>
/// <item>3 次命中第一级（且两次之间至少间隔 20 分钟）→ 硬封禁 24 小时（403）。</item>
/// </list>
/// <para>
/// 与 Flask 的**唯一语义差异**：封禁键粒度。Flask 是
/// <c>security:ban:{school}:{username}:{password_hash}</c>，换个错误密码即可绕过；
/// 本服务改为 <c>security:ban:{school}:{username}</c>。因此读路径要同时覆盖两种形状
/// （灰度期间 Redis 里两种键并存），见 <see cref="FindActiveBansAsync"/>。
/// </para>
/// </summary>
internal sealed class BanService(ILoginRedisStore store, ILogger<BanService> logger)
{
    /// <summary>第一级：窗口内允许的最大失败次数。</summary>
    public const int MaxAttempts = 5;

    /// <summary>第一级窗口：20 分钟。</summary>
    public const int WindowSeconds = 1200;

    /// <summary>第二级：统计限流事件的滑动窗口（61 分钟）。</summary>
    public const int BanWindowSeconds = 3660;

    /// <summary>第二级：窗口内触发多少次限流才封禁。</summary>
    public const int BanThreshold = 3;

    /// <summary>封禁时长：24 小时。</summary>
    public const int BanSeconds = 86400;

    /// <summary>两次限流事件之间的最小间隔，低于此值不重复计数（去抖）。</summary>
    public const int MinIntervalSeconds = 1200;

    /// <summary>自动封禁的默认理由，与 Flask 一致。</summary>
    public const string DefaultBanReason = "连续多次触发登录限流";

    /// <summary>手动解封的默认理由，与 Flask 一致。</summary>
    public const string DefaultUnbanReason = "手动解封";

    // ================================================================ 第一级：失败计数

    /// <summary>
    /// 原子性地尝试占一个失败名额。
    /// <para>
    /// 语义要点：**先占名额再打网络**。调用方应在真正发起登录之前调用，
    /// 这样失败不会重复计数（Flask 的注释也是这么写的）。超限时会把刚占的名额退回去，
    /// 返回的计数是回退后的值。
    /// </para>
    /// </summary>
    /// <returns>(是否已被限流, 当前计数)。Redis 不可用时恒为 (false, 0) —— 即不限流。</returns>
    public async Task<(bool Limited, int Count)> AcquireRateLimitSlotAsync(string key)
    {
        if (!store.IsAvailable) return (false, 0);

        var count = await store.IncrementAsync(key);
        if (count == 0) return (false, 0); // Redis 出错，按不限流处理

        // 只在首次创建时设置 TTL —— 形成"从第一次失败起算"的固定窗口
        if (count == 1)
        {
            await store.ExpireAsync(key, LoginCacheTtl.LoginFailed);
        }

        if (count > MaxAttempts)
        {
            await store.DecrementAsync(key);
            return (true, (int)count - 1);
        }

        return (false, (int)count);
    }

    /// <summary>
    /// 登录成功时归还名额。
    /// Flask 的实现是 DECR 后若为负则 DEL——这里照搬（成功的登录不该消耗失败额度）。
    /// </summary>
    public async Task ReleaseRateLimitSlotAsync(string key)
    {
        if (!store.IsAvailable) return;

        var remaining = await store.DecrementAsync(key);
        if (remaining < 0)
        {
            await store.DeleteAsync(key);
        }
    }

    /// <summary>清除某用户的 cookie 缓存。失败次数达到上限时由登录链路调用。</summary>
    public async Task ClearUserCacheAsync(string username, string passwordHash)
    {
        await store.DeleteAsync(
            LoginCacheKeys.EduCookies(username, passwordHash),
            LoginCacheKeys.SsoCookies(username, passwordHash));
    }

    // ================================================================ 第二级：封禁

    /// <summary>
    /// 记录一次限流事件，达到阈值则封禁。
    /// </summary>
    /// <returns>(本次是否新产生封禁, 封禁信息)。已经在封禁中时返回 (true, 既有封禁信息)。</returns>
    public async Task<(bool BannedNow, BanInfo? Info)> RecordRateLimitAndMaybeBanAsync(
        string accountKey, string? reason = null)
    {
        if (!store.IsAvailable) return (false, null);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var zsetKey = LoginCacheKeys.RateLimitEvents(accountKey);

        // 裁掉滑出窗口的事件
        await store.SortedSetRemoveRangeByScoreAsync(zsetKey, now - BanWindowSeconds);

        // 去抖：距上次事件不足 20 分钟则不重复计数（但仍续期，避免窗口中途丢键）
        var top = await store.SortedSetTopWithScoresAsync(zsetKey, 1);
        if (top.Length > 0 && now - (long)top[0].Score < MinIntervalSeconds)
        {
            await store.ExpireAsync(zsetKey, LoginCacheTtl.RateLimitEvents);
            return (false, null);
        }

        await store.SortedSetAddAsync(zsetKey, now.ToString(), now);
        await store.ExpireAsync(zsetKey, LoginCacheTtl.RateLimitEvents);

        var count = await store.SortedSetCardinalityAsync(zsetKey);
        if (count < BanThreshold) return (false, null);

        // 已达阈值：已在封禁中就直接复用既有记录，不重复写日志
        var existing = await GetBanStatusAsync(accountKey);
        if (existing is not null) return (true, existing.Value.Info);

        var banInfo = new BanInfo
        {
            Account = accountKey,
            Reason = string.IsNullOrWhiteSpace(reason) ? DefaultBanReason : reason,
            BannedAt = now,
            UnbanAt = now + BanSeconds
        };

        await store.SetStringAsync(
            LoginCacheKeys.Ban(accountKey),
            JsonSerializer.Serialize(banInfo, RedisJson.TypeInfo<BanInfo>()),
            LoginCacheTtl.Ban);

        await PushBanLogAsync(new BanLogEntry { Type = "auto_ban", CreatedAt = now, Data = banInfo });

        logger.LogError("[SECURITY] 账号 {Account} 在 {Window} 秒内多次触发限流，已封禁 {Ban} 秒",
            accountKey, BanWindowSeconds, BanSeconds);

        return (true, banInfo);
    }

    /// <summary>
    /// 查询精确封禁键的状态。注意这里**只查新形状的键**；跨新旧两种形状的判断
    /// 由 <see cref="FindActiveBansAsync"/> 负责。
    /// </summary>
    public async Task<(BanInfo Info, long Ttl)?> GetBanStatusAsync(string accountKey)
    {
        if (!store.IsAvailable) return null;

        var key = LoginCacheKeys.Ban(accountKey);
        var raw = await store.GetStringAsync(key);
        if (string.IsNullOrEmpty(raw)) return null;

        var info = ParseBanInfo(raw, accountKey);
        return (info, await store.TimeToLiveSecondsAsync(key));
    }

    /// <summary>
    /// 找出某用户当前所有的封禁记录，**同时覆盖新旧两种键形状**。
    /// <para>
    /// 新形状 <c>security:ban:{school}:{username}</c> 精确命中；
    /// 旧形状 <c>security:ban:{school}:{username}:{password_hash}</c> 走 SCAN。
    /// 灰度期间 Redis 里两种键并存，只查其一会漏判。
    /// </para>
    /// </summary>
    public async Task<List<BanItemDto>> FindActiveBansAsync(string school, string username)
    {
        var items = new List<BanItemDto>();
        if (!store.IsAvailable) return items;

        var accountKey = LoginCacheKeys.AccountKey(school, username);

        // 新形状：精确键
        var exact = await GetBanStatusAsync(accountKey);
        if (exact is not null)
        {
            items.Add(ToDto(exact.Value.Info, exact.Value.Ttl, LoginCacheKeys.Ban(accountKey)));
        }

        // 旧形状：SCAN 出所有密码哈希变体
        foreach (var key in await store.ScanKeysAsync(LoginCacheKeys.LegacyBanPattern(school, username)))
        {
            var raw = await store.GetStringAsync(key);
            if (string.IsNullOrEmpty(raw)) continue;

            var suffix = key.Replace("security:ban:", "");
            items.Add(ToDto(ParseBanInfo(raw, suffix), await store.TimeToLiveSecondsAsync(key), key));
        }

        return items;
    }

    /// <summary>某用户是否处于封禁中（新键或旧键任一命中即为真）。</summary>
    public async Task<bool> IsBannedAsync(string school, string username)
        => (await FindActiveBansAsync(school, username)).Count > 0;

    /// <summary>
    /// 按用户名解封：新旧两种键一起删，每个被删的键都留一条 <c>manual_unban</c> 日志。
    /// </summary>
    /// <returns>是否真的删掉了至少一个封禁键。</returns>
    public async Task<bool> UnbanByUsernameAsync(string school, string username, string? reason = null)
    {
        if (!store.IsAvailable) return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var unbanReason = string.IsNullOrWhiteSpace(reason) ? DefaultUnbanReason : reason;
        var deletedAny = false;

        var keys = new List<string> { LoginCacheKeys.Ban(LoginCacheKeys.AccountKey(school, username)) };
        keys.AddRange(await store.ScanKeysAsync(LoginCacheKeys.LegacyBanPattern(school, username)));

        foreach (var key in keys)
        {
            var raw = await store.GetStringAsync(key);
            if (!string.IsNullOrEmpty(raw))
            {
                await PushBanLogAsync(new BanLogEntry
                {
                    Type = "manual_unban",
                    CreatedAt = now,
                    // Flask 用 key.replace("security:ban:", "") —— 保留其前缀剥离行为，
                    // 因此旧键写下的 account 仍是三段式的 {school}:{user}:{hash}
                    Account = key.Replace("security:ban:", ""),
                    Reason = unbanReason,
                    Previous = TryParseBanInfo(raw)
                });
            }

            if (await store.DeleteAsync(key) > 0)
            {
                deletedAny = true;
            }
        }

        if (deletedAny)
        {
            logger.LogInformation("已为用户 {School}:{Username} 解封", school, username);
        }

        return deletedAny;
    }

    // ================================================================ 封禁日志

    /// <summary>
    /// 读取封禁日志（最新在前），可选按学校/用户名过滤。
    /// <para>
    /// 过滤规则来自 Flask 的页面逻辑，但必须**同时兼容两种 account 形状**：
    /// 旧的 <c>xauat:user:hash</c> 用 <c>:{username}:</c> 子串匹配，
    /// 新的 <c>xauat:user</c> 是结尾匹配。
    /// </para>
    /// </summary>
    public async Task<List<BanLogItemDto>> ListBanLogsAsync(string? school, string? username, long limit = 200)
    {
        // Flask 页面读的是 LRANGE 0 200（201 条）
        var rawEntries = await store.ListRangeAsync(LoginCacheKeys.BanLogs, limit);
        var result = new List<BanLogItemDto>(rawEntries.Length);

        foreach (var raw in rawEntries)
        {
            BanLogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(raw, RedisJson.TypeInfo<BanLogEntry>());
            }
            catch (JsonException ex)
            {
                // 单条坏数据不该让整个页面挂掉
                logger.LogWarning(ex, "封禁日志条目不是合法 JSON，已跳过");
                continue;
            }

            if (entry is null) continue;

            var item = Normalize(entry);
            if (school is not null && !item.Account.StartsWith($"{school}:", StringComparison.Ordinal)) continue;
            if (username is not null && !MatchesUsername(item.Account, username)) continue;

            result.Add(item);
        }

        return result;
    }

    /// <summary>account 里是否含指定用户名——兼容新的两段式与旧的三段式。</summary>
    private static bool MatchesUsername(string account, string username)
        => account.Contains($":{username}:", StringComparison.Ordinal)
           || account.EndsWith($":{username}", StringComparison.Ordinal);

    /// <summary>
    /// 把两种存储形状抹平成统一的展示模型。
    /// <para>
    /// 取值顺序**逐字照抄 Flask 页面资源**的 <c>data = entry.get("data") or entry.get("previous") or {}</c>：
    /// 自动封禁取 <c>data</c>，手动解封取 <c>previous</c>（解封前的封禁快照）。
    /// 因此手动解封条目的 <c>account</c>/<c>unban_at</c> 也来自 <c>previous</c>，
    /// 而不是它自己的顶层字段——这点不看 Flask 源码很容易写反。
    /// </para>
    /// </summary>
    private static BanLogItemDto Normalize(BanLogEntry entry)
    {
        var isAutoBan = string.Equals(entry.Type, "auto_ban", StringComparison.Ordinal);

        // entry.data or entry.previous or {}
        var effective = entry.Data ?? entry.Previous;

        var account = !string.IsNullOrEmpty(effective?.Account) ? effective.Account : entry.Account ?? "";
        var reason = !string.IsNullOrEmpty(effective?.Reason) ? effective.Reason : entry.Reason ?? "";

        // created_at = entry.get("created_at") or data.get("banned_at")
        var createdAt = entry.CreatedAt != 0 ? entry.CreatedAt : effective?.BannedAt ?? 0;
        var unbanAt = effective?.UnbanAt;

        return new BanLogItemDto
        {
            Type = entry.Type,
            TypeLabel = isAutoBan ? "自动封禁" : "手动解封",
            CreatedAt = createdAt,
            CreatedAtHuman = FormatTimestamp(createdAt),
            UnbanAt = unbanAt,
            UnbanAtHuman = unbanAt is null or 0 ? null : FormatTimestamp(unbanAt.Value),
            Reason = reason,
            Account = account
        };
    }

    /// <summary>复刻 Flask 页面的 <c>_fmt_ts</c>：本地时区的 <c>yyyy-MM-dd HH:mm:ss</c>。</summary>
    private static string FormatTimestamp(long unixSeconds)
        => DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private async Task PushBanLogAsync(BanLogEntry entry)
    {
        var json = JsonSerializer.Serialize(entry, RedisJson.TypeInfo<BanLogEntry>());
        await store.ListLeftPushAsync(LoginCacheKeys.BanLogs, json);
        await store.ListTrimAsync(LoginCacheKeys.BanLogs, LoginCacheTtl.BanLogsMaxLength);
    }

    /// <summary>
    /// 解析封禁记录。坏数据不抛异常：Flask 把它塞进 <c>raw</c> 键照常返回，这里保持一致。
    /// </summary>
    private static BanInfo ParseBanInfo(string raw, string fallbackAccount)
        => TryParseBanInfo(raw) ?? new BanInfo { Account = fallbackAccount, Reason = raw };

    private static BanInfo? TryParseBanInfo(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize(raw, RedisJson.TypeInfo<BanInfo>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static BanItemDto ToDto(BanInfo info, long ttl, string key)
        => new()
        {
            Account = info.Account,
            Reason = info.Reason,
            BannedAt = info.BannedAt,
            UnbanAt = info.UnbanAt,
            Ttl = ttl,
            Key = key
        };
}
