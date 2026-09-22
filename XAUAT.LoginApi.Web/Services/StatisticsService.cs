using XAUAT.LoginApi.Redis;

namespace XAUAT.LoginApi.Services;

/// <summary>
/// 活跃用户统计。
/// <para>
/// <b>语义</b>：「活跃用户数」= 当前存活的 <c>sso-cookies-*</c> 键数量。它统计的其实是
/// 「<c>(学号, 密码哈希)</c> 组合中持有未过期 SSO 票据的个数」，并不按真实身份去重——
/// 同一个学生换一次密码就会多算一个。这是 Flask 既有的定义，沿用以免口径变化。
/// </para>
/// <para>
/// <b>与 Flask 的实现差异（有意）</b>：Flask 靠 Redis keyspace 通知维护一个
/// <c>user:count:total</c> 计数器，键过期时 DECR。但部署是 <c>gunicorn -w 4</c>，
/// 4 个 worker 各跑一个监听线程，同一个过期事件会把计数扣 4 次，导致系统性偏低；
/// 而且它要执行 <c>CONFIG SET notify-keyspace-events</c>，会改到**整个共享 Redis 实例**的全局配置。
/// 这里改为请求时 SCAN 现算：无副作用、无多进程偏斜、也不会漏事件，代价是每次 O(N) 次扫描
/// （N 为活跃用户数，对本场景可接受）。<c>user:count:total</c> 键因此不再被读写。
/// </para>
/// </summary>
internal sealed class StatisticsService(ILoginRedisStore redis, ILogger<StatisticsService> logger)
{
    public async Task<int> GetUserCountAsync()
    {
        var keys = await redis.ScanKeysAsync($"{LoginCacheKeys.SsoCookiesPrefix}*");
        logger.LogDebug("活跃用户数（SCAN 现算）: {Count}", keys.Count);
        return keys.Count;
    }
}
