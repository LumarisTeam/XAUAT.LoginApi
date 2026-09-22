using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Redis;
using XAUAT.LoginApi.Tests.TestSupport;

namespace XAUAT.LoginApi.Tests.Redis;

/// <summary>
/// 限流与封禁语义。数值与分支全部来自 Flask 的 <c>RedisClient</c> + <c>xauat_login</c>，
/// 这里的断言就是"迁移没有改变行为"的凭据。
/// </summary>
public class BanServiceTests
{
    private const string School = "xauat";
    private const string Username = "alice";
    private const string PasswordHash = "hash-of-password";

    private readonly FakeLoginRedisStore _store = new();
    private readonly BanService _service;

    public BanServiceTests() => _service = new BanService(_store, NullLogger<BanService>.Instance);

    private string AccountKey => LoginCacheKeys.AccountKey(School, Username);

    private static string SerializeBan(BanInfo info)
        => JsonSerializer.Serialize(info, RedisJson.TypeInfo<BanInfo>());

    private static string SerializeLog(BanLogEntry entry)
        => JsonSerializer.Serialize(entry, RedisJson.TypeInfo<BanLogEntry>());

    // ================================================================ 第一级：失败计数

    [Fact]
    public async Task AcquireRateLimitSlot_ShouldAllowFiveAttemptsThenLimit()
    {
        var key = LoginCacheKeys.LoginFailed(Username, PasswordHash);

        for (var attempt = 1; attempt <= BanService.MaxAttempts; attempt++)
        {
            var (limited, count) = await _service.AcquireRateLimitSlotAsync(key);
            Assert.False(limited);
            Assert.Equal(attempt, count);
        }

        // 第 6 次：超限，且要把刚占的名额退回去
        var (limitedNow, reportedCount) = await _service.AcquireRateLimitSlotAsync(key);

        Assert.True(limitedNow);
        Assert.Equal(5, reportedCount);
        Assert.Contains($"DECR {key}", _store.Operations);
    }

    [Fact]
    public async Task AcquireRateLimitSlot_ShouldSetTtlOnlyOnFirstAttempt()
    {
        // 固定窗口：TTL 从第一次失败算起，后续失败不再续期
        var key = LoginCacheKeys.LoginFailed(Username, PasswordHash);

        await _service.AcquireRateLimitSlotAsync(key);
        await _service.AcquireRateLimitSlotAsync(key);
        await _service.AcquireRateLimitSlotAsync(key);

        Assert.Single(_store.Operations, op => op.StartsWith($"EXPIRE {key}", StringComparison.Ordinal));
        Assert.Equal(LoginCacheTtl.LoginFailed, _store.TtlOf(key));
    }

    [Fact]
    public async Task ReleaseRateLimitSlot_ShouldDecrement()
    {
        var key = LoginCacheKeys.LoginFailed(Username, PasswordHash);
        await _service.AcquireRateLimitSlotAsync(key);
        await _service.AcquireRateLimitSlotAsync(key);

        await _service.ReleaseRateLimitSlotAsync(key);

        Assert.Equal("1", _store.Peek(key));
    }

    [Fact]
    public async Task ReleaseRateLimitSlot_ShouldDeleteKeyWhenCountGoesNegative()
    {
        // 成功登录的次数多于失败次数时会减到负数，此时直接把键删掉
        var key = LoginCacheKeys.LoginFailed(Username, PasswordHash);

        await _service.ReleaseRateLimitSlotAsync(key);

        Assert.Contains($"DEL {key}", _store.Operations);
    }

    [Fact]
    public async Task AcquireRateLimitSlot_ShouldNotLimitWhenRedisUnavailable()
    {
        _store.Available = false;

        var (limited, count) = await _service.AcquireRateLimitSlotAsync("any-key");

        Assert.False(limited);
        Assert.Equal(0, count);
        Assert.Empty(_store.Operations);
    }

    // ================================================================ 第二级：封禁

    [Fact]
    public async Task RecordRateLimitAndMaybeBan_ShouldDebounceEventsWithinMinInterval()
    {
        // 两次限流事件间隔不足 20 分钟时不重复计数（Flask 的 min_interval_seconds）
        var (firstBanned, _) = await _service.RecordRateLimitAndMaybeBanAsync(AccountKey);
        var (secondBanned, _) = await _service.RecordRateLimitAndMaybeBanAsync(AccountKey);

        Assert.False(firstBanned);
        Assert.False(secondBanned);

        var zsetKey = LoginCacheKeys.RateLimitEvents(AccountKey);
        var cardinality = await _store.SortedSetCardinalityAsync(zsetKey);
        Assert.Equal(1, cardinality);
    }

    [Fact]
    public async Task RecordRateLimitAndMaybeBan_ShouldBanOnThirdNonDebouncedEvent()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var zsetKey = LoginCacheKeys.RateLimitEvents(AccountKey);

        // 预置两条"间隔足够远"的历史事件，再加本次就凑满 3 次阈值
        _store.SeedSortedSet(zsetKey, now - 3000, now - 1500);

        var (banned, info) = await _service.RecordRateLimitAndMaybeBanAsync(AccountKey, "连续触发登录限流");

        Assert.True(banned);
        Assert.NotNull(info);
        Assert.Equal(AccountKey, info!.Account);
        Assert.Equal("连续触发登录限流", info.Reason);
        Assert.Equal(LoginCacheTtl.Ban.TotalSeconds, info.UnbanAt - info.BannedAt);

        var banKey = LoginCacheKeys.Ban(AccountKey);
        Assert.True(_store.ContainsKey(banKey));
        Assert.Equal(LoginCacheTtl.Ban, _store.TtlOf(banKey));
        Assert.Equal(LoginCacheTtl.RateLimitEvents, _store.TtlOf(zsetKey));

        // 要留一条自动封禁日志
        var logs = await _store.ListRangeAsync(LoginCacheKeys.BanLogs, 10);
        Assert.Single(logs);
        Assert.Contains("auto_ban", logs[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordRateLimitAndMaybeBan_ShouldReuseExistingBanWithoutDuplicateLog()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var zsetKey = LoginCacheKeys.RateLimitEvents(AccountKey);
        _store.SeedSortedSet(zsetKey, now - 3000, now - 1500);
        _store.SeedString(LoginCacheKeys.Ban(AccountKey), SerializeBan(new BanInfo
        {
            Account = AccountKey, Reason = "既有封禁", BannedAt = now - 100, UnbanAt = now + 100
        }));

        var (banned, info) = await _service.RecordRateLimitAndMaybeBanAsync(AccountKey);

        Assert.True(banned);
        Assert.Equal("既有封禁", info!.Reason);
        // 已在封禁中就不该再写一条日志
        Assert.Empty(await _store.ListRangeAsync(LoginCacheKeys.BanLogs, 10));
    }

    [Fact]
    public async Task GetBanStatus_ShouldReturnInfoAndRemainingTtl()
    {
        var banKey = LoginCacheKeys.Ban(AccountKey);
        _store.SeedString(banKey, SerializeBan(new BanInfo
        {
            Account = AccountKey, Reason = "测试", BannedAt = 100, UnbanAt = 200
        }), LoginCacheTtl.Ban);

        var status = await _service.GetBanStatusAsync(AccountKey);

        Assert.NotNull(status);
        Assert.Equal("测试", status!.Value.Info.Reason);
        Assert.Equal(LoginCacheTtl.Ban.TotalSeconds, status.Value.Ttl);
    }

    [Fact]
    public async Task GetBanStatus_ShouldReturnNullWhenNotBanned()
    {
        Assert.Null(await _service.GetBanStatusAsync(AccountKey));
    }

    [Fact]
    public async Task FindActiveBans_ShouldCoverBothNewAndLegacyKeyShapes()
    {
        // 灰度期间 Redis 里两种键并存：新的两段式 + Flask 写的三段式
        _store.SeedString(LoginCacheKeys.Ban(AccountKey), SerializeBan(new BanInfo
        {
            Account = AccountKey, Reason = "新键", BannedAt = 1, UnbanAt = 2
        }), LoginCacheTtl.Ban);

        var legacyKey = $"security:ban:{School}:{Username}:{PasswordHash}";
        _store.SeedString(legacyKey, SerializeBan(new BanInfo
        {
            Account = $"{School}:{Username}:{PasswordHash}", Reason = "旧键", BannedAt = 1, UnbanAt = 2
        }), LoginCacheTtl.Ban);

        var bans = await _service.FindActiveBansAsync(School, Username);

        Assert.Equal(2, bans.Count);
        Assert.Contains(bans, ban => ban.Reason == "新键" && ban.Key == LoginCacheKeys.Ban(AccountKey));
        Assert.Contains(bans, ban => ban.Reason == "旧键" && ban.Key == legacyKey);
        Assert.True(await _service.IsBannedAsync(School, Username));
    }

    [Fact]
    public async Task FindActiveBans_ShouldReturnEmptyWhenNothingBanned()
    {
        Assert.Empty(await _service.FindActiveBansAsync(School, Username));
        Assert.False(await _service.IsBannedAsync(School, Username));
    }

    [Fact]
    public async Task UnbanByUsername_ShouldDeleteBothKeyShapesAndLogEachOne()
    {
        _store.SeedString(LoginCacheKeys.Ban(AccountKey), SerializeBan(new BanInfo
        {
            Account = AccountKey, Reason = "新键", BannedAt = 1, UnbanAt = 2
        }), LoginCacheTtl.Ban);

        var legacyKey = $"security:ban:{School}:{Username}:{PasswordHash}";
        _store.SeedString(legacyKey, SerializeBan(new BanInfo
        {
            Account = $"{School}:{Username}:{PasswordHash}", Reason = "旧键", BannedAt = 1, UnbanAt = 2
        }), LoginCacheTtl.Ban);

        var removed = await _service.UnbanByUsernameAsync(School, Username);

        Assert.True(removed);
        Assert.False(_store.ContainsKey(LoginCacheKeys.Ban(AccountKey)));
        Assert.False(_store.ContainsKey(legacyKey));

        var logs = await _store.ListRangeAsync(LoginCacheKeys.BanLogs, 10);
        Assert.Equal(2, logs.Length);
        Assert.All(logs, log => Assert.Contains("manual_unban", log, StringComparison.Ordinal));
        // account 保留 Flask 的"剥离 security:ban: 前缀"行为，因此旧键写成三段式
        Assert.Contains($"{School}:{Username}:{PasswordHash}", logs[0], StringComparison.Ordinal);
        Assert.Contains("手动解封", logs[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnbanByUsername_ShouldReturnFalseWhenNothingToUnban()
    {
        Assert.False(await _service.UnbanByUsernameAsync(School, Username));
    }

    // ================================================================ 封禁日志

    [Fact]
    public async Task ListBanLogs_ShouldNormalizeAutoBanFromDataField()
    {
        _store.SeedList(LoginCacheKeys.BanLogs, SerializeLog(new BanLogEntry
        {
            Type = "auto_ban",
            CreatedAt = 1_700_000_000,
            Data = new BanInfo
            {
                Account = AccountKey, Reason = "连续多次触发登录限流",
                BannedAt = 1_700_000_000, UnbanAt = 1_700_086_400
            }
        }));

        var logs = await _service.ListBanLogsAsync(null, null);

        Assert.Single(logs);
        Assert.Equal("auto_ban", logs[0].Type);
        Assert.Equal("自动封禁", logs[0].TypeLabel);
        Assert.Equal(AccountKey, logs[0].Account);
        Assert.Equal("连续多次触发登录限流", logs[0].Reason);
        Assert.Equal(1_700_086_400, logs[0].UnbanAt);
        Assert.NotNull(logs[0].UnbanAtHuman);
    }

    [Fact]
    public async Task ListBanLogs_ShouldNormalizeManualUnbanFromPreviousField()
    {
        // Flask 页面逻辑是 data = entry.data or entry.previous or {}，
        // 因此手动解封条目的 account/unban_at 取自 previous，而不是它自己的顶层字段。
        // 这点不看源码很容易写反。
        _store.SeedList(LoginCacheKeys.BanLogs, SerializeLog(new BanLogEntry
        {
            Type = "manual_unban",
            CreatedAt = 1_700_000_100,
            Account = $"{School}:{Username}:{PasswordHash}",
            Reason = "手动解封",
            Previous = new BanInfo
            {
                Account = $"{School}:{Username}:{PasswordHash}",
                Reason = "连续多次触发登录限流",
                BannedAt = 1_700_000_000,
                UnbanAt = 1_700_086_400
            }
        }));

        var logs = await _service.ListBanLogsAsync(null, null);

        Assert.Single(logs);
        Assert.Equal("手动解封", logs[0].TypeLabel);
        Assert.Equal($"{School}:{Username}:{PasswordHash}", logs[0].Account);
        Assert.Equal(1_700_086_400, logs[0].UnbanAt);
    }

    [Fact]
    public async Task ListBanLogs_ShouldMatchUsernameInBothKeyShapes()
    {
        _store.SeedList(LoginCacheKeys.BanLogs,
            SerializeLog(new BanLogEntry
            {
                Type = "auto_ban", CreatedAt = 1,
                Data = new BanInfo { Account = $"{School}:{Username}", Reason = "r", BannedAt = 1, UnbanAt = 2 }
            }),
            SerializeLog(new BanLogEntry
            {
                Type = "auto_ban", CreatedAt = 1,
                Data = new BanInfo
                {
                    Account = $"{School}:{Username}:{PasswordHash}", Reason = "r", BannedAt = 1, UnbanAt = 2
                }
            }),
            SerializeLog(new BanLogEntry
            {
                Type = "auto_ban", CreatedAt = 1,
                Data = new BanInfo { Account = $"{School}:bob", Reason = "r", BannedAt = 1, UnbanAt = 2 }
            }));

        // 新键是结尾匹配（xauat:alice），旧键是子串匹配（xauat:alice:hash）——
        // Flask 只做 :username: 子串匹配，因此会漏掉新键，这里必须两者兼顾
        var logs = await _service.ListBanLogsAsync(null, Username);

        Assert.Equal(2, logs.Count);
        Assert.All(logs, log => Assert.Contains(Username, log.Account, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListBanLogs_ShouldFilterBySchoolPrefix()
    {
        _store.SeedList(LoginCacheKeys.BanLogs,
            SerializeLog(new BanLogEntry
            {
                Type = "auto_ban", CreatedAt = 1,
                Data = new BanInfo { Account = "xauat:alice", Reason = "r", BannedAt = 1, UnbanAt = 2 }
            }),
            SerializeLog(new BanLogEntry
            {
                Type = "auto_ban", CreatedAt = 1,
                Data = new BanInfo { Account = "nwafu:alice", Reason = "r", BannedAt = 1, UnbanAt = 2 }
            }));

        Assert.Single(await _service.ListBanLogsAsync(School, null));
    }

    [Fact]
    public async Task ListBanLogs_ShouldSkipCorruptEntriesWithoutFailingTheWholeQuery()
    {
        _store.SeedList(LoginCacheKeys.BanLogs, "{ 这不是 JSON", SerializeLog(new BanLogEntry
        {
            Type = "auto_ban", CreatedAt = 1,
            Data = new BanInfo { Account = AccountKey, Reason = "ok", BannedAt = 1, UnbanAt = 2 }
        }));

        var logs = await _service.ListBanLogsAsync(null, null);

        Assert.Single(logs);
        Assert.Equal("ok", logs[0].Reason);
    }

    [Fact]
    public async Task StoredValues_ShouldUseRawUtf8LikeFlaskEnsureAsciiFalse()
    {
        // Flask 用 json.dumps(..., ensure_ascii=False)，中文在 Redis 里是原始 UTF-8 而非 \uXXXX。
        // 转义写法两边都读得懂，但既然要与 Flask 共用同一批键，字节形态也应当一致，
        // 否则 redis-cli 里看到的值与 Flask 写的对不上，排查时容易误判。
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _store.SeedSortedSet(LoginCacheKeys.RateLimitEvents(AccountKey), now - 3000, now - 1500);

        await _service.RecordRateLimitAndMaybeBanAsync(AccountKey, "连续触发登录限流");

        var storedBan = _store.Peek(LoginCacheKeys.Ban(AccountKey));
        Assert.NotNull(storedBan);
        Assert.Contains("连续触发登录限流", storedBan!, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", storedBan!, StringComparison.Ordinal);

        var logs = await _store.ListRangeAsync(LoginCacheKeys.BanLogs, 10);
        Assert.Contains("连续触发登录限流", logs[0], StringComparison.Ordinal);
        Assert.DoesNotContain("\\u", logs[0], StringComparison.Ordinal);

        // snake_case 的键名同样是存储契约
        Assert.Contains("banned_at", storedBan!, StringComparison.Ordinal);
        Assert.Contains("unban_at", storedBan!, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyConstants_ShouldMatchFlask()
    {
        Assert.Equal(5, BanService.MaxAttempts);
        Assert.Equal(1200, BanService.WindowSeconds);
        Assert.Equal(3660, BanService.BanWindowSeconds);
        Assert.Equal(3, BanService.BanThreshold);
        Assert.Equal(86400, BanService.BanSeconds);
        Assert.Equal(1200, BanService.MinIntervalSeconds);
    }
}
