namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// 统一认证客户端的接缝。
/// <para>
/// 与 <c>ILoginRedisStore</c> 同理：登录编排的三条分支
/// （缓存命中 / 票据换票 / 完整登录）需要能被独立驱动验证，
/// 而走真实 HTTP 只能覆盖到第一条。
/// </para>
/// </summary>
internal interface IXauatSsoClient
{
    /// <summary>用户名密码登录。</summary>
    Task<LoginToken> LoginAsync(
        string username, string password, XauatCookieJar jar, CancellationToken cancellationToken);

    /// <summary>用 SSO 票据（CASTGC）换取教务系统会话 cookie。</summary>
    Task<LoginToken> ExchangeSsoTicketAsync(
        string ssoCookie, XauatCookieJar jar, CancellationToken cancellationToken);
}
