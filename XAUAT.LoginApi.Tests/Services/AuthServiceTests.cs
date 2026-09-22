using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XAUAT.LoginApi.Configuration;
using XAUAT.LoginApi.Models;
using XAUAT.LoginApi.Redis;
using XAUAT.LoginApi.Services;
using XAUAT.LoginApi.Tests.TestSupport;
using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.Services;

/// <summary>
/// 登录编排的三级优先级：缓存 eduCookie → 缓存 SSO 票据换票 → 完整登录。
/// <para>
/// 这些用例逐条对应 Flask <c>tests/test_login.py</c> 与 <c>tests/test_rate_limit.py</c> 里
/// 已经断言过的行为——也就是说，它们保护的是"迁移没有改变既有语义"。
/// </para>
/// </summary>
public class AuthServiceTests
{
    private const string School = "xauat";
    private const string Username = "alice";
    private const string Password = "my-password";

    private static readonly string PasswordHash = PasswordEncryptor.Sha256Hex(Password);
    private static readonly string EduCookieKey = LoginCacheKeys.EduCookies(Username, PasswordHash);
    private static readonly string SsoCookieKey = LoginCacheKeys.SsoCookies(Username, PasswordHash);
    private static readonly string RateLimitKey = LoginCacheKeys.LoginFailed(Username, PasswordHash);
    private static readonly string AccountKey = LoginCacheKeys.AccountKey(School, Username);

    private readonly FakeLoginRedisStore _store = new();
    private readonly FakeSsoClient _sso = new();
    private readonly BanService _banService;
    private readonly AuthService _service;

    public AuthServiceTests()
    {
        _banService = new BanService(_store, NullLogger<BanService>.Instance);
        _service = new AuthService(
            _sso, _banService, _store, NoOpTestAccountResolver.Instance, NullLogger<AuthService>.Instance);
    }

    private Task<AuthResult> LoginAsync() => _service.LoginAsync(School, Username, Password, CancellationToken.None);

    private void SeedBan(string reason = "连续多次触发登录限流")
    {
        var info = new BanInfo { Account = AccountKey, Reason = reason, BannedAt = 1, UnbanAt = 2 };
        _store.SeedString(LoginCacheKeys.Ban(AccountKey),
            JsonSerializer.Serialize(info, RedisJson.TypeInfo<BanInfo>()), LoginCacheTtl.Ban);
    }

    // ================================================================ 优先级 1：缓存的 eduCookie

    [Fact]
    public async Task Login_ShouldReturnCachedEduCookieWithoutTouchingUpstream()
    {
        _store.SeedString(EduCookieKey, "__pstsid__=cached; SESSION=cached", LoginCacheTtl.EduCookies);

        var result = await LoginAsync();

        Assert.True(result.Success);
        Assert.Equal("__pstsid__=cached; SESSION=cached", result.Cookies);
        Assert.Equal(0, _sso.LoginCallCount);
        Assert.Equal(0, _sso.ExchangeCallCount);
    }

    [Fact]
    public async Task Login_ShouldNotConsumeRateLimitSlotOnCacheHit()
    {
        // Flask 明确断言过这一点：命中缓存不能消耗失败额度，
        // 否则正常用户光是刷新页面就会把自己限流掉。
        _store.SeedString(EduCookieKey, "cached", LoginCacheTtl.EduCookies);

        await LoginAsync();

        Assert.DoesNotContain(_store.Operations, op => op.StartsWith($"INCR {RateLimitKey}", StringComparison.Ordinal));
    }

    // ================================================================ 优先级 2：SSO 票据换票

    [Fact]
    public async Task Login_ShouldExchangeCachedSsoTicketInsteadOfFullLogin()
    {
        _store.SeedString(SsoCookieKey, FakeSsoClient.DefaultSsoCookie, LoginCacheTtl.SsoCookies);

        var result = await LoginAsync();

        Assert.True(result.Success);
        Assert.Equal(FakeSsoClient.DefaultEduCookie, result.Cookies);
        Assert.Equal(1, _sso.ExchangeCallCount);
        Assert.Equal(0, _sso.LoginCallCount);
        // 换到的教务会话要按 20 分钟回填
        Assert.Equal(LoginCacheTtl.EduCookies, _store.TtlOf(EduCookieKey));
        Assert.Equal(FakeSsoClient.DefaultEduCookie, _store.Peek(EduCookieKey));
    }

    [Fact]
    public async Task Login_ShouldFallBackToFullLoginWhenCachedSsoTicketFails()
    {
        // 票据在 7 天 TTL 内也可能被上游吊销，此时必须能落回完整登录而不是直接失败
        _store.SeedString(SsoCookieKey, "CASTGC=revoked", LoginCacheTtl.SsoCookies);
        _sso.ExchangeResult = LoginToken.Failed("SSO登录失败：ticket 无效");

        var result = await LoginAsync();

        Assert.True(result.Success);
        Assert.Equal(1, _sso.LoginCallCount);
        // 换票被调用了两次，且两次的票据不同：第一次用缓存里那张（已失效），
        // 第二次用完整登录刚拿到的新票据。这不是重复劳动，两张票各自都要试。
        Assert.Equal(2, _sso.ExchangeCallCount);
    }

    // ================================================================ 优先级 3：完整登录

    [Fact]
    public async Task Login_ShouldExchangeTicketWhenFullLoginOnlyYieldsSsoCookie()
    {
        // 真实上游就是这样：CAS 的 POST 不跟随重定向，只能拿到 CASTGC
        var result = await LoginAsync();

        Assert.True(result.Success);
        Assert.Equal(FakeSsoClient.DefaultEduCookie, result.Cookies);
        Assert.Equal(1, _sso.LoginCallCount);
        Assert.Equal(1, _sso.ExchangeCallCount);

        // 两种 cookie 各自按对应 TTL 落缓存
        Assert.Equal(LoginCacheTtl.EduCookies, _store.TtlOf(EduCookieKey));
        Assert.Equal(LoginCacheTtl.SsoCookies, _store.TtlOf(SsoCookieKey));
        Assert.Equal(FakeSsoClient.DefaultSsoCookie, _store.Peek(SsoCookieKey));
    }

    [Fact]
    public async Task Login_ShouldNotExchangeWhenFullLoginAlreadyYieldsEduCookie()
    {
        _sso.LoginResult = new LoginToken(true, "登录成功", FakeSsoClient.DefaultEduCookie, "");

        var result = await LoginAsync();

        Assert.True(result.Success);
        Assert.Equal(FakeSsoClient.DefaultEduCookie, result.Cookies);
        Assert.Equal(0, _sso.ExchangeCallCount);
        Assert.False(_store.ContainsKey(SsoCookieKey));
    }

    [Fact]
    public async Task Login_ShouldSurviveWhenTicketExchangeFailsAndReturnRawSsoCookie()
    {
        _sso.ExchangeResult = LoginToken.Failed("SSO登录失败：boom");

        var result = await LoginAsync();

        // 换票失败不算登录失败——退回 SSO 票据（与 Flask 一致），调用方随后会拿到 401
        Assert.True(result.Success);
        Assert.Equal(FakeSsoClient.DefaultSsoCookie, result.Cookies);
        Assert.False(_store.ContainsKey(EduCookieKey));
        Assert.True(_store.ContainsKey(SsoCookieKey));
    }

    [Fact]
    public async Task Login_ShouldNotWriteAnyCacheOnFailure()
    {
        _sso.LoginResult = LoginToken.Failed("登录失败：用户名或密码错误");

        var result = await LoginAsync();

        Assert.False(result.Success);
        Assert.Equal("登录失败：用户名或密码错误", result.Message);
        Assert.DoesNotContain(_store.Operations, op => op.StartsWith("SET ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_ShouldReleaseRateLimitSlotOnSuccess()
    {
        await LoginAsync();

        Assert.Contains($"DECR {RateLimitKey}", _store.Operations);
    }

    // ================================================================ 限流与封禁

    [Fact]
    public async Task Login_ShouldRejectWhenRateLimited()
    {
        _store.SeedString(RateLimitKey, "5");

        var result = await LoginAsync();

        Assert.False(result.Success);
        Assert.False(result.Banned);
        Assert.Equal("登录失败次数过多，请在20分钟后重试", result.Message);
        Assert.Equal(0, _sso.LoginCallCount);
    }

    [Fact]
    public async Task Login_ShouldBanAfterThirdRateLimitEvent()
    {
        // 预置两条历史限流事件，本次超限就成了第 3 次 -> 触发硬封禁
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var zsetKey = LoginCacheKeys.RateLimitEvents(AccountKey);
        _store.SeedSortedSet(zsetKey, now - 3000, now - 1500);
        _store.SeedString(RateLimitKey, "5");

        var result = await LoginAsync();

        Assert.False(result.Success);
        Assert.True(result.Banned);
        Assert.Equal("账户已被暂时封禁，请在24小时后重试或联系管理员", result.Message);
        Assert.NotNull(result.BanReason);
        Assert.NotNull(result.BanUntil);
        Assert.True(_store.ContainsKey(LoginCacheKeys.Ban(AccountKey)));
    }

    [Fact]
    public async Task Login_ShouldRejectBannedAccountBeforeTouchingAnythingElse()
    {
        SeedBan("被管理员封禁");

        var result = await LoginAsync();

        Assert.False(result.Success);
        Assert.True(result.Banned);
        Assert.Equal("账户已被暂时封禁，请稍后重试或联系管理员", result.Message);
        Assert.Equal("被管理员封禁", result.BanReason);
        Assert.Equal(2, result.BanUntil);
        Assert.Equal(0, _sso.LoginCallCount);
        // 封禁检查在最前面：只读封禁键，不读任何 cookie 缓存，也不占限流名额。
        // （用 INCR 判断"没占名额"而不是断言 Operations 为空——读封禁键本身就是 Redis 操作。）
        Assert.DoesNotContain(_store.Operations,
            op => op.Contains("login-cookies", StringComparison.Ordinal)
                  || op.Contains("sso-cookies", StringComparison.Ordinal)
                  || op.StartsWith("INCR ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_ShouldClearUserCacheWhenFailureCountReachesThreshold()
    {
        // 要让流程走到"完整登录"，必须让前两级都落空：
        //   edu 缓存键留空（不预置）
        //   sso 缓存键预置但让换票失败（这样才能顺带断言它也被清掉）
        _store.SeedString(SsoCookieKey, "stale-sso", LoginCacheTtl.SsoCookies);
        _sso.ExchangeResult = LoginToken.Failed("SSO登录失败：票据已失效");
        // 预置 4 次失败，本次预占后 count=5（尚未超限）-> 完整登录失败 -> 达到阈值 -> 清缓存
        _store.SeedString(RateLimitKey, "4");
        _sso.LoginResult = LoginToken.Failed("登录失败：用户名或密码错误");

        var result = await LoginAsync();

        Assert.False(result.Success);
        Assert.Contains($"DEL {EduCookieKey}", _store.Operations);
        Assert.Contains($"DEL {SsoCookieKey}", _store.Operations);
    }

    // ================================================================ 降级与边界

    [Fact]
    public async Task Login_ShouldDegradeGracefullyWhenRedisUnavailable()
    {
        _store.Available = false;

        var result = await LoginAsync();

        // 降级路径：无缓存、无限流、无封禁检查，但登录本身必须走通
        Assert.True(result.Success);
        Assert.Equal(FakeSsoClient.DefaultEduCookie, result.Cookies);
        Assert.Equal(1, _sso.LoginCallCount);
        Assert.Equal(1, _sso.ExchangeCallCount);
        Assert.Empty(_store.Operations);
    }

    [Fact]
    public async Task Login_ShouldStillExchangeTicketOnDegradedPath()
    {
        // 这条是相对 Flask 的**有意修复**：Flask 在 Redis 挂掉时只调 login() 不做换票，
        // 于是会把教务系统不认的 CASTGC 当成功返回，调用方随后必然 401。
        _store.Available = false;

        var result = await LoginAsync();

        Assert.Equal(1, _sso.ExchangeCallCount);
        Assert.Equal(FakeSsoClient.DefaultEduCookie, result.Cookies);
    }

    [Fact]
    public async Task Login_ShouldRejectUnsupportedSchool()
    {
        var result = await _service.LoginAsync("nwafu", Username, Password, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("不支持的学校: nwafu", result.Message);
        Assert.Equal(0, _sso.LoginCallCount);
    }

    [Fact]
    public async Task Login_ShouldAcceptSchoolCodeCaseInsensitively()
    {
        var result = await _service.LoginAsync("XAUAT", Username, Password, CancellationToken.None);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Login_ShouldShortCircuitForTestAccount()
    {
        var resolver = new TestAccountResolver(Options.Create(new TestAccountOptions
        {
            Enabled = true, Username = "tester", Password = "secret", CookieMarker = "marker"
        }));
        var service = new AuthService(
            _sso, _banService, _store, resolver, NullLogger<AuthService>.Instance);

        var result = await service.LoginAsync(School, "tester", "secret", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("__pstsid__=marker; SESSION=marker;", result.Cookies);
        Assert.Equal(0, _sso.LoginCallCount);
        // 测试账号绕过一切，连 Redis 都不该碰
        Assert.Empty(_store.Operations);
    }

    [Fact]
    public async Task Login_ShouldUseHashedPasswordInCacheKeysNotPlaintext()
    {
        await LoginAsync();

        // 缓存键里只能是 SHA-256 摘要，绝不能出现明文口令
        Assert.All(_store.Operations, op =>
            Assert.DoesNotContain(Password, op, StringComparison.Ordinal));
    }
}
