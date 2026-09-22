using StackExchange.Redis;

namespace XAUAT.LoginApi.Redis;

/// <summary>
/// 直连 <see cref="IDatabase"/> 的薄封装。
/// <para>
/// <b>刻意不套用 PaymentAPI/EduApi 的三级缓存 <c>CacheService</c></b>，原因是键名契约：
/// <c>CacheService</c> 会给键加 <c>{KeyPrefix}:</c> 前缀，并把值包成 <c>CacheItem&lt;T&gt;</c>
/// 的 JSON 信封；而本服务必须原样复用 Flask 的键名与裸值（见 <see cref="LoginCacheKeys"/>），
/// 两者不可兼得。Flask 侧本来也没有本地内存层，这层简化不损失任何既有行为。
/// </para>
/// <para>
/// 降级语义与 Flask 对齐：Redis 不可用时所有方法返回默认值并记日志，登录链路随即走
/// "无缓存、无限流"的全量登录路径（见 <c>AuthService</c>）。所有 Redis 异常都被吞掉——
/// 缓存/限流失败绝不能把登录请求本身打挂。
/// </para>
/// </summary>
internal sealed class LoginRedisStore : ILoginRedisStore
{
    private readonly IConnectionMultiplexer? _multiplexer;
    private readonly IDatabase? _database;
    private readonly ILogger<LoginRedisStore> _logger;

    /// <summary>
    /// 构造函数。用 <see cref="IEnumerable{T}"/> 接收多路复用器是本仓库的既有约定
    /// （PaymentAPI / EduApi 同款）：未注册 Redis 时容器注入空集合，
    /// <see cref="IsAvailable"/> 即为 false，无需在业务代码里到处判空。
    /// </summary>
    public LoginRedisStore(IEnumerable<IConnectionMultiplexer> multiplexers, ILogger<LoginRedisStore> logger)
    {
        _logger = logger;
        _multiplexer = multiplexers.FirstOrDefault();

        if (_multiplexer is null)
        {
            _logger.LogWarning("未配置 Redis：登录将不做缓存、不做限流（与 Flask 的降级行为一致）");
            return;
        }

        try
        {
            _database = _multiplexer.GetDatabase();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取 Redis database 失败，退化为无缓存模式");
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => _database is not null;

    // ---------------------------------------------------------------- 字符串

    public async Task<string?> GetStringAsync(string key)
    {
        if (_database is null) return null;
        try
        {
            var value = await _database.StringGetAsync(key);
            return value.IsNull ? null : (string?)value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 Redis 键 {Key} 失败", key);
            return null;
        }
    }

    public async Task<bool> SetStringAsync(string key, string value, TimeSpan ttl)
    {
        if (_database is null) return false;
        try
        {
            return await _database.StringSetAsync(key, value, ttl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入 Redis 键 {Key} 失败", key);
            return false;
        }
    }

    public async Task<long> IncrementAsync(string key)
    {
        if (_database is null) return 0;
        try
        {
            return await _database.StringIncrementAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "递增 Redis 键 {Key} 失败", key);
            return 0;
        }
    }

    public async Task<long> DecrementAsync(string key)
    {
        if (_database is null) return 0;
        try
        {
            return await _database.StringDecrementAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "递减 Redis 键 {Key} 失败", key);
            return 0;
        }
    }

    public async Task<bool> ExpireAsync(string key, TimeSpan ttl)
    {
        if (_database is null) return false;
        try
        {
            return await _database.KeyExpireAsync(key, ttl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置 Redis 键 {Key} 过期时间失败", key);
            return false;
        }
    }

    /// <summary>剩余 TTL（秒）。-1 表示键存在但无过期时间，-2 表示键不存在——与 Redis TTL 语义一致。</summary>
    public async Task<long> TimeToLiveSecondsAsync(string key)
    {
        if (_database is null) return -2;
        try
        {
            var ttl = await _database.KeyTimeToLiveAsync(key);
            return ttl is null ? -2 : (long)ttl.Value.TotalSeconds;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 Redis 键 {Key} TTL 失败", key);
            return -2;
        }
    }

    public async Task<long> DeleteAsync(params string[] keys)
    {
        if (_database is null) return 0;
        try
        {
            return await _database.KeyDeleteAsync([.. keys.Select(k => (RedisKey)k)]);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除 Redis 键失败");
            return 0;
        }
    }

    // ---------------------------------------------------------------- 有序集合（限流事件）

    /// <summary>剔除 score 落在 <c>[0, maxScore]</c> 的成员，用于滑动窗口裁剪。</summary>
    public async Task<long> SortedSetRemoveRangeByScoreAsync(string key, double maxScore)
    {
        if (_database is null) return 0;
        try
        {
            return await _database.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, maxScore);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "裁剪 ZSET {Key} 失败", key);
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<(string Member, double Score)[]> SortedSetTopWithScoresAsync(string key, long count)
    {
        if (_database is null) return [];
        try
        {
            var entries = await _database.SortedSetRangeByRankWithScoresAsync(key, 0, count - 1, Order.Descending);
            return [.. entries.Select(entry => ((string)entry.Element!, entry.Score))];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 ZSET {Key} 顶部成员失败", key);
            return [];
        }
    }

    public async Task<bool> SortedSetAddAsync(string key, string member, double score)
    {
        if (_database is null) return false;
        try
        {
            return await _database.SortedSetAddAsync(key, member, score);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入 ZSET {Key} 失败", key);
            return false;
        }
    }

    public async Task<long> SortedSetCardinalityAsync(string key)
    {
        if (_database is null) return 0;
        try
        {
            return await _database.SortedSetLengthAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取 ZSET {Key} 基数失败", key);
            return 0;
        }
    }

    // ---------------------------------------------------------------- 列表（封禁日志）

    public async Task<long> ListLeftPushAsync(string key, string value)
    {
        if (_database is null) return 0;
        try
        {
            return await _database.ListLeftPushAsync(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LPUSH {Key} 失败", key);
            return 0;
        }
    }

    /// <summary>
    /// LPUSH 之后裁剪列表长度。
    /// Flask 从不 LTRIM，<c>security:ban_logs</c> 会无限增长；这里补上封顶，
    /// 且每次写入后都裁，保证长度有界。
    /// </summary>
    public async Task ListTrimAsync(string key, long maxLength)
    {
        if (_database is null) return;
        try
        {
            await _database.ListTrimAsync(key, 0, maxLength - 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LTRIM {Key} 失败", key);
        }
    }

    public async Task<string[]> ListRangeAsync(string key, long stop)
    {
        if (_database is null) return [];
        try
        {
            var values = await _database.ListRangeAsync(key, 0, stop);
            return [.. values.Select(v => (string?)v).Where(v => v is not null).Select(v => v!)];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LRANGE {Key} 失败", key);
            return [];
        }
    }

    // ---------------------------------------------------------------- 扫描

    /// <summary>
    /// 按模式扫描所有键（底层是 SCAN，非 KEYS，不会阻塞 Redis）。
    /// <para>
    /// 返回完整列表而非 <c>IAsyncEnumerable</c>：调用方（活跃统计、旧封禁查找）本来就要全量结果，
    /// 且异步迭代器里没法跨 <c>yield</c> 做异常兜底。
    /// </para>
    /// </summary>
    public async Task<List<string>> ScanKeysAsync(string pattern)
    {
        var keys = new List<string>();
        if (_multiplexer is null) return keys;

        try
        {
            foreach (var endpoint in _multiplexer.GetEndPoints())
            {
                var server = _multiplexer.GetServer(endpoint);
                if (!server.IsConnected || server.IsReplica) continue;

                await foreach (var key in server.KeysAsync(pattern: pattern, pageSize: 500))
                {
                    keys.Add((string)key!);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SCAN 模式 {Pattern} 失败", pattern);
        }

        return keys;
    }
}
