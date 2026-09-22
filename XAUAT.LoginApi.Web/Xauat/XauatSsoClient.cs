using System.Net;

namespace XAUAT.LoginApi.Xauat;

/// <summary>
/// XAUAT 统一认证（UAAP/CAS）客户端。
/// <para>
/// 移植自 Flask 的 <c>xauat_sso_login.py</c>。这里有几个**看起来奇怪但必须保留**的行为，
/// 它们全部来自上游，改动会直接导致登录失败：
/// </para>
/// <list type="bullet">
/// <item>认证服务器是<b>明文 HTTP</b>。</item>
/// <item>登录 POST 必须 <c>allow_redirects=False</c>，并且**只从 302 响应本身**取 cookie：
/// Flask 里那段 <c>for resp in response.history</c> 是死代码（不跟随重定向时 history 恒为空），
/// 因此直接登录路径**永远只能拿到 SSO 票据（CASTGC），拿不到教务会话**。
/// 教务会话必须再走一次 <see cref="ExchangeSsoTicketAsync"/> 换票。</item>
/// <item>回 200 时的成功判定是正文里是否出现「退出」或「logout」这种启发式。</item>
/// </list>
/// </summary>
internal sealed class XauatSsoClient(IHttpClientFactory httpClientFactory, ILogger<XauatSsoClient> logger)
    : IXauatSsoClient
{
    /// <summary>不跟随重定向、不管 cookie 的命名客户端（重定向由本类手工处理，见 <see cref="FollowAsync"/>）。</summary>
    public const string ClientName = "XauatAuthClient";

    /// <summary>
    /// 用户名密码登录。返回的 <see cref="LoginToken.SsoCookie"/> 可直接用于换教务会话。
    /// </summary>
    public async Task<LoginToken> LoginAsync(
        string username, string password, XauatCookieJar jar, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            // ---- 第一步：取登录页，拿到 lt / execution / 加密盐
            LoginFormFields fields;
            try
            {
                var loginPageUrl = $"{XauatConstants.AuthServerUrl}?service={XauatConstants.ServiceUrlEncoded}";
                using var pageRequest = new HttpRequestMessage(HttpMethod.Get, loginPageUrl);
                using var pageResponse = await XauatHttpSession.SendAsync(client, pageRequest, jar, cancellationToken);
                var html = await XauatHttpUtils.ReadTextAsync(pageResponse, logger, cancellationToken);
                fields = XauatHtmlParser.ParseLoginPage(html);
            }
            catch (TaskCanceledException)
            {
                // Flask 在 get_login_params 里把它包成普通 Exception，随后被最外层的
                // except Exception 捕获成"登录出错：…"。这里照搬这个链路。
                throw new Exception("获取登录参数超时");
            }
            catch (HttpRequestException)
            {
                throw new Exception("无法连接到认证服务器，请检查网络环境");
            }

            // ---- 第二步：加密密码并提交表单
            var encryptedPassword = PasswordEncryptor.Encrypt(password, fields.EncryptSalt, logger);

            var form = new Dictionary<string, string>
            {
                ["username"] = username,
                ["password"] = encryptedPassword,
                ["lt"] = fields.Lt,
                ["execution"] = fields.Execution,
                ["_eventId"] = fields.EventId,
                ["captcha"] = "",
                ["cllt"] = "userNameLogin",
                ["dllt"] = "generalLogin",
                ["rememberMe"] = "true"
            };

            var postUrl = $"{XauatConstants.AuthServerUrl}?service={XauatConstants.ServiceUrlEncoded}";
            using var postRequest = new HttpRequestMessage(HttpMethod.Post, postUrl)
            {
                Content = new FormUrlEncodedContent(form)
            };

            using var response = await XauatHttpSession.SendAsync(client, postRequest, jar, cancellationToken);

            return await InterpretLoginResponseAsync(response, jar, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return LoginToken.Failed("登录请求超时，无法连接到认证服务器");
        }
        catch (HttpRequestException)
        {
            return LoginToken.Failed("网络连接错误，无法连接到认证服务器");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "登录出错");
            return LoginToken.Failed($"登录出错：{ex.Message}");
        }
    }

    private async Task<LoginToken> InterpretLoginResponseAsync(
        HttpResponseMessage response, XauatCookieJar jar, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;

        if (status is 301 or 302)
        {
            // Flask 的 history 循环在此处是死代码（allow_redirects=False），所以 eduCookie 恒为空；
            // 真正有值的是"本次 302 响应自己种下的 cookie"，即 SSO 票据。
            var ssoCookie = XauatCookieJar.ReadResponseCookies(response);

            return string.IsNullOrEmpty(ssoCookie)
                ? LoginToken.Failed("登录失败：未能获取有效会话")
                : new LoginToken(true, "登录成功", "", ssoCookie);
        }

        var body = await XauatHttpUtils.ReadTextAsync(response, logger, cancellationToken);

        if (status == 200)
        {
            if (body.Contains("退出", StringComparison.Ordinal) ||
                body.Contains("logout", StringComparison.OrdinalIgnoreCase))
            {
                // Flask 此处取的是整个会话的 cookie（self.session.cookies）
                return new LoginToken(true, "登录成功", jar.AllAsString(), "");
            }

            var message = XauatHtmlParser.ParseMessageSpan(body);
            return message is not null
                ? LoginToken.Failed($"登录失败：{message}")
                : LoginToken.Failed("登录失败：用户名或密码错误");
        }

        if (status is 401 or 403)
        {
            return LoginToken.Failed("登录失败：账号或密码错误或无权限");
        }

        var unknownMessage = XauatHtmlParser.ParseMessageSpan(body);
        return unknownMessage is not null
            ? LoginToken.Failed($"登录失败：{unknownMessage}")
            : LoginToken.Failed($"登录失败：未知错误 (HTTP {status})");
    }

    /// <summary>
    /// 用 SSO 票据（CASTGC）换取教务系统会话 cookie。
    /// <para>
    /// 跳转链是：<c>authserver</c>(302，持有 CASTGC) → <c>swjw</c> 校验并种下教务会话(302) → 门户(200)。
    /// 教务 cookie 在**第二跳**种下，所以取的是 <c>history[1]</c>——Flask 正是这么写的。
    /// 这里手工跟随后逐跳记录，比"猜第几跳"更明确，但选取规则保持一致。
    /// </para>
    /// </summary>
    public async Task<LoginToken> ExchangeSsoTicketAsync(
        string ssoCookie, XauatCookieJar jar, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            // 票据归属 authserver，先入罐，首跳才会带上它
            jar.Seed(XauatConstants.AuthServerHost, ssoCookie);

            var url = $"{XauatConstants.AuthServerUrl}?service={XauatConstants.ServiceUrlQuoted}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);

            var responses = await XauatHttpSession.SendFollowingRedirectsAsync(
                client, request, jar, logger, cancellationToken);
            var final = responses[^1];

            var body = await XauatHttpUtils.ReadTextAsync(final, logger, cancellationToken);
            if (final.StatusCode != HttpStatusCode.OK)
            {
                return LoginToken.Failed($"SSO登录失败：{body}");
            }

            // history = 除最后一跳外的所有响应（与 requests 的 response.history 定义一致）
            var history = responses.Take(responses.Count - 1).ToList();
            var eduCookieResponse = history.Count > 1 ? history[1]
                : history.Count == 1 ? history[0]
                : final;

            var eduCookie = XauatCookieJar.ReadResponseCookies(eduCookieResponse);

            logger.LogDebug("SSO 换票完成：跳转链 {HopCount} 跳，取到教务 cookie {CookieCount} 个",
                history.Count, eduCookie.Count(c => c == '='));
            return new LoginToken(true, "登录成功", eduCookie, ssoCookie);
        }
        catch (TaskCanceledException)
        {
            return LoginToken.Failed("SSO登录请求超时，无法连接到认证服务器");
        }
        catch (HttpRequestException)
        {
            return LoginToken.Failed("网络连接错误，无法连接到认证服务器");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SSO 登录出错");
            return LoginToken.Failed($"SSO登录出错：{ex.Message}");
        }
    }

}
