namespace XAUAT.LoginApi.Redis;

/// <summary>
/// Redis 操作的接缝。
/// <para>
/// 抽这层接口纯粹是为了可测：登录编排（<c>AuthService</c>）与限流封禁（<c>BanService</c>）
/// 里有大量"先读缓存、再决定要不要打上游"的分支，这些分支正是最容易写错、
/// 也最需要回归保护的部分；直接依赖具体类就只能去 mock <c>IDatabase</c> + <c>IConnectionMultiplexer</c>，
/// 既啰嗦又测不到真实语义。有了接口，测试可以塞一个几十行的内存实现，
/// 反过来又能像集成测试一样验证完整流程。
/// </para>
/// <para>
/// 刻意不暴露 <c>StackExchange.Redis</c> 的类型（如 <c>RedisValue</c>/<c>SortedSetEntry</c>），
/// 否则内存实现也得拖着整个驱动。
/// </para>
/// </summary>
internal interface ILoginRedisStore
{
    /// <summary>Redis 是否可用。false 时所有方法都是安全的空操作。</summary>
    bool IsAvailable { get; }

    Task<string?> GetStringAsync(string key);

    Task<bool> SetStringAsync(string key, string value, TimeSpan ttl);

    Task<long> IncrementAsync(string key);

    Task<long> DecrementAsync(string key);

    Task<bool> ExpireAsync(string key, TimeSpan ttl);

    /// <summary>剩余 TTL（秒）。-1 表示无过期时间，-2 表示键不存在。</summary>
    Task<long> TimeToLiveSecondsAsync(string key);

    Task<long> DeleteAsync(params string[] keys);

    // ---------- 有序集合（限流事件窗口） ----------

    Task<long> SortedSetRemoveRangeByScoreAsync(string key, double maxScore);

    /// <summary>按 score 倒序取前 <paramref name="count"/> 个成员。</summary>
    Task<(string Member, double Score)[]> SortedSetTopWithScoresAsync(string key, long count);

    Task<bool> SortedSetAddAsync(string key, string member, double score);

    Task<long> SortedSetCardinalityAsync(string key);

    // ---------- 列表（封禁日志） ----------

    Task<long> ListLeftPushAsync(string key, string value);

    Task ListTrimAsync(string key, long maxLength);

    Task<string[]> ListRangeAsync(string key, long stop);

    // ---------- 扫描 ----------

    /// <summary>按模式扫描所有键（底层是 SCAN，非 KEYS）。</summary>
    Task<List<string>> ScanKeysAsync(string pattern);
}
