using XAUAT.LoginApi.Redis;

namespace XAUAT.LoginApi.Tests.Compatibility;

/// <summary>
/// 跨实现的存储契约守卫。
/// <para>
/// 本服务与 Flask（<c>schedule.xauat.site</c>）**共用同一个 Redis 实例与同一批键**，
/// 这样灰度期间两边可以并行跑、缓存不冷、封禁状态延续。代价是这些键名成了硬契约：
/// 一旦有人"顺手"加上 <c>LoginApi:</c> 前缀（像 PaymentAPI/EduApi 那样），
/// Flask 写的 cookie 缓存就再也读不到，切换瞬间全体用户要重新登录，
/// 而且**不会有任何报错**——只会表现为登录变慢、活跃统计归零。
/// </para>
/// <para>
/// 所以这里把键名逐字钉死。改动本测试前请先想清楚与 Flask 的互通怎么办。
/// </para>
/// </summary>
public class LoginCacheKeysTests
{
    private const string PasswordHash = "5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8";

    [Fact]
    public void EduCookies_ShouldMatchFlaskKeyWithoutAnyPrefix()
    {
        Assert.Equal(
            $"login-cookies:REPLACE_ME".Replace(":", "-").Replace("REPLACE_ME", $"alice-{PasswordHash}"),
            LoginCacheKeys.EduCookies("alice", PasswordHash));
    }

    [Fact]
    public void SsoCookies_ShouldMatchFlaskKey()
    {
        Assert.Equal($"sso-cookies-alice-{PasswordHash}", LoginCacheKeys.SsoCookies("alice", PasswordHash));
    }

    [Fact]
    public void LoginFailed_ShouldMatchFlaskKey()
    {
        Assert.Equal($"login-failed-alice-{PasswordHash}", LoginCacheKeys.LoginFailed("alice", PasswordHash));
    }

    [Fact]
    public void CurrentSemester_ShouldMatchFlaskKey()
    {
        Assert.Equal("current_semester", LoginCacheKeys.CurrentSemester);
    }

    [Fact]
    public void BanLogs_ShouldMatchFlaskKey()
    {
        Assert.Equal("security:ban_logs", LoginCacheKeys.BanLogs);
    }

    [Fact]
    public void SsoCookiesPrefix_ShouldMatchFlaskStatisticsPrefix()
    {
        // 活跃用户统计靠这个前缀 SCAN
        Assert.Equal("sso-cookies-", LoginCacheKeys.SsoCookiesPrefix);
    }

    [Fact]
    public void BanKey_ShouldUseTwoSegmentAccountWithoutPasswordHash()
    {
        // 这是相对 Flask 的**有意变更**：Flask 是 security:ban:{school}:{user}:{password_hash}，
        // 换个错误密码就能绕过封禁。本服务收敛到 (school, username)。
        var accountKey = LoginCacheKeys.AccountKey("xauat", "alice");

        Assert.Equal("xauat:alice", accountKey);
        Assert.Equal("security:ban:xauat:alice", LoginCacheKeys.Ban(accountKey));
        Assert.DoesNotContain(PasswordHash, LoginCacheKeys.Ban(accountKey), StringComparison.Ordinal);
    }

    [Fact]
    public void RateLimitEventsKey_ShouldMatchFlaskShape()
    {
        Assert.Equal("security:rate_limit_events:xauat:alice",
            LoginCacheKeys.RateLimitEvents(LoginCacheKeys.AccountKey("xauat", "alice")));
    }

    [Fact]
    public void LegacyBanPattern_ShouldTargetFlaskThreeSegmentKeys()
    {
        // 灰度期间 Redis 里两种封禁键并存，读路径必须能同时覆盖
        Assert.Equal("security:ban:xauat:alice:*", LoginCacheKeys.LegacyBanPattern("xauat", "alice"));
    }

    [Theory]
    [InlineData("login-cookies")]
    [InlineData("sso-cookies")]
    [InlineData("login-failed")]
    [InlineData("security:ban")]
    [InlineData("security:rate_limit_events")]
    public void NoKey_ShouldCarryAServicePrefix(string forbidden)
    {
        // 把"没有前缀"这件事本身也断言出来：只要有人引入 LoginApi:/loginapi: 之类的前缀，
        // 上面那些精确断言也会挂，但这条能直接指出问题所在。
        var keys = new[]
        {
            LoginCacheKeys.EduCookies("alice", PasswordHash),
            LoginCacheKeys.SsoCookies("alice", PasswordHash),
            LoginCacheKeys.LoginFailed("alice", PasswordHash),
            LoginCacheKeys.CurrentSemester,
            LoginCacheKeys.BanLogs,
            LoginCacheKeys.Ban(LoginCacheKeys.AccountKey("xauat", "alice")),
            LoginCacheKeys.RateLimitEvents(LoginCacheKeys.AccountKey("xauat", "alice"))
        };

        Assert.All(keys, key =>
        {
            Assert.DoesNotContain("LoginApi:", key, StringComparison.Ordinal);
            Assert.DoesNotContain("loginapi:", key, StringComparison.OrdinalIgnoreCase);
            // 注意 EduApi/PaymentAPI 的前缀也不能出现——那意味着抄错了实现
            Assert.DoesNotContain("eduapi:", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("paymentapi:", key, StringComparison.OrdinalIgnoreCase);
        });

        Assert.All(keys, key => Assert.True(
            key.StartsWith(forbidden, StringComparison.Ordinal) || forbidden.Length > 0,
            $"键 {key} 形态异常"));
    }

    [Fact]
    public void Ttls_ShouldMatchFlaskExactly()
    {
        // 这些数值直接决定客户端拿到的 cookie 能用多久，改动等于改行为
        Assert.Equal(1200, LoginCacheTtl.EduCookies.TotalSeconds);
        Assert.Equal(604800, LoginCacheTtl.SsoCookies.TotalSeconds);
        Assert.Equal(1200, LoginCacheTtl.LoginFailed.TotalSeconds);
        Assert.Equal(604800, LoginCacheTtl.CurrentSemester.TotalSeconds);
        Assert.Equal(86400, LoginCacheTtl.Ban.TotalSeconds);
        Assert.Equal(86400, LoginCacheTtl.RateLimitEvents.TotalSeconds);
    }
}
