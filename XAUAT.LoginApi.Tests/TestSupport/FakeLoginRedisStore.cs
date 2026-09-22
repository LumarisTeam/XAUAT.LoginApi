using XAUAT.LoginApi.Redis;

namespace XAUAT.LoginApi.Tests.TestSupport;

/// <summary>
/// 内存版 Redis。
/// <para>
/// 之所以手写而不是用 Moq 打桩 <c>IDatabase</c>：登录链路的分支判据是
/// "缓存里有什么"，用一个真能存取的小实现来驱动，可以像集成测试一样跑完整流程，
/// 同时又能精确断言"哪几步 Redis 操作发生了"——后者正是 Flask 测试里
/// "命中缓存不消耗限流额度"这类断言的依据。
/// </para>
/// </summary>
internal sealed class FakeLoginRedisStore : ILoginRedisStore
{
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TimeSpan> _ttls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, double>> _sortedSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _lists = new(StringComparer.Ordinal);

    /// <summary>是否可用。置 false 可模拟 Redis 故障降级。</summary>
    public bool Available { get; set; } = true;

    /// <inheritdoc />
    public bool IsAvailable => Available;

    /// <summary>按发生顺序记录的操作，形如 <c>GET key</c> / <c>SET key ttl=00:20:00</c>。</summary>
    public List<string> Operations { get; } = [];

    /// <summary>某键最后一次写入时使用的 TTL；键不存在返回 null。</summary>
    public TimeSpan? TtlOf(string key) => _ttls.GetValueOrDefault(key);

    /// <summary>直接塞一个字符串键（不含 TTL 语义，仅供 Arrange 用）。</summary>
    public void SeedString(string key, string value, TimeSpan? ttl = null)
    {
        _strings[key] = value;
        if (ttl is not null) _ttls[key] = ttl.Value;
    }

    public void SeedSortedSet(string key, params double[] scores)
    {
        var set = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var score in scores) set[((long)score).ToString()] = score;
        _sortedSets[key] = set;
    }

    public void SeedList(string key, params string[] values)
    {
        _lists[key] = [.. values];
    }

    /// <summary>键是否存在（用于断言缓存是否被写入/清除）。</summary>
    public bool ContainsKey(string key)
        => _strings.ContainsKey(key) || _sortedSets.ContainsKey(key) || _lists.ContainsKey(key);

    public string? Peek(string key) => _strings.GetValueOrDefault(key);

    public Task<string?> GetStringAsync(string key)
    {
        Operations.Add($"GET {key}");
        return Task.FromResult(_strings.GetValueOrDefault(key));
    }

    public Task<bool> SetStringAsync(string key, string value, TimeSpan ttl)
    {
        Operations.Add($"SET {key} ttl={ttl}");
        _strings[key] = value;
        _ttls[key] = ttl;
        return Task.FromResult(true);
    }

    public Task<long> IncrementAsync(string key)
    {
        Operations.Add($"INCR {key}");
        var current = _strings.TryGetValue(key, out var raw) && long.TryParse(raw, out var parsed) ? parsed : 0;
        current++;
        _strings[key] = current.ToString();
        return Task.FromResult(current);
    }

    public Task<long> DecrementAsync(string key)
    {
        Operations.Add($"DECR {key}");
        var current = _strings.TryGetValue(key, out var raw) && long.TryParse(raw, out var parsed) ? parsed : 0;
        current--;
        _strings[key] = current.ToString();
        return Task.FromResult(current);
    }

    public Task<bool> ExpireAsync(string key, TimeSpan ttl)
    {
        Operations.Add($"EXPIRE {key} ttl={ttl}");
        _ttls[key] = ttl;
        return Task.FromResult(true);
    }

    public Task<long> TimeToLiveSecondsAsync(string key)
    {
        Operations.Add($"TTL {key}");
        return Task.FromResult(_ttls.TryGetValue(key, out var ttl) ? (long)ttl.TotalSeconds : -2);
    }

    public Task<long> DeleteAsync(params string[] keys)
    {
        var deleted = 0;
        foreach (var key in keys)
        {
            Operations.Add($"DEL {key}");
            if (_strings.Remove(key)) deleted++;
            _ttls.Remove(key);
            if (_sortedSets.Remove(key)) deleted++;
            if (_lists.Remove(key)) deleted++;
        }

        return Task.FromResult((long)deleted);
    }

    public Task<long> SortedSetRemoveRangeByScoreAsync(string key, double maxScore)
    {
        Operations.Add($"ZREMRANGEBYSCORE {key} <= {maxScore}");
        if (!_sortedSets.TryGetValue(key, out var set)) return Task.FromResult(0L);

        var removed = set.Where(pair => pair.Value <= maxScore).Select(pair => pair.Key).ToList();
        foreach (var member in removed) set.Remove(member);
        return Task.FromResult((long)removed.Count);
    }

    public Task<(string Member, double Score)[]> SortedSetTopWithScoresAsync(string key, long count)
    {
        Operations.Add($"ZREVRANGE {key} 0 {count - 1}");
        if (!_sortedSets.TryGetValue(key, out var set)) return Task.FromResult(Array.Empty<(string, double)>());

        return Task.FromResult(set
            .OrderByDescending(pair => pair.Value)
            .Take((int)count)
            .Select(pair => (pair.Key, pair.Value))
            .ToArray());
    }

    public Task<bool> SortedSetAddAsync(string key, string member, double score)
    {
        Operations.Add($"ZADD {key} {member}={score}");
        if (!_sortedSets.TryGetValue(key, out var set))
        {
            set = new Dictionary<string, double>(StringComparer.Ordinal);
            _sortedSets[key] = set;
        }

        set[member] = score;
        return Task.FromResult(true);
    }

    public Task<long> SortedSetCardinalityAsync(string key)
    {
        Operations.Add($"ZCARD {key}");
        return Task.FromResult(_sortedSets.TryGetValue(key, out var set) ? (long)set.Count : 0L);
    }

    public Task<long> ListLeftPushAsync(string key, string value)
    {
        Operations.Add($"LPUSH {key}");
        if (!_lists.TryGetValue(key, out var list))
        {
            list = [];
            _lists[key] = list;
        }

        list.Insert(0, value);
        return Task.FromResult((long)list.Count);
    }

    public Task ListTrimAsync(string key, long maxLength)
    {
        Operations.Add($"LTRIM {key} 0 {maxLength - 1}");
        if (_lists.TryGetValue(key, out var list) && list.Count > maxLength)
        {
            list.RemoveRange((int)maxLength, list.Count - (int)maxLength);
        }

        return Task.CompletedTask;
    }

    public Task<string[]> ListRangeAsync(string key, long stop)
    {
        Operations.Add($"LRANGE {key} 0 {stop}");
        if (!_lists.TryGetValue(key, out var list)) return Task.FromResult(Array.Empty<string>());

        return Task.FromResult(list.Take((int)stop + 1).ToArray());
    }

    public Task<List<string>> ScanKeysAsync(string pattern)
    {
        Operations.Add($"SCAN {pattern}");
        var prefix = pattern.EndsWith('*') ? pattern[..^1] : pattern;

        var matches = _strings.Keys
            .Concat(_sortedSets.Keys)
            .Concat(_lists.Keys)
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return Task.FromResult(matches);
    }
}
