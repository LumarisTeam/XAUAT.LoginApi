using XAUAT.LoginApi.Xauat;

namespace XAUAT.LoginApi.Tests.TestSupport;

/// <summary>
/// 可编排的统一认证客户端替身。
/// <para>
/// 默认行为是"完整登录只返回 SSO 票据、换票返回教务会话"——这正是真实上游的行为
/// （CAS 的 POST 不跟随重定向，因此拿不到教务 cookie），
/// 让测试默认就跑在最接近生产的路径上。
/// </para>
/// </summary>
internal sealed class FakeSsoClient : IXauatSsoClient
{
    public const string DefaultEduCookie = "__pstsid__=abc123; SESSION=def456";
    public const string DefaultSsoCookie = "CASTGC=TGT-12345";

    public int LoginCallCount { get; private set; }
    public int ExchangeCallCount { get; private set; }

    /// <summary>登录结果。默认：只拿到 SSO 票据。</summary>
    public LoginToken LoginResult { get; set; } = new(true, "登录成功", "", DefaultSsoCookie);

    /// <summary>换票结果。默认：拿到教务会话。</summary>
    public LoginToken ExchangeResult { get; set; } = new(true, "登录成功", DefaultEduCookie, DefaultSsoCookie);

    /// <summary>登录时收到的用户名/密码，用于断言透传正确。</summary>
    public (string Username, string Password)? LastLoginCredentials { get; private set; }

    public Task<LoginToken> LoginAsync(
        string username, string password, XauatCookieJar jar, CancellationToken cancellationToken)
    {
        LoginCallCount++;
        LastLoginCredentials = (username, password);
        return Task.FromResult(LoginResult);
    }

    public Task<LoginToken> ExchangeSsoTicketAsync(
        string ssoCookie, XauatCookieJar jar, CancellationToken cancellationToken)
    {
        ExchangeCallCount++;
        return Task.FromResult(ExchangeResult);
    }
}
