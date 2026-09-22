using Microsoft.Extensions.Logging.Abstractions;
using XAUAT.LoginApi.Redis;
using XAUAT.LoginApi.Services;
using XAUAT.LoginApi.Tests.TestSupport;

namespace XAUAT.LoginApi.Tests.Services;

public class StatisticsServiceTests
{
    private readonly FakeLoginRedisStore _store = new();
    private readonly StatisticsService _service;

    public StatisticsServiceTests()
        => _service = new StatisticsService(_store, NullLogger<StatisticsService>.Instance);

    [Fact]
    public async Task GetUserCount_ShouldCountLiveSsoCookieKeys()
    {
        // "活跃用户"的定义沿用 Flask：当前存活的 sso-cookies-* 键数量
        _store.SeedString("sso-cookies-alice-hash1", "CASTGC=1", LoginCacheTtl.SsoCookies);
        _store.SeedString("sso-cookies-bob-hash2", "CASTGC=2", LoginCacheTtl.SsoCookies);
        _store.SeedString("sso-cookies-carol-hash3", "CASTGC=3", LoginCacheTtl.SsoCookies);

        Assert.Equal(3, await _service.GetUserCountAsync());
    }

    [Fact]
    public async Task GetUserCount_ShouldIgnoreUnrelatedKeys()
    {
        _store.SeedString("sso-cookies-alice-hash1", "CASTGC=1", LoginCacheTtl.SsoCookies);
        _store.SeedString("login-cookies-alice-hash1", "__pstsid__=x", LoginCacheTtl.EduCookies);
        _store.SeedString("current_semester", "2025-2026-1", LoginCacheTtl.CurrentSemester);
        _store.SeedString("login-failed-alice-hash1", "2", LoginCacheTtl.LoginFailed);

        Assert.Equal(1, await _service.GetUserCountAsync());
    }

    [Fact]
    public async Task GetUserCount_ShouldNotWriteTheLegacyCounterKey()
    {
        // 改为 SCAN 现算后不应再读写 user:count:total（Flask 的计数器会因多 worker 重复扣减而偏斜）
        await _service.GetUserCountAsync();

        Assert.DoesNotContain(_store.Operations, op => op.Contains("user:count:total", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetUserCount_ShouldReturnZeroWhenRedisUnavailable()
    {
        _store.Available = false;

        Assert.Equal(0, await _service.GetUserCountAsync());
    }

    [Fact]
    public async Task GetUserCount_ShouldReturnZeroOnEmptyStore()
    {
        Assert.Equal(0, await _service.GetUserCountAsync());
    }
}
